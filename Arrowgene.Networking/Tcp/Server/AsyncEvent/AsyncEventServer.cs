using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    /*
     * I found that the AsyncTcpSession.SendInternal, AsyncTcpSession.
     * StartReceive and AsyncTcpSession.SetBuffer can be using the same instance of SocketAsyncEventArgs without locking.
     * However, the SocketAsyncEventArgs internally excludes data-sending, data-receiving and buffer-setting operations mutually.
     * Those three operations can not proceed at the same time.
     * Consequently, the SetBuffer method can fail when the Socket is using the SocketAsyncEventArgs instance to receive or send data and
     * therefore the InvalidOperationException is thrown. Thread synchronization shall be deployed to avoid the above problem.
     */

    /// <summary>
    /// Implementation of socket server using AsyncEventArgs.
    /// 
    /// - Preallocate socket/runtime objects and reuse client/event-arg instances.
    /// - Support UnitOfOrder -> Enables to simultaneously process tcp packages and preserving order.
    /// - Support SocketTimeout -> Prevent half-open connections by closing the socket if last recv/send time is larger.
    /// </summary>
    public class AsyncEventServer : TcpServer
    {
        private const string ServerThreadName = "AsyncEventServer";
        private const string TimeoutThreadName = "AsyncEventServer_Timeout";
        private const int ThreadTimeoutMs = 10000;
        private const int NumAccepts = 10;
        private const int MinSocketTimeoutDelayMs = 30000;

        private static readonly ILogger Logger = LogProvider.Logger(typeof(AsyncEventServer));


        private readonly int[] _clientGenerations; // track current client generations

        private readonly byte[] _buffer; // pinned continues buffer for SAEA operations
        private readonly int _bufferSize; // total size of _buffer
        private readonly Stack<int> _freeOffsets; // track which buffer offsets are free to use

        private readonly ConcurrentStack<AsyncEventClient> _clientPool;
        private readonly Stack<SocketAsyncEventArgs> _acceptPool;

        private readonly AsyncEventSettings _settings;
        private readonly TimeSpan _socketTimeout;
        private readonly int[] _unitOfOrders;
        private readonly object _unitOfOrderLock;
        private readonly object _clientPoolLock;
        private readonly object _acceptPoolLock;
        private readonly object _isRunningLock;
        private readonly string _identity;
        private readonly AsyncEventClientList<AsyncEventClient> _clients;

        private SemaphoreSlim _maxNumberAccepts;
        private Thread _serverThread;
        private Thread _timeoutThread;
        private Socket _listenSocket;
        private CancellationTokenSource _cancellation;
        private long _acceptedConnections;
        private int _currentConnections;
        private int _acceptLoopGeneration;
        private volatile bool _isRunning;

        public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer, AsyncEventSettings settings)
            : base(ipAddress, port, consumer)
        {
            settings.Validate();
            _settings = new AsyncEventSettings(settings);
            _clients = new AsyncEventClientList<AsyncEventClient>();
            _socketTimeout = TimeSpan.FromSeconds(_settings.SocketTimeoutSeconds);
            _isRunningLock = new object();
            _unitOfOrderLock = new object();
            _clientPoolLock = new object();
            _acceptPoolLock = new object();
            _identity = "";
            _unitOfOrders = new int[_settings.MaxUnitOfOrder];
            if (!string.IsNullOrEmpty(_settings.Identity))
            {
                _identity = $"[{_settings.Identity}] ";
            }

            _acceptPool = new Stack<SocketAsyncEventArgs>();
            _clientPool = new ConcurrentStack<AsyncEventClient>();

            _clientGenerations = new int[_settings.MaxConnections];
            for (int i = 0; i < _settings.MaxConnections; i++)
            {
                _clientGenerations[i] = 0;
            }

            _bufferSize = _settings.BufferSize * _settings.MaxConnections * 2;
            _buffer = GC.AllocateArray<byte>(_bufferSize, pinned: true);
            _freeOffsets = new Stack<int>();
            for (int i = 0; i < _bufferSize; i += _settings.BufferSize)
                _freeOffsets.Push(i);
        }

        public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer)
            : this(ipAddress, port, consumer, new AsyncEventSettings())
        {
        }

        private void Init()
        {
            _clients.Clear();
            _isRunning = false;
            _acceptedConnections = 0;
            _currentConnections = 0;
            _cancellation = new CancellationTokenSource();
            int generation = Interlocked.Increment(ref _acceptLoopGeneration);

            lock (_clientPoolLock)
            {
                _clientPool.Clear();

                int bufferOffset = 0;
                for (int clientId = 0; clientId < _settings.MaxConnections; clientId++)
                {
                    SocketAsyncEventArgs readEventArgs = new SocketAsyncEventArgs();
                    readEventArgs.Completed += Receive_Completed;
                    readEventArgs.SetBuffer(_buffer, bufferOffset, _settings.BufferSize);
                    bufferOffset += _settings.BufferSize;

                    SocketAsyncEventArgs writeEventArgs = new SocketAsyncEventArgs();
                    writeEventArgs.Completed += Send_Completed;
                    writeEventArgs.SetBuffer(_buffer, bufferOffset, _settings.BufferSize);
                    bufferOffset += _settings.BufferSize;

                    AsyncEventClient client = new AsyncEventClient(
                        clientId,
                        this,
                        readEventArgs,
                        writeEventArgs
                    );
                    _clientPool.Push(client);
                }

                for (int i = 0; i < _settings.MaxConnections; i++)
                {
                    _clientGenerations[i] += 1;
                }
            }

            lock (_acceptPoolLock)
            {
                _acceptPool.Clear();
                _maxNumberAccepts?.Dispose();
                _maxNumberAccepts = new SemaphoreSlim(NumAccepts, NumAccepts);

                for (int i = 0; i < NumAccepts; i++)
                {
                    SocketAsyncEventArgs acceptEventArgs = new SocketAsyncEventArgs();
                    acceptEventArgs.Completed += Accept_Completed;
                    acceptEventArgs.UserToken = generation;
                    _acceptPool.Push(acceptEventArgs);
                }
            }

            lock (_unitOfOrderLock)
            {
                for (int i = 0; i < _unitOfOrders.Length; i++)
                {
                    _unitOfOrders[i] = 0;
                }
            }
        }

        protected override void ServerStart()
        {
            Logger.Info($"{_identity}Starting...");
            lock (_isRunningLock)
            {
                if (_isRunning)
                {
                    Logger.Error($"{_identity}Server already running.");
                    return;
                }

                Init();

                _isRunning = true;
                _serverThread = new Thread(Run);
                _serverThread.Name = $"{_identity}{ServerThreadName}";
                _serverThread.IsBackground = true;
                _serverThread.Start();
            }

            Logger.Info($"{_identity}Started");
        }

        protected override void ServerStop()
        {
            Logger.Info($"{_identity}Stopping...");
            lock (_isRunningLock)
            {
                if (!_isRunning)
                {
                    Logger.Error($"{_identity}Server already stopped.");
                    return;
                }

                Logger.Info($"{_identity}Closing Threads...");
                _isRunning = false;
                _cancellation.Cancel();

                Logger.Info($"{_identity}Shutting down listening socket...");
                Service.CloseSocket(_listenSocket);

                Logger.Info($"{_identity}Shutting down client sockets...");
                List<AsyncEventClient> clients = _clients.Snapshot();
                foreach (AsyncEventClient client in clients)
                {
                    if (client == null)
                    {
                        continue;
                    }

                    DisconnectClient(client);
                }

                Service.JoinThread(_serverThread, ThreadTimeoutMs, Logger);
                Service.JoinThread(_timeoutThread, ThreadTimeoutMs, Logger);
            }

            Logger.Info($"{_identity}Stopped");
        }

        private void Run()
        {
            if (_isRunning && Startup())
            {
                if (_socketTimeout.TotalSeconds > 0)
                {
                    _timeoutThread = new Thread(CheckSocketTimeout);
                    _timeoutThread.Name = $"{_identity}{TimeoutThreadName}";
                    _timeoutThread.IsBackground = true;
                    _timeoutThread.Start();
                }

                StartAccept();
            }
            else
            {
                Logger.Error($"{_identity}Stopping server due to startup failure...");
                Stop();
            }
        }

        private bool Startup()
        {
            IPEndPoint localEndPoint = new IPEndPoint(IpAddress, Port);
            _listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _settings.SocketSettings.ConfigureSocket(_listenSocket, Logger);
            bool success = false;
            int retries = 0;
            CancellationToken cancellationToken = _cancellation.Token;
            while (_isRunning && !success && _settings.Retries >= retries)
            {
                try
                {
                    _listenSocket.Bind(localEndPoint);
                    _listenSocket.Listen(_settings.SocketSettings.Backlog);
                    success = true;
                }
                catch (Exception exception)
                {
                    Logger.Exception(exception);
                    if (exception is SocketException socketException &&
                        socketException.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        Logger.Error(
                            $"{_identity}Address is already in use ({IpAddress}:{Port}), try another IP/Port");
                    }

                    Logger.Error($"{_identity}Retrying in 1 Minute");
                    if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)))
                    {
                        break;
                    }

                    retries++;
                }
            }

            Logger.Info($"{_identity}Startup Result: {success}");
            return success;
        }

        public override void Send(ITcpSocket socket, byte[] data)
        {
            if (socket == null)
            {
                Logger.Error($"{_identity}called send with null-socket");
                return;
            }

            if (!(socket is AsyncEventClient client))
            {
                Logger.Error($"{_identity}called send with wrong instance");
                return;
            }

            Send(client, data);
        }

        public void Send(AsyncEventClient client, byte[] data)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}called send with null client instance");
                return;
            }

            if (data == null || data.Length == 0)
            {
                Logger.Debug($"{_identity}{client.Identity} empty payload, not sending.");
                return;
            }

            if (!_isRunning)
            {
                Logger.Debug($"{_identity}{client.Identity} Server stopped, not sending anymore.");
                return;
            }

            if (!client.IsAlive)
            {
                Logger.Debug($"{_identity}{client.Identity} not alive, not sending.");
                return;
            }

            Socket socket = client.Socket;
            bool isConnected = false;
            if (socket != null)
            {
                try
                {
                    isConnected = socket.Connected;
                }
                catch (ObjectDisposedException)
                {
                    isConnected = false;
                }
            }

            if (!isConnected)
            {
                Logger.Error(
                    $"{_identity}{client.Identity} AsyncEventClient not connected during send, closing socket.");
                DisconnectClient(client);
                return;
            }

            AsyncEventWriteState state = client.WriteState;
            if (state.EnqueueSend(data))
            {
                StartSend(client);
            }
        }

        private void StartAccept()
        {
            CancellationToken cancellationToken = _cancellation.Token;
            int currentAcceptLoopGeneration = Volatile.Read(ref _acceptLoopGeneration);
            while (_isRunning)
            {
                if (currentAcceptLoopGeneration != Volatile.Read(ref _acceptLoopGeneration))
                {
                    Logger.Debug(
                        $"{_identity}StartAccept({currentAcceptLoopGeneration}): stop - Server is restarting, a new generation has started");
                    return;
                }

                try
                {
                    _maxNumberAccepts.Wait(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Logger.Debug(
                        $"{_identity}StartAccept({currentAcceptLoopGeneration}): stop - Server stopped, not accepting new connections anymore.");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    Logger.Debug(
                        $"{_identity}StartAccept({currentAcceptLoopGeneration}): stop - SemaphoreSlim '_maxNumberAccepts' or CancellationToken 'cancellationToken' disposed.");
                    return;
                }

                SocketAsyncEventArgs acceptEventArgs;
                lock (_acceptPoolLock)
                {
                    if (!_acceptPool.TryPop(out acceptEventArgs))
                    {
                        Logger.Error(
                            $"{_identity}StartAccept({currentAcceptLoopGeneration}): fatal - Could not acquire SocketAsyncEventArgs 'acceptEventArgs'.");
                        throw new Exception(
                            "SemaphoreSlim: '_maxNumberAccepts' is out of sync with available SocketAsyncEventArgs in '_acceptPool' ConcurrentStack");
                    }
                }

                bool willRaiseEvent;
                try
                {
                    willRaiseEvent = _listenSocket.AcceptAsync(acceptEventArgs);
                }
                catch (ObjectDisposedException)
                {
                    Logger.Error(
                        $"{_identity}StartAccept({currentAcceptLoopGeneration}): stop - Socket '_listenSocket' is disposed");
                    ReturnAcceptEventArgs(acceptEventArgs);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"{_identity}StartAccept({currentAcceptLoopGeneration}): continue - exception during 'AcceptAsync'");
                    Logger.Exception(ex);
                    ReturnAcceptEventArgs(acceptEventArgs);
                    continue;
                }

                if (!willRaiseEvent)
                {
                    ProcessAccept(acceptEventArgs);
                }
            }
        }

        private void Accept_Completed(object sender, SocketAsyncEventArgs acceptEventArg)
        {
            ProcessAccept(acceptEventArg);
        }

        private void ProcessAccept(SocketAsyncEventArgs acceptEventArg)
        {
            Socket acceptSocket = acceptEventArg.AcceptSocket;
            SocketError socketError = acceptEventArg.SocketError;
            object userToken = acceptEventArg.UserToken;
            ReturnAcceptEventArgs(acceptEventArg);

            string clientIdentity = $"[Unknown Client]";
            if (acceptSocket == null)
            {
                Logger.Error($"{_identity}ProcessAccept - acceptSocket socket is null");
                return;
            }

            if (acceptSocket.RemoteEndPoint is IPEndPoint ipEndPoint)
            {
                clientIdentity = $"[{ipEndPoint.Address}:{ipEndPoint.Port}]";
            }

            if (!(userToken is int acceptGeneration))
            {
                Logger.Error(
                    $"{_identity}{clientIdentity} ProcessAccept - missing generation token on SocketAsyncEventArgs 'acceptEventArg'.");
                Service.CloseSocket(acceptSocket);
                return;
            }

            int currentAcceptLoopGeneration = Volatile.Read(ref _acceptLoopGeneration);
            if (acceptGeneration != currentAcceptLoopGeneration)
            {
                Logger.Error($"{_identity}{clientIdentity} ProcessAccept - generation mismatch");
                Service.CloseSocket(acceptSocket);
                return;
            }

            if (socketError == SocketError.Success)
            {
                AsyncEventClient client;
                lock (_isRunningLock)
                {
                    if (!_isRunning)
                    {
                        Logger.Info(
                            $"{_identity}ProcessAccept - Server stopped, not processing new connections anymore.");
                        Service.CloseSocket(acceptSocket);
                        return;
                    }

                    lock (_clientPoolLock)
                    {
                        if (_currentConnections >= _settings.MaxConnections)
                        {
                            Logger.Error($"{_identity}{clientIdentity} ProcessAccept - max connections exceeded");
                            Service.CloseSocket(acceptSocket);
                            return;
                        }

                        if (!_clientPool.TryPop(out client))
                        {
                            Logger.Error(
                                $"{_identity}{clientIdentity} ProcessAccept - no available client in _clientPool.");
                            Service.CloseSocket(acceptSocket);
                            return;
                        }

                        _currentConnections++;
                    }
                }
                
                int clientGeneration = Interlocked.Increment(ref _clientGenerations[client.ClientId]);
                int unitOfOrder = ClaimUnitOfOrder();
                Logger.Debug(
                    $"{_identity}{clientIdentity} ProcessAccept: accepted - UnitOfOrder: {unitOfOrder} clientGeneration: {clientGeneration}");
                
                client.Open(acceptSocket, unitOfOrder, clientGeneration);
                _clients.Add(client);
                Interlocked.Increment(ref _acceptedConnections);
                Logger.Debug($"{_identity}ProcessAccept - Active Client Connections: _clients.Count:{_clients.Count} _currentConnections:{_currentConnections}");
                Logger.Debug($"{_identity}ProcessAccept - Total Accepted Connections: {_acceptedConnections}");
                try
                {
                    OnClientConnected(new AsyncEventClientHandle(client, clientGeneration));
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"{_identity}{clientIdentity} ProcessAccept - error during OnClientConnected() user code.");
                    Logger.Exception(ex);
                }

                StartReceive(client);
            }
            else
            {
                if (socketError == SocketError.OperationAborted)
                {
                    Logger.Info($"{_identity}{clientIdentity} ProcessAccept - accept socket aborted");
                }
                else
                {
                    Logger.Error($"{_identity}{clientIdentity} ProcessAccept - socket error: {socketError}");
                }
            }
        }

        private void StartReceive(AsyncEventClient client)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}StartReceive - client is null");
                return;
            }

            while (true)
            {
                if (!client.IsAlive)
                {
                    DisconnectClient(client);
                    return;
                }

                Socket socket = client.Socket;
                if (socket == null)
                {
                    DisconnectClient(client);
                    return;
                }

                if (!client.TryBeginIoOperation())
                {
                    DisconnectClient(client);
                    return;
                }

                bool willRaiseEvent;
                try
                {
                    willRaiseEvent = socket.ReceiveAsync(client.ReadEventArgs);
                }
                catch (ObjectDisposedException)
                {
                    CompleteIoOperation(client);
                    Logger.Debug($"{_identity}{client.Identity} StartReceive - client is disposed");
                    DisconnectClient(client);
                    return;
                }
                catch (InvalidOperationException)
                {
                    CompleteIoOperation(client);
                    Logger.Error(
                        $"{_identity}{client.Identity} StartReceive - InvalidOperationException, closing client.");
                    DisconnectClient(client);
                    return;
                }

                if (willRaiseEvent)
                {
                    return;
                }

                if (!ProcessReceive(client, client.ReadEventArgs))
                {
                    return;
                }
            }
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs readEventArgs)
        {
            AsyncEventClient client = readEventArgs.UserToken as AsyncEventClient;
            if (ProcessReceive(client, readEventArgs))
            {
                StartReceive(client);
            }
        }

        private bool ProcessReceive(AsyncEventClient client, SocketAsyncEventArgs readEventArgs)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}ProcessReceive - client is null");
                return false;
            }

            try
            {
                if (!client.IsAlive)
                {
                    DisconnectClient(client);
                    return false;
                }

                if (readEventArgs.BytesTransferred > 0 && readEventArgs.SocketError == SocketError.Success)
                {
                    client.LastRead = DateTime.Now;
                    client.BytesReceived += (ulong)readEventArgs.BytesTransferred;
                    try
                    {
                        OnReceivedData(client, readEventArgs.Buffer, readEventArgs.Offset,
                            readEventArgs.BytesTransferred);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(
                            $"{_identity}{client.Identity} ProcessReceive - error in OnReceivedData() user code.");
                        Logger.Exception(ex);
                    }

                    return client.IsAlive;
                }

                if (readEventArgs.SocketError != SocketError.Success)
                {
                    Logger.Debug(
                        $"{_identity}{client.Identity} ProcessReceive - socket error {readEventArgs.SocketError}");
                }

                if (readEventArgs.BytesTransferred <= 0)
                {
                    Logger.Debug(
                        $"{_identity}{client.Identity} ProcessReceive - no bytes transferred (readEventArgs.BytesTransferred:{readEventArgs.BytesTransferred}), remote most likely closed");
                }

                DisconnectClient(client);
                return false;
            }
            finally
            {
                CompleteIoOperation(client);
            }
        }

        private void StartSend(AsyncEventClient client)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}StartSend - client is null");
                return;
            }

            while (true)
            {
                if (!client.IsAlive)
                {
                    DisconnectClient(client);
                    return;
                }

                Socket socket = client.Socket;
                if (socket == null)
                {
                    DisconnectClient(client);
                    return;
                }

                SocketAsyncEventArgs writeEventArgs = client.WriteEventArgs;
                AsyncEventWriteState state = client.WriteState;
                if (!state.TryGetSendChunk(_settings.BufferSize, out byte[] data, out int dataOffset,
                        out int chunkSize))
                {
                    return;
                }

                if (!client.TryBeginIoOperation())
                {
                    DisconnectClient(client);
                    return;
                }

                writeEventArgs.SetBuffer(writeEventArgs.Offset, chunkSize);
                Buffer.BlockCopy(data, dataOffset, writeEventArgs.Buffer, writeEventArgs.Offset, chunkSize);

                bool willRaiseEvent;
                try
                {
                    willRaiseEvent = socket.SendAsync(writeEventArgs);
                }
                catch (ObjectDisposedException)
                {
                    CompleteIoOperation(client);
                    Logger.Debug($"{_identity}{client.Identity} StartSend - client is disposed");
                    DisconnectClient(client);
                    return;
                }
                catch (InvalidOperationException)
                {
                    CompleteIoOperation(client);
                    Logger.Error(
                        $"{_identity}{client.Identity} StartSend - InvalidOperationException");
                    DisconnectClient(client);
                    return;
                }

                if (willRaiseEvent)
                {
                    return;
                }

                if (!ProcessSend(client, writeEventArgs))
                {
                    return;
                }
            }
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs writeEventArgs)
        {
            AsyncEventClient client = writeEventArgs.UserToken as AsyncEventClient;
            if (ProcessSend(client, writeEventArgs))
            {
                StartSend(client);
            }
        }

        private bool ProcessSend(AsyncEventClient client, SocketAsyncEventArgs writeEventArgs)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}ProcessSend - client is null");
                return false;
            }

            try
            {
                if (!client.IsAlive)
                {
                    DisconnectClient(client);
                    return false;
                }

                AsyncEventWriteState state = client.WriteState;
                if (writeEventArgs.SocketError == SocketError.Success)
                {
                    if (writeEventArgs.BytesTransferred <= 0)
                    {
                        Logger.Debug(
                            $"{_identity}{client.Identity} ProcessSend - no bytes transferred, closing client");
                        DisconnectClient(client);
                        return false;
                    }

                    client.BytesSend += (ulong)writeEventArgs.BytesTransferred;
                    if (state.CompleteSend(writeEventArgs.BytesTransferred))
                    {
                        return client.IsAlive;
                    }

                    client.LastWrite = DateTime.Now;
                    return false;
                }

                Logger.Debug(
                    $"{_identity}{client.Identity} ProcessSend - socket error {writeEventArgs.SocketError}");
                DisconnectClient(client);
                return false;
            }
            finally
            {
                CompleteIoOperation(client);
            }
        }

        private void DisconnectClient(AsyncEventClient client)
        {
            if (client == null)
            {
                Logger.Error($"{_identity}DisconnectClient - client is null");
                return;
            }

            if (!client.TryBeginDisconnect())
            {
                return;
            }

            client.Close();

            FreeUnitOfOrder(client.UnitOfOrder);

            if (!_clients.Remove(client))
            {
                Logger.Error($"{_identity}{client.Identity} NotifyDisconnected - could not remove client from list.");
            }

            TimeSpan duration = DateTime.Now - client.ConnectedAt;
            Logger.Debug(
                $"{_identity}{client.Identity} NotifyDisconnected - {Environment.NewLine}" +
                $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
                $"Total Bytes Received:{client.BytesReceived} ({Service.GetHumanReadableSize(client.BytesReceived)}){Environment.NewLine}" +
                $"Total Bytes Send:{client.BytesSend} ({Service.GetHumanReadableSize(client.BytesSend)}){Environment.NewLine}" +
                $"Current Connections: {_clients.Count}"
            );
            try
            {
                OnClientDisconnected(client);
            }
            catch (Exception ex)
            {
                Logger.Error($"{_identity}{client.Identity} Error during OnClientDisconnected() user code");
                Logger.Exception(ex);
            }

            TryReturnClientToPool(client);
        }

        private void ReturnAcceptEventArgs(SocketAsyncEventArgs acceptEventArgs)
        {
            if (acceptEventArgs == null)
            {
                return;
            }

            acceptEventArgs.AcceptSocket = null;

            lock (_acceptPoolLock)
            {
                if (!(acceptEventArgs.UserToken is int acceptGeneration))
                {
                    return;
                }

                if (acceptGeneration != Volatile.Read(ref _acceptLoopGeneration))
                {
                    return;
                }

                _acceptPool.Push(acceptEventArgs);
                try
                {
                    _maxNumberAccepts.Release();
                }
                catch (ObjectDisposedException)
                {
                    Logger.Debug($"{_identity}ReturnAcceptEventArgs - semaphore disposed.");
                }
                catch (SemaphoreFullException)
                {
                    Logger.Error($"{_identity}ReturnAcceptEventArgs - semaphore release exceeded max count.");
                }
            }
        }

        private void CompleteIoOperation(AsyncEventClient client)
        {
            if (client == null)
            {
                return;
            }

            if (client.CompleteIoOperation())
            {
                TryReturnClientToPool(client);
            }
        }

        private void TryReturnClientToPool(AsyncEventClient client)
        {
            if (client == null)
            {
                return;
            }

            int currentConnections;
            lock (_clientPoolLock)
            {
                if (client.Generation != Volatile.Read(ref _acceptLoopGeneration))
                {
                    return;
                }

                if (!client.TryMarkReturnedToPool())
                {
                    return;
                }

                _clientPool.Push(client);
                if (_currentConnections > 0)
                {
                    _currentConnections--;
                }
                else
                {
                    Logger.Error(
                        $"{_identity}{client.Identity} NotifyDisconnected - _currentConnections already zero.");
                }

                currentConnections = _currentConnections;
            }

            if (_settings.DebugMode)
            {
                Logger.Debug($"{_identity}Current Connections: {currentConnections}");
            }
        }

        private int ClaimUnitOfOrder()
        {
            lock (_unitOfOrderLock)
            {
                int minNumber = int.MaxValue;
                int unitOfOrder = 0;
                for (int i = 0; i < _unitOfOrders.Length; i++)
                {
                    if (_unitOfOrders[i] < minNumber)
                    {
                        minNumber = _unitOfOrders[i];
                        unitOfOrder = i;
                    }
                }

                _unitOfOrders[unitOfOrder]++;
                return unitOfOrder;
            }
        }

        private void FreeUnitOfOrder(int unitOfOrder)
        {
            if (unitOfOrder < 0 || unitOfOrder >= _unitOfOrders.Length)
            {
                Logger.Error($"{_identity}FreeUnitOfOrder - invalid unitOfOrder: {unitOfOrder}");
                return;
            }

            lock (_unitOfOrderLock)
            {
                _unitOfOrders[unitOfOrder]--;
            }
        }

        private void CheckSocketTimeout()
        {
            CancellationToken cancellationToken = _cancellation.Token;
            List<AsyncEventClient> clients = new List<AsyncEventClient>(_settings.MaxConnections);
            while (_isRunning)
            {
                DateTime now = DateTime.Now;
                _clients.SnapshotTo(clients);
                foreach (AsyncEventClient client in clients)
                {
                    if (client == null)
                    {
                        continue;
                    }

                    DateTime lastActive = client.LastWrite > client.LastRead ? client.LastWrite : client.LastRead;

                    TimeSpan elapsedSinceLastActive = now - lastActive;
                    if (elapsedSinceLastActive > _socketTimeout)
                    {
                        Logger.Error(
                            $"{_identity}{client.Identity}) CheckSocketTimeout - client socket timed out after {elapsedSinceLastActive.TotalSeconds} seconds. SocketTimeout: {_socketTimeout.TotalSeconds} LastActive: {lastActive:yyyy-MM-dd HH:mm:ss}");
                        DisconnectClient(client);
                    }
                }

                int timeoutMs = (int)_socketTimeout.TotalMilliseconds;
                if (timeoutMs < MinSocketTimeoutDelayMs)
                {
                    timeoutMs = MinSocketTimeoutDelayMs;
                }

                cancellationToken.WaitHandle.WaitOne(timeoutMs);
            }
        }


        public void SetBuffer(SocketAsyncEventArgs args)
        {
            if (_freeOffsets.TryPop(out int offset))
                args.SetBuffer(_buffer, offset, _bufferSize);
        }

        public void FreeBuffer(SocketAsyncEventArgs args)
        {
            _freeOffsets.Push(args.Offset);
            args.SetBuffer(null, 0, 0);
        }
    }
}