using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// - Preallocate all objects -> No further allocations during runtime.
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
        
        private readonly ConcurrentStack<SocketAsyncEventArgs> _receivePool;
        private readonly ConcurrentStack<SocketAsyncEventArgs> _sendPool;
        private readonly ConcurrentStack<SocketAsyncEventArgs> _acceptPool;
        private readonly SemaphoreSlim _maxNumberAcceptedClients;
        private readonly SemaphoreSlim _maxNumberAccepts;
        private readonly SemaphoreSlim _maxNumberSendOperations;
        private readonly byte[] _buffer;
        private readonly AsyncEventSettings _settings;
        private readonly TimeSpan _socketTimeout;
        private readonly int[] _unitOfOrders;
        private readonly object _unitOfOrderLock;
        private readonly object _isRunningLock;
        private readonly string _identity;
        private readonly AsyncEventClientList<AsyncEventClient> _clients;

        private Thread _serverThread;
        private Thread _timeoutThread;
        private Socket _listenSocket;
        private CancellationTokenSource _cancellation;
        private long _acceptedConnections;
        private volatile bool _isRunning;

        public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer, AsyncEventSettings settings)
            : base(ipAddress, port, consumer)
        {
            _clients = new AsyncEventClientList<AsyncEventClient>();
            _settings = new AsyncEventSettings(settings);
            _socketTimeout = TimeSpan.FromSeconds(_settings.SocketTimeoutSeconds);
            _isRunning = false;
            _isRunningLock = new object();
            _unitOfOrderLock = new object();
            _acceptedConnections = 0;
            _identity = "";
            _unitOfOrders = new int[_settings.MaxUnitOfOrder];
            if (!string.IsNullOrEmpty(_settings.Identity))
            {
                _identity = $"[{_settings.Identity}] ";
            }

            _acceptPool = new ConcurrentStack<SocketAsyncEventArgs>();
            _receivePool = new ConcurrentStack<SocketAsyncEventArgs>();
            _sendPool = new ConcurrentStack<SocketAsyncEventArgs>();

            _maxNumberAccepts = new SemaphoreSlim(NumAccepts, NumAccepts);
            _maxNumberAcceptedClients = new SemaphoreSlim(_settings.MaxConnections, _settings.MaxConnections);
            _maxNumberSendOperations = new SemaphoreSlim(_settings.NumSimultaneouslyWriteOperations,
                _settings.NumSimultaneouslyWriteOperations);

            int bufferSize = _settings.BufferSize * _settings.MaxConnections +
                             _settings.BufferSize * _settings.NumSimultaneouslyWriteOperations;

            _buffer = new byte[bufferSize];
            int bufferOffset = 0;
            for (int i = 0; i < _settings.MaxConnections; i++)
            {
                SocketAsyncEventArgs readEventArgs = new SocketAsyncEventArgs();
                readEventArgs.Completed += Receive_Completed;
                readEventArgs.SetBuffer(_buffer, bufferOffset, _settings.BufferSize);
                _receivePool.Push(readEventArgs);
                bufferOffset += _settings.BufferSize;
            }

            for (int i = 0; i < _settings.NumSimultaneouslyWriteOperations; i++)
            {
                SocketAsyncEventArgs writeEventArgs = new SocketAsyncEventArgs();
                writeEventArgs.Completed += Send_Completed;
                writeEventArgs.UserToken = new AsyncEventToken();
                writeEventArgs.SetBuffer(_buffer, bufferOffset, _settings.BufferSize);
                _sendPool.Push(writeEventArgs);
                bufferOffset += _settings.BufferSize;
            }

            for (int i = 0; i < NumAccepts; i++)
            {
                SocketAsyncEventArgs acceptEventArgs = new SocketAsyncEventArgs();
                acceptEventArgs.Completed += Accept_Completed;
                _acceptPool.Push(acceptEventArgs);
            }
        }

        public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer)
            : this(ipAddress, port, consumer, new AsyncEventSettings())
        {
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
            _maxNumberSendOperations.Wait();
            if (!_isRunning)
            {
                Logger.Debug($"{_identity}{client.Identity} Server stopped, not sending anymore.");
                _maxNumberSendOperations.Release();
                return;
            }

            if (!client.IsAlive)
            {
                Logger.Debug($"{_identity}{client.Identity} not alive, not sending.");
                _maxNumberSendOperations.Release();
                return;
            }

            if (!client.Socket.Connected)
            {
                Logger.Error(
                    $"{_identity}{client.Identity} AsyncEventClient not connected during send, closing socket.");
                client.Close();
                _maxNumberSendOperations.Release();
                return;
            }

            if (!_sendPool.TryPop(out SocketAsyncEventArgs writeEventArgs))
            {
                Logger.Error(
                    $"{_identity}{client.Identity} Could not acquire writeEventArgs, closing socket.");
                client.Close();
                _maxNumberSendOperations.Release();
                return;
            }
            AsyncEventToken token = (AsyncEventToken) writeEventArgs.UserToken;
            token.Assign(client, data);
            client.WaitSend();
            StartSend(writeEventArgs);
        }

        internal void NotifyDisconnected(AsyncEventClient client)
        {
            if (client.ReadEventArgs == null)
            {
                Logger.Error($"{_identity}{client.Identity} NotifyDisconnected - already returned AsyncEventArgs to poll");
                return;
            }

            FreeUnitOfOrder(client.UnitOfOrder);
            ReleaseAccept(client.ReadEventArgs);
            if (!_clients.Remove(client))
            {
                Logger.Error($"{_identity}{client.Identity} NotifyDisconnected - could not remove client from list.");
            }

            if (_settings.DebugMode)
            {
                Logger.Debug($"{_identity}Free Receive: {_maxNumberAcceptedClients.CurrentCount}");
                Logger.Debug($"{_identity}Free Send: {_maxNumberSendOperations.CurrentCount}");
                
                StackTrace stackTrace = new StackTrace(true);
                Logger.Debug($"{_identity}{client.Identity} Stack Trace:{Environment.NewLine}{stackTrace}");
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
        }

        protected override void OnStart()
        {
            Logger.Info($"{_identity}Starting...");
            lock (_isRunningLock)
            {
                if (_isRunning)
                {
                    Logger.Error($"{_identity}Server already running.");
                    return;
                }

                _acceptedConnections = 0;
                _cancellation = new CancellationTokenSource();
                _clients.Clear();

                lock (_unitOfOrderLock)
                {
                    for (int i = 0; i < _unitOfOrders.Length; i++)
                    {
                        _unitOfOrders[i] = 0;
                    }
                }

                _isRunning = true;
                _serverThread = new Thread(Run);
                _serverThread.Name = $"{_identity}{ServerThreadName}";
                _serverThread.IsBackground = true;
                _serverThread.Start();
            }
            Logger.Info($"{_identity}Started");
        }

        protected override void OnStop()
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
                Service.JoinThread(_serverThread, ThreadTimeoutMs, Logger);
                Service.JoinThread(_timeoutThread, ThreadTimeoutMs, Logger);
                
                Logger.Info($"{_identity}Shutting down listening socket...");
                if (_listenSocket != null)
                {
                    _listenSocket.Close();
                }

                Logger.Info($"{_identity}Shutting down client sockets...");
                List<AsyncEventClient> clients = _clients.Snapshot();
                foreach (AsyncEventClient client in clients)
                {
                    if (client == null)
                    {
                        continue;
                    }

                    client.Close();
                }
                
                Logger.Info($"{_identity}Notify shutdown to user code...");
                try
                {
                    OnStopped();
                }
                catch (Exception ex)
                {
                    Logger.Error($"{_identity}Error during OnStopped() user code");
                    Logger.Exception(ex);
                }
            }
            Logger.Info($"{_identity}Stopped");
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
            lock (_unitOfOrderLock)
            {
                _unitOfOrders[unitOfOrder]--;
            }
        }

        private void Run()
        {
            if (_isRunning && Startup())
            {
                try
                {
                    OnStarted();
                }
                catch (Exception ex)
                {
                    Logger.Error($"{_identity}Error during OnStarted() user code");
                    Logger.Exception(ex);
                }

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
                    Thread.Sleep(TimeSpan.FromMinutes(1));
                    retries++;
                }
            }

            Logger.Info($"{_identity}Startup Result: {success}");
            return success;
        }

        private void StartAccept()
        {
            _maxNumberAcceptedClients.Wait();
            _maxNumberAccepts.Wait();

            if (!_isRunning)
            {
                Logger.Debug($"{_identity}Server stopped, not accepting new connections anymore.");
                _maxNumberAccepts.Release();
                _maxNumberAcceptedClients.Release();
                return;
            }

            if (!_acceptPool.TryPop(out SocketAsyncEventArgs acceptEventArgs))
            {
                Logger.Error($"{_identity}Could not acquire acceptEventArgs.");
                _maxNumberAccepts.Release();
                _maxNumberAcceptedClients.Release();
                return;
            }

            bool willRaiseEvent = _listenSocket.AcceptAsync(acceptEventArgs);
            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArgs);
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
            acceptEventArg.AcceptSocket = null;
            _acceptPool.Push(acceptEventArg);
            _maxNumberAccepts.Release();
            StartAccept();
            
            
            string clientIdentity = $"[Unknown Client]";
            if (acceptSocket.RemoteEndPoint is IPEndPoint ipEndPoint)
            {
                clientIdentity = $"[{ipEndPoint.Address}:{ipEndPoint.Port}]";
            }


            if (socketError == SocketError.Success)
            {
                if (!_receivePool.TryPop(out SocketAsyncEventArgs readEventArgs))
                {
                    Logger.Error($"{_identity}{clientIdentity} ProcessAccept - could not acquire readEventArgs");
                    _maxNumberAcceptedClients.Release();
                    return;
                }

                int unitOfOrder = ClaimUnitOfOrder();
                Logger.Debug($"{_identity}{clientIdentity} ProcessAccept - claimed UnitOfOrder: {unitOfOrder}");
                AsyncEventClient client = new AsyncEventClient(
                    acceptSocket,
                    readEventArgs,
                    this,
                    unitOfOrder,
                    _settings.MaxSimultaneousSendsPerClient
                );
                client.ConnectedAt = DateTime.Now;
                _clients.Add(client);
                readEventArgs.UserToken = client;
                Interlocked.Increment(ref _acceptedConnections);
                Logger.Debug($"{_identity}ProcessAccept - Active Client Connections: {_clients.Count}");
                Logger.Debug($"{_identity}ProcessAccept - Total Accepted Connections: {_acceptedConnections}");
                try
                {
                    OnClientConnected(client);
                }
                catch (Exception ex)
                {
                    Logger.Error($"{_identity}{clientIdentity} ProcessAccept - error during OnClientConnected() user code.");
                    Logger.Exception(ex);
                }

                StartReceive(readEventArgs);
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

                _maxNumberAcceptedClients.Release();
            }
        }

        private void StartReceive(SocketAsyncEventArgs readEventArgs)
        {
            AsyncEventClient client = (AsyncEventClient) readEventArgs.UserToken;
            if (client == null)
            {
                Logger.Error($"{_identity}StartReceive - client is null");
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = client.Socket.ReceiveAsync(readEventArgs);
            }
            catch (ObjectDisposedException)
            {
                Logger.Debug($"{_identity}{client.Identity} StartReceive - client is disposed");
                client.Close();
                return;
            }
            catch (InvalidOperationException)
            {
                Logger.Error($"{_identity}{client.Identity} StartReceive - InvalidOperationException, retrying...");
                StartReceive(readEventArgs);
                return;
            }

            if (!willRaiseEvent)
            {
                ProcessReceive(readEventArgs);
            }
        }

        private void Receive_Completed(object sender, SocketAsyncEventArgs readEventArgs)
        {
            ProcessReceive(readEventArgs);
        }

        private void ProcessReceive(SocketAsyncEventArgs readEventArgs)
        {
            AsyncEventClient client = (AsyncEventClient) readEventArgs.UserToken;
            if (client == null)
            {
                Logger.Error($"{_identity}ProcessReceive - client is null");
                return;
            }

            if (readEventArgs.BytesTransferred > 0 && readEventArgs.SocketError == SocketError.Success)
            {
                byte[] data = new byte[readEventArgs.BytesTransferred];
                Buffer.BlockCopy(readEventArgs.Buffer, readEventArgs.Offset, data, 0, readEventArgs.BytesTransferred);
                client.LastRead = DateTime.Now;
                client.BytesReceived += (ulong)data.Length;
                try
                {
                    OnReceivedData(client, data);
                }
                catch (Exception ex)
                {
                    Logger.Error($"{_identity}{client.Identity} ProcessReceive - error in OnReceivedData() user code.");
                    Logger.Exception(ex);
                }

                StartReceive(readEventArgs);
            }
            else
            {
                Logger.Debug($"{_identity}{client.Identity} ProcessReceive - socket error {readEventArgs.SocketError}");
                client.Close();
            }
        }

        private void StartSend(SocketAsyncEventArgs writeEventArgs)
        {
            AsyncEventToken token = (AsyncEventToken) writeEventArgs.UserToken;
            if (token.OutstandingCount <= _settings.BufferSize)
            {
                writeEventArgs.SetBuffer(writeEventArgs.Offset, token.OutstandingCount);
                Buffer.BlockCopy(token.Data, token.TransferredCount, writeEventArgs.Buffer, writeEventArgs.Offset,
                    token.OutstandingCount);
            }
            else
            {
                writeEventArgs.SetBuffer(writeEventArgs.Offset, _settings.BufferSize);
                Buffer.BlockCopy(token.Data, token.TransferredCount, writeEventArgs.Buffer, writeEventArgs.Offset,
                    _settings.BufferSize);
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = token.Client.Socket.SendAsync(writeEventArgs);
            }
            catch (ObjectDisposedException)
            {
                Logger.Debug($"{_identity}{token.Client.Identity} StartSend - client is disposed");
                token.Client.Close();
                ReleaseWrite(writeEventArgs);
                return;
            }
            catch (InvalidOperationException)
            {
                Logger.Error(
                    $"{_identity}{token.Client.Identity} StartSend - InvalidOperationException");
                token.Client.Close();
                ReleaseWrite(writeEventArgs);
                return;
            }

            if (!willRaiseEvent)
            {
                ProcessSend(writeEventArgs);
            }
        }

        private void Send_Completed(object sender, SocketAsyncEventArgs writeEventArgs)
        {
            ProcessSend(writeEventArgs);
        }

        private void ProcessSend(SocketAsyncEventArgs writeEventArgs)
        {
            AsyncEventToken token = (AsyncEventToken) writeEventArgs.UserToken;
            if (writeEventArgs.SocketError == SocketError.Success)
            {
                token.Update(writeEventArgs.BytesTransferred);
                if (token.OutstandingCount == 0)
                {
                    token.Client.LastWrite = DateTime.Now;
                    token.Client.BytesSend += (ulong)token.TransferredCount;
                    ReleaseWrite(writeEventArgs);
                }
                else
                {
                    StartSend(writeEventArgs);
                }
            }
            else
            {
                Logger.Debug($"{_identity}{token.Client.Identity} ProcessSend - socket error {writeEventArgs.SocketError}");
                token.Client.Close();
                ReleaseWrite(writeEventArgs);
            }
        }

        private void ReleaseWrite(SocketAsyncEventArgs writeEventArgs)
        {
            AsyncEventToken token = (AsyncEventToken) writeEventArgs.UserToken;
            token.Client.ReleaseSend();
            token.Reset();
            _sendPool.Push(writeEventArgs);
            _maxNumberSendOperations.Release();
        }

        private void ReleaseAccept(SocketAsyncEventArgs readEventArgs)
        {
            _receivePool.Push(readEventArgs);
            readEventArgs.UserToken = null;
            _maxNumberAcceptedClients.Release();
        }

        private void CheckSocketTimeout()
        {
            CancellationToken cancellationToken = _cancellation.Token;
            while (_isRunning)
            {
                DateTime now = DateTime.Now;
                List<AsyncEventClient> clients = _clients.Snapshot();
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
                        client.Close();
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
    }
}
