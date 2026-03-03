using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

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
public class AsyncEventServer : TcpServer, IDisposable
{
    private const string ServerThreadName = "AsyncEventServer";
    private const string TimeoutThreadName = "AsyncEventServer_Timeout";
    private const int ThreadTimeoutMs = 10000;
    private const int NumAccepts = 10;
    private const int MinSocketTimeoutDelayMs = 30000;

    private static readonly ILogger Logger = LogProvider.Logger(typeof(AsyncEventServer));

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly byte[] _buffer;

    private readonly int[] _clientGenerations;
    private readonly Stack<AsyncEventClient> _clientPool;
    private readonly Stack<SocketAsyncEventArgs> _acceptPool;
    private readonly AsyncEventSettings _settings;
    private readonly TimeSpan _socketTimeout;
    private readonly int _maxConnections;
    private readonly int _bufferSize;
    private readonly int[] _unitOfOrders;
    private readonly object _clientLock;
    private readonly object _acceptPoolLock;
    private readonly object _isRunningLock;
    private readonly string _identity;
    private readonly List<AsyncEventClientHandle> _clientHandles;
    private readonly SemaphoreSlim _maxNumberAccepts;
    private Thread? _serverThread;
    private Thread? _timeoutThread;
    private Socket? _listenSocket;
    private CancellationTokenSource _cancellation;
    private int _runGeneration;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer, AsyncEventSettings settings)
        : base(ipAddress, port, consumer)
    {
        settings.Validate();
        _settings = new AsyncEventSettings(settings);
        _bufferSize = _settings.BufferSize;
        _maxConnections = _settings.MaxConnections;
        int totalBufferSize = _bufferSize * _maxConnections * 2;

        _acceptPool = new Stack<SocketAsyncEventArgs>();
        _acceptPoolLock = new object();
        _buffer = GC.AllocateArray<byte>(totalBufferSize, true);
        _clientGenerations = new int[_maxConnections];
        _clientHandles = new List<AsyncEventClientHandle>();
        _clientLock = new object();
        _clientPool = new Stack<AsyncEventClient>();
        _identity = "";
        _isRunningLock = new object();
        _maxNumberAccepts = new SemaphoreSlim(NumAccepts, NumAccepts);
        _socketTimeout = TimeSpan.FromSeconds(_settings.SocketTimeoutSeconds);
        _unitOfOrders = new int[_settings.MaxUnitOfOrder];
        _cancellation = new CancellationTokenSource();
        _isRunning = false;
        _listenSocket = null;
        _runGeneration = 0;
        _serverThread = null;
        _timeoutThread = null;
        _isDisposed = false;

        for (int i = 0; i < _maxConnections; i++)
        {
            _clientGenerations[i] = 0;
        }

        if (!string.IsNullOrEmpty(_settings.Identity))
        {
            _identity = $"[{_settings.Identity}] ";
        }

        int bufferOffset = 0;
        for (int clientId = 0; clientId < _maxConnections; clientId++)
        {
            SocketAsyncEventArgs readEventArgs = new SocketAsyncEventArgs();
            readEventArgs.Completed += Receive_Completed;
            readEventArgs.SetBuffer(_buffer, bufferOffset, _bufferSize);
            bufferOffset += _bufferSize;

            SocketAsyncEventArgs writeEventArgs = new SocketAsyncEventArgs();
            writeEventArgs.Completed += Send_Completed;
            writeEventArgs.SetBuffer(_buffer, bufferOffset, _bufferSize);
            bufferOffset += _bufferSize;

            AsyncEventClient client = new AsyncEventClient(
                clientId,
                this,
                readEventArgs,
                writeEventArgs
            );
            _clientPool.Push(client);
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

    protected override void ServerStart()
    {
        Log(LogLevel.Info, "ServerStart", "Starting server...");

        lock (_isRunningLock)
        {
            if (_isDisposed)
            {
                Log(LogLevel.Error, "ServerStart", "Server is disposed.");
                return;
            }

            if (_isRunning)
            {
                Log(LogLevel.Error, "ServerStart", "Server already started.");
                return;
            }

            _cancellation = new CancellationTokenSource();
            int runGeneration = Interlocked.Increment(ref _runGeneration);

            lock (_clientLock)
            {
                if (_clientPool.Count != _maxConnections)
                {
                    ThrowInvalidStateException();
                }

                _clientHandles.Clear();
                for (int i = 0; i < _maxConnections; i++)
                {
                    _clientGenerations[i] += 1;
                }

                for (int i = 0; i < _unitOfOrders.Length; i++)
                {
                    _unitOfOrders[i] = 0;
                }
            }

            lock (_acceptPoolLock)
            {
                if (_maxNumberAccepts.CurrentCount != NumAccepts)
                {
                    ThrowInvalidStateException();
                }

                if (_acceptPool.Count != NumAccepts)
                {
                    ThrowInvalidStateException();
                }

                foreach (SocketAsyncEventArgs acceptEventArgs in _acceptPool)
                {
                    acceptEventArgs.UserToken = runGeneration;
                }
            }

            _isRunning = true;
            _serverThread = new Thread(Run)
            {
                Name = $"{_identity}{ServerThreadName}",
                IsBackground = true
            };
            _serverThread.Start();
        }

        Log(LogLevel.Info, "ServerStart", "Server started.");
    }

    protected override void ServerStop()
    {
        Log(LogLevel.Info, "ServerStop", "Stopping server...");
        lock (_isRunningLock)
        {
            if (!_isRunning)
            {
                Log(LogLevel.Error, "ServerStop", "Server already stopped.");
                return;
            }

            Log(LogLevel.Info, "ServerStop", "Closing Threads...");
            _isRunning = false;
            _cancellation.Cancel();
            Interlocked.Increment(ref _runGeneration);

            Log(LogLevel.Info, "ServerStop", "Shutting down listening socket...");
            Service.CloseSocket(_listenSocket);
            _listenSocket = null;

            Log(LogLevel.Info, "ServerStop", "Shutting down client sockets...");

            List<AsyncEventClientHandle> clientHandles = new List<AsyncEventClientHandle>(_maxConnections);
            lock (_clientLock)
            {
                clientHandles.AddRange(_clientHandles);
            }

            foreach (AsyncEventClientHandle clientHandle in clientHandles)
            {
                DisconnectClient(clientHandle);
            }

            Service.JoinThread(_serverThread, ThreadTimeoutMs, Logger);
            _serverThread = null;

            Service.JoinThread(_timeoutThread, ThreadTimeoutMs, Logger);
            _timeoutThread = null;

            _cancellation.Dispose();
        }

        Log(LogLevel.Info, "ServerStop", "Server stopped.");
    }

    private void Run()
    {
        if (_isRunning && Startup())
        {
            if (_socketTimeout.TotalSeconds > 0)
            {
                _timeoutThread = new Thread(CheckSocketTimeout)
                {
                    Name = $"{_identity}{TimeoutThreadName}",
                    IsBackground = true
                };
                _timeoutThread.Start();
            }

            StartAccept();
        }
        else
        {
            Log(LogLevel.Error, "Run", "Stopping server due to startup failure...");
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
                if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                {
                    Log(LogLevel.Error, "Startup",
                        $"Address is already in use ({IpAddress}:{Port}), try another IP/Port.");
                }

                Log(LogLevel.Error, "Startup", "Exception during startup, retrying in 1 minute...");
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)))
                {
                    break;
                }

                retries++;
            }
        }

        Log(LogLevel.Info, "Startup", $"Result: {success}.");
        return success;
    }

    private void StartAccept()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        int runGeneration = Volatile.Read(ref _runGeneration);
        while (_isRunning)
        {
            if (runGeneration != Volatile.Read(ref _runGeneration))
            {
                Log(
                    LogLevel.Debug,
                    $"StartAccept({runGeneration})",
                    "Server is restarting, a new generation has started."
                );
                return;
            }

            try
            {
                _maxNumberAccepts.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log(
                    LogLevel.Debug,
                    $"StartAccept({runGeneration})",
                    "Server stopped, not accepting new connections anymore."
                );
                return;
            }
            catch (ObjectDisposedException)
            {
                Log(
                    LogLevel.Debug,
                    $"StartAccept({runGeneration})",
                    "SemaphoreSlim '_maxNumberAccepts' or CancellationToken 'cancellationToken' disposed."
                );
                return;
            }

            SocketAsyncEventArgs acceptEventArgs;
            lock (_acceptPoolLock)
            {
                if (!_acceptPool.TryPop(out acceptEventArgs!))
                {
                    Log(
                        LogLevel.Error,
                        $"StartAccept({runGeneration})",
                        "Could not acquire SocketAsyncEventArgs 'acceptEventArgs'."
                    );
                    throw new Exception(
                        "SemaphoreSlim: '_maxNumberAccepts' is out of sync with available SocketAsyncEventArgs in '_acceptPool' ConcurrentStack.");
                }
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = _listenSocket!.AcceptAsync(acceptEventArgs);
            }
            catch (ObjectDisposedException)
            {
                Log(LogLevel.Error, $"StartAccept({runGeneration})", "Socket '_listenSocket' is disposed.");
                ReturnAcceptEventArgs(acceptEventArgs);
                return;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"StartAccept({runGeneration})", "Exception during 'AcceptAsync'.");
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

    private void Accept_Completed(object? sender, SocketAsyncEventArgs acceptEventArg)
    {
        ProcessAccept(acceptEventArg);
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArg)
    {
        Socket? acceptSocket = acceptEventArg.AcceptSocket;
        SocketError socketError = acceptEventArg.SocketError;
        object? userToken = acceptEventArg.UserToken;
        ReturnAcceptEventArgs(acceptEventArg);

        string clientIdentity = $"[Unknown Client]";
        if (acceptSocket == null)
        {
            Log(LogLevel.Error, "ProcessAccept", "'acceptSocket' socket is null.");
            return;
        }

        if (acceptSocket.RemoteEndPoint is IPEndPoint ipEndPoint)
        {
            clientIdentity = $"[{ipEndPoint.Address}:{ipEndPoint.Port}]";
        }

        if (!_isRunning)
        {
            Log(LogLevel.Debug, "ProcessAccept", "Server is not running, rejecting accepted socket.", clientIdentity);
            Service.CloseSocket(acceptSocket);
            return;
        }

        if (!(userToken is int runGeneration))
        {
            Log(
                LogLevel.Error,
                "ProcessAccept",
                "Missing generation token on SocketAsyncEventArgs 'acceptEventArg'.",
                clientIdentity
            );
            Service.CloseSocket(acceptSocket);
            return;
        }

        if (runGeneration != Volatile.Read(ref _runGeneration))
        {
            Log(LogLevel.Error,
                "ProcessAccept",
                "Run generation mismatch.",
                clientIdentity
            );
            Service.CloseSocket(acceptSocket);
            return;
        }

        if (socketError != SocketError.Success)
        {
            Log(
                LogLevel.Error,
                "ProcessAccept",
                $"Socket error: {socketError}.",
                clientIdentity
            );
            Service.CloseSocket(acceptSocket);
            return;
        }

        AsyncEventClientHandle clientHandle;
        lock (_clientLock)
        {
            if (!_isRunning)
            {
                Log(LogLevel.Debug, "ProcessAccept", "Server is stopping, rejecting accepted socket.", clientIdentity);
                Service.CloseSocket(acceptSocket);
                return;
            }

            if (_clientHandles.Count >= _maxConnections)
            {
                Log(
                    LogLevel.Error,
                    "ProcessAccept",
                    "Max connections exceeded.",
                    clientIdentity
                );
                Service.CloseSocket(acceptSocket);
                return;
            }

            if (!_clientPool.TryPop(out AsyncEventClient? client))
            {
                Log(
                    LogLevel.Error,
                    "ProcessAccept",
                    "No available client in '_clientPool'.",
                    clientIdentity
                );
                Service.CloseSocket(acceptSocket);
                return;
            }

            // claim unit of order
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
            // end


            int clientGeneration = Interlocked.Increment(ref _clientGenerations[client.ClientId]);
            clientHandle = new AsyncEventClientHandle(client, clientGeneration);

            client.Initialize(acceptSocket, unitOfOrder, runGeneration, clientGeneration, clientHandle);
            _clientHandles.Add(clientHandle);
            Log(
                LogLevel.Info,
                "ProcessAccept",
                $"Active Client Connections: {_clientHandles.Count}.",
                clientIdentity
            );
        }

        try
        {
            OnClientConnected(clientHandle);
        }
        catch (Exception ex)
        {
            Log(
                LogLevel.Error,
                "ProcessAccept",
                "Error during OnClientConnected() user code.",
                clientIdentity
            );
            Logger.Exception(ex);
        }

        StartReceive(clientHandle);
    }

    private void StartReceive(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "StartReceive", "'clientHandle' is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, "StartReceive", "Client is not alive.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        client.IncrementPendingOperations();
        bool willRaiseEvent;
        try
        {
            willRaiseEvent = client.Socket.ReceiveAsync(client.ReadEventArgs);
        }
        catch (ObjectDisposedException)
        {
            client.DecrementPendingOperations();
            Log(LogLevel.Error, "StartReceive", "Client socket is disposed.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }
        catch (InvalidOperationException)
        {
            client.DecrementPendingOperations();
            Log(LogLevel.Error, "StartReceive", "InvalidOperationException, closing client.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        if (!willRaiseEvent)
        {
            try
            {
                ProcessReceive(clientHandle);
            }
            finally
            {
                client.DecrementPendingOperations();
                TryRecycleClient(client);
            }
        }
    }

    private void Receive_Completed(object? sender, SocketAsyncEventArgs readEventArgs)
    {
        if (readEventArgs.UserToken is not AsyncEventClientHandle clientHandle)
        {
            Log(LogLevel.Error, "Receive_Completed", "Unexpected user token.");
            return;
        }

        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "Receive_Completed", "'clientHandle' is stale.");
            return;
        }

        try
        {
            ProcessReceive(clientHandle);
        }
        finally
        {
            client.DecrementPendingOperations();
            TryRecycleClient(client);
        }
    }

    private void ProcessReceive(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "StartReceive", "'clientHandle' is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, "ProcessReceive", "Client is not alive.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        SocketAsyncEventArgs readEventArgs = client.ReadEventArgs;
        if (readEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, "ProcessReceive", $"Socket error {readEventArgs.SocketError}.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        if (readEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error,
                "ProcessReceive",
                $"No bytes transferred (readEventArgs.BytesTransferred:{readEventArgs.BytesTransferred}), remote most likely closed.",
                client.Identity
            );
            DisconnectClient(clientHandle);
            return;
        }

        client.LastReadTicks = Environment.TickCount64;
        client.BytesReceived += (ulong)readEventArgs.BytesTransferred;
        try
        {
            OnReceivedData(clientHandle, readEventArgs.Buffer, readEventArgs.Offset,
                readEventArgs.BytesTransferred);
        }
        catch (Exception ex)
        {
            Log(
                LogLevel.Error,
                "ProcessReceive",
                "Error in OnReceivedData() user code.",
                client.Identity
            );
            Logger.Exception(ex);
        }

        StartReceive(clientHandle);
    }

    public override void Send<T>(T socket, byte[] data)
    {
        if (_isDisposed)
        {
            Log(LogLevel.Error, "Send", "Server is disposed.");
            return;
        }

        if (socket is not AsyncEventClientHandle clientHandle)
        {
            Log(LogLevel.Error, "Send", "ITcpSocket is not a AsyncEventClientHandle instance.");
            return;
        }

        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "Send", "'clientHandle' is stale.");
            return;
        }

        if (data == null || data.Length == 0)
        {
            Log(LogLevel.Error, "Send", "Empty payload, not sending.", client.Identity);
            return;
        }

        AsyncEventWriteState state = client.WriteState;
        bool queueOverflow;
        if (state.EnqueueSend(data, out queueOverflow))
        {
            StartSend(clientHandle);
        }
        else if (queueOverflow)
        {
            Log(LogLevel.Error, "Send", "Write queue overflow, closing client.", client.Identity);
            DisconnectClient(clientHandle);
        }
    }

    private void StartSend(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "StartSend", "'clientHandle' is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, "StartSend", "Client is not alive.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        AsyncEventWriteState state = client.WriteState;
        if (!state.TryGetSendChunk(_bufferSize, out byte[] data, out int dataOffset, out int chunkSize))
        {
            return;
        }

        SocketAsyncEventArgs writeEventArgs = client.WriteEventArgs;
        writeEventArgs.SetBuffer(writeEventArgs.Offset, chunkSize);
        Buffer.BlockCopy(data, dataOffset, writeEventArgs.Buffer, writeEventArgs.Offset, chunkSize);

        client.IncrementPendingOperations();
        bool willRaiseEvent;
        try
        {
            willRaiseEvent = client.Socket.SendAsync(writeEventArgs);
        }
        catch (ObjectDisposedException)
        {
            client.DecrementPendingOperations();
            Log(LogLevel.Error, "StartSend", "Client socket is disposed.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }
        catch (InvalidOperationException)
        {
            client.DecrementPendingOperations();
            Log(LogLevel.Error, "StartSend", "InvalidOperationException.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        if (!willRaiseEvent)
        {
            try
            {
                ProcessSend(clientHandle);
            }
            finally
            {
                client.DecrementPendingOperations();
                TryRecycleClient(client);
            }
        }
    }

    private void Send_Completed(object? sender, SocketAsyncEventArgs writeEventArgs)
    {
        if (writeEventArgs.UserToken is not AsyncEventClientHandle clientHandle)
        {
            Log(LogLevel.Error, "Send_Completed_Completed", "Unexpected user token.");
            return;
        }

        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "Send_Completed", "'clientHandle' is stale.");
            return;
        }

        try
        {
            ProcessSend(clientHandle);
        }
        finally
        {
            client.DecrementPendingOperations();
            TryRecycleClient(client);
        }
    }

    private void ProcessSend(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "ProcessSend", "'clientHandle' is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, "ProcessSend", "Client is not alive.", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        SocketAsyncEventArgs writeEventArgs = client.WriteEventArgs;
        if (writeEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, "ProcessSend", $"Socket error {writeEventArgs.SocketError}", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        if (writeEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error, "ProcessSend", "No bytes transferred, closing client", client.Identity);
            DisconnectClient(clientHandle);
            return;
        }

        AsyncEventWriteState state = client.WriteState;
        client.BytesSend += (ulong)writeEventArgs.BytesTransferred;
        client.LastWriteTicks = Environment.TickCount64;

        if (state.CompleteSend(writeEventArgs.BytesTransferred))
        {
            StartSend(clientHandle);
        }
    }

    private void DisconnectClient(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, "ProcessSend", "'clientHandle' is stale.");
            return;
        }

        if (!client.TryBeginShutdown())
        {
            TryRecycleClient(client);
            return;
        }

        client.Close();

        int currentConnections;
        lock (_clientLock)
        {
            if (!_clientHandles.Remove(clientHandle))
            {
                Log(LogLevel.Error,
                    "DisconnectClient",
                    "Could not remove client from '_clients' list.",
                    client.Identity
                );
            }

            currentConnections = _clientHandles.Count;

            // free unit of order
            int unitOfOrder = client.UnitOfOrder;
            if (unitOfOrder < 0 || unitOfOrder >= _unitOfOrders.Length)
            {
                Log(LogLevel.Error, "FreeUnitOfOrder", $"Unit of order out of range: {unitOfOrder}");
                return;
            }

            _unitOfOrders[unitOfOrder]--;
            // end
        }


        TimeSpan duration = DateTime.Now - client.ConnectedAt;
        Log(
            LogLevel.Debug,
            "DisconnectClient",
            $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
            $"Total Bytes Received:{client.BytesReceived} ({Service.GetHumanReadableSize(client.BytesReceived)}){Environment.NewLine}" +
            $"Total Bytes Send:{client.BytesSend} ({Service.GetHumanReadableSize(client.BytesSend)}){Environment.NewLine}" +
            $"Current Connections: {currentConnections}",
            client.Identity
        );
        try
        {
            OnClientDisconnected(clientHandle);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error,
                "DisconnectClient",
                "Error during OnClientDisconnected() user code",
                client.Identity
            );
            Logger.Exception(ex);
        }

        TryRecycleClient(client);
    }

    private void TryRecycleClient(AsyncEventClient client)
    {
        if (client.IsAlive)
        {
            return;
        }

        if (client.PendingOperations > 0)
        {
            return;
        }

        lock (_clientLock)
        {
            if (client.IsAlive)
            {
                return;
            }

            if (client.PendingOperations > 0)
            {
                return;
            }

            if (!client.TryMarkPooled())
            {
                return;
            }

            _clientPool.Push(client);
        }
    }

    private void ReturnAcceptEventArgs(SocketAsyncEventArgs acceptEventArgs)
    {
        acceptEventArgs.AcceptSocket = null;

        lock (_acceptPoolLock)
        {
            acceptEventArgs.UserToken = Volatile.Read(ref _runGeneration);
            _acceptPool.Push(acceptEventArgs);

            try
            {
                _maxNumberAccepts.Release();
            }
            catch (ObjectDisposedException)
            {
                Log(LogLevel.Error, "ReturnAcceptEventArgs", "Semaphore disposed.");
            }
            catch (SemaphoreFullException)
            {
                Log(LogLevel.Error, "ReturnAcceptEventArgs", "Semaphore release exceeded max count.");
            }
        }
    }

    private void CheckSocketTimeout()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        List<AsyncEventClientHandle> clientHandles = new List<AsyncEventClientHandle>(_maxConnections);
        while (_isRunning)
        {
            long now = Environment.TickCount64;
            clientHandles.Clear();
            lock (_clientLock)
            {
                clientHandles.AddRange(_clientHandles);
            }

            foreach (AsyncEventClientHandle clientHandle in clientHandles)
            {
                if (!clientHandle.TryGetClient(out AsyncEventClient client))
                {
                    continue;
                }

                long lastActiveTicks = client.LastWriteTicks > client.LastReadTicks
                    ? client.LastWriteTicks
                    : client.LastReadTicks;
                long elapsedTicksSinceLastActive = now - lastActiveTicks;
                if (elapsedTicksSinceLastActive > _socketTimeout.TotalMilliseconds)
                {
                    TimeSpan uptime = TimeSpan.FromMilliseconds(now);
                    TimeSpan elapsedSinceLastActive = TimeSpan.FromMilliseconds(elapsedTicksSinceLastActive);
                    DateTime bootTime = DateTime.UtcNow - uptime;
                    DateTime lastActive = bootTime + elapsedSinceLastActive;
                    Log(
                        LogLevel.Error,
                        "CheckSocketTimeout",
                        $"Client socket timed out after {elapsedSinceLastActive.TotalSeconds} seconds. SocketTimeout: {_socketTimeout.TotalSeconds} LastActive: {lastActive:yyyy-MM-dd HH:mm:ss}",
                        client.Identity
                    );
                    DisconnectClient(clientHandle);
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

    public void Dispose()
    {
        bool shouldStop;
        lock (_isRunningLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            shouldStop = _isRunning;
        }

        if (shouldStop)
        {
            Stop();
        }

        WaitForClientPoolDrain();
        WaitForAcceptPoolDrain();

        List<SocketAsyncEventArgs> acceptEventArgsList = new List<SocketAsyncEventArgs>(NumAccepts);
        lock (_acceptPoolLock)
        {
            while (_acceptPool.TryPop(out SocketAsyncEventArgs? acceptEventArgs))
            {
                acceptEventArgsList.Add(acceptEventArgs);
            }
        }

        foreach (SocketAsyncEventArgs acceptEventArgs in acceptEventArgsList)
        {
            acceptEventArgs.Completed -= Accept_Completed;
            acceptEventArgs.Dispose();
        }

        List<AsyncEventClient> clients = new List<AsyncEventClient>(_maxConnections);
        bool[] addedClients = new bool[_maxConnections];
        lock (_clientLock)
        {
            foreach (AsyncEventClientHandle clientHandle in _clientHandles)
            {
                if (!clientHandle.TryGetClient(out AsyncEventClient client))
                {
                    continue;
                }

                if (addedClients[client.ClientId])
                {
                    continue;
                }

                clients.Add(client);
                addedClients[client.ClientId] = true;
            }

            while (_clientPool.TryPop(out AsyncEventClient? pooledClient))
            {
                if (addedClients[pooledClient.ClientId])
                {
                    continue;
                }

                clients.Add(pooledClient);
                addedClients[pooledClient.ClientId] = true;
            }

            _clientHandles.Clear();
        }

        foreach (AsyncEventClient client in clients)
        {
            client.Close();
            client.ReadEventArgs.Completed -= Receive_Completed;
            client.WriteEventArgs.Completed -= Send_Completed;
            client.ReadEventArgs.Dispose();
            client.WriteEventArgs.Dispose();
        }

        _maxNumberAccepts.Dispose();
        _cancellation.Dispose();

        GC.SuppressFinalize(this);
    }

    private void WaitForClientPoolDrain()
    {
        int waitedMs = 0;
        while (waitedMs < ThreadTimeoutMs)
        {
            lock (_clientLock)
            {
                if (_clientPool.Count == _maxConnections)
                {
                    return;
                }
            }

            Thread.Sleep(10);
            waitedMs += 10;
        }

        Log(LogLevel.Error, "Dispose", "Timed out waiting for client pool to drain.");
    }

    private void WaitForAcceptPoolDrain()
    {
        int waitedMs = 0;
        while (waitedMs < ThreadTimeoutMs)
        {
            lock (_acceptPoolLock)
            {
                if (_acceptPool.Count == NumAccepts)
                {
                    return;
                }
            }

            Thread.Sleep(10);
            waitedMs += 10;
        }

        Log(LogLevel.Error, "Dispose", "Timed out waiting for accept pool to drain.");
    }

    private void Log(LogLevel level, string function, string message, string clientIdentity = "")
    {
        // ReSharper disable once InconsistentlySynchronizedField
        Logger.Write(level, $"{_identity}{clientIdentity} {function} - {message}", null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidStateException() =>
        throw new InvalidOperationException("Initial State is invalid.");
}