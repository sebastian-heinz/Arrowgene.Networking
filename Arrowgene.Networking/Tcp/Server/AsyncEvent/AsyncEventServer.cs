using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

/// <summary>
/// A pooled TCP server built on <see cref="SocketAsyncEventArgs"/>.
/// </summary>
public sealed class AsyncEventServer : TcpServer, IDisposable
{
    private const string AcceptThreadName = "AsyncEventServer";
    private const string TimeoutThreadName = "AsyncEventServer_Timeout";
    private const int ThreadTimeoutMs = 10000;
    private const int MinSocketTimeoutDelayMs = 1000;
    private const int MaxSocketTimeoutDelayMs = 30000;

    private static readonly ILogger Logger = LogProvider.Logger(typeof(AsyncEventServer));

    private readonly object _lifecycleLock;
    private readonly AsyncEventSettings _settings;
    private readonly AsyncEventAcceptPool _acceptPool;
    private readonly AsyncEventBufferSlab _bufferSlab;
    private readonly AsyncEventClientRegistry _clientRegistry;
    private readonly TimeSpan _socketTimeout;
    private readonly string _identity;
    private CancellationTokenSource _cancellation;
    private Thread? _acceptThread;
    private Thread? _timeoutThread;
    private Socket? _listenSocket;
    private int _runGeneration;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    /// <summary>
    /// Creates a server with explicit settings.
    /// </summary>
    /// <param name="ipAddress">The IP address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="consumer">The consumer that receives callbacks.</param>
    /// <param name="settings">The server settings.</param>
    public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer, AsyncEventSettings settings)
        : base(ipAddress, port, consumer)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        settings.Validate();
        _settings = new AsyncEventSettings(settings);
        _lifecycleLock = new object();
        _socketTimeout = TimeSpan.FromSeconds(_settings.SocketTimeoutSeconds);
        _identity = string.IsNullOrEmpty(_settings.Identity) ? string.Empty : $"[{_settings.Identity}] ";
        _cancellation = new CancellationTokenSource();
        _bufferSlab = new AsyncEventBufferSlab(_settings.MaxConnections, _settings.BufferSize);
        _clientRegistry = new AsyncEventClientRegistry(
            _settings.MaxConnections,
            _settings.OrderingLaneCount,
            CreateClient);
        _acceptPool = new AsyncEventAcceptPool(_settings.AcceptConcurrency, AcceptCompleted);
    }

    /// <summary>
    /// Creates a server with default settings.
    /// </summary>
    /// <param name="ipAddress">The IP address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="consumer">The consumer that receives callbacks.</param>
    public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer)
        : this(ipAddress, port, consumer, new AsyncEventSettings())
    {
    }

    /// <inheritdoc />
    protected override void ServerStart()
    {
        Log(LogLevel.Info, nameof(ServerStart), "Starting server...");

        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                Log(LogLevel.Error, nameof(ServerStart), "Server is disposed.");
                return;
            }

            if (_isRunning)
            {
                Log(LogLevel.Error, nameof(ServerStart), "Server already started.");
                return;
            }

            if (!WaitForDrain(nameof(ServerStart), ThreadTimeoutMs))
            {
                Log(LogLevel.Error, nameof(ServerStart), "Previous run has not drained yet.");
                return;
            }

            try
            {
                _clientRegistry.PrepareForStart();
            }
            catch (Exception exception)
            {
                Log(LogLevel.Error, nameof(ServerStart), exception.Message);
                return;
            }

            if (_acceptPool.CurrentCount != _acceptPool.Capacity)
            {
                Log(
                    LogLevel.Error,
                    nameof(ServerStart),
                    $"Accept pool is not fully drained. Count:{_acceptPool.CurrentCount} Expected:{_acceptPool.Capacity}.");
                return;
            }

            if (_acceptPool.CurrentSemaphoreCount != _acceptPool.Capacity)
            {
                Log(
                    LogLevel.Error,
                    nameof(ServerStart),
                    $"Accept semaphore is out of sync. CurrentCount:{_acceptPool.CurrentSemaphoreCount} Expected:{_acceptPool.Capacity}.");
                return;
            }

            int runGeneration = Interlocked.Increment(ref _runGeneration);
            _acceptPool.PrepareForStart(runGeneration);
            _isRunning = true;
            _acceptThread = new Thread(Run)
            {
                Name = $"{_identity}{AcceptThreadName}",
                IsBackground = true
            };
            _acceptThread.Start();
        }

        Log(LogLevel.Info, nameof(ServerStart), "Server start initiated.");
    }

    /// <inheritdoc />
    protected override void ServerStop()
    {
        Log(LogLevel.Info, nameof(ServerStop), "Stopping server...");
        if (!StopCore(nameof(ServerStop), logAlreadyStopped: true))
        {
            return;
        }

        Log(LogLevel.Info, nameof(ServerStop), "Server stopped.");
    }

    /// <inheritdoc />
    public override void Send<T>(T socket, byte[] data)
    {
        if (_isDisposed || !_isRunning)
        {
            Log(LogLevel.Error, nameof(Send), "Server is not running.");
            return;
        }

        if (data is null || data.Length == 0)
        {
            Log(LogLevel.Error, nameof(Send), "Empty payload, not sending.");
            return;
        }

        if (socket is not AsyncEventClientHandle clientHandle)
        {
            Log(LogLevel.Error, nameof(Send), "ITcpSocket is not an AsyncEventClientHandle.");
            return;
        }

        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, nameof(Send), "Client handle is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, nameof(Send), "Client is not alive.", client.Identity);
            return;
        }

        if (!client.QueueSend(data, out bool startSend, out bool queueOverflow))
        {
            if (queueOverflow)
            {
                Log(LogLevel.Error, nameof(Send), "Send queue overflow, closing client.", client.Identity);
                Disconnect(client);
            }

            return;
        }

        if (startSend)
        {
            StartSend(client);
        }
    }

    internal void Disconnect(AsyncEventClient client)
    {
        if (client is null)
        {
            return;
        }

        if (!client.TryBeginDisconnect())
        {
            TryRecycleClient(client);
            return;
        }

        client.ClearQueuedSends();

        bool removed = _clientRegistry.TryRemoveActiveClient(client, out int currentConnections);
        if (!removed)
        {
            Log(LogLevel.Error, nameof(Disconnect), "Could not remove client from the active registry.", client.Identity);
        }

        TimeSpan duration = client.ConnectedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : DateTime.UtcNow - client.ConnectedAt;

        Log(
            LogLevel.Debug,
            nameof(Disconnect),
            $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
            $"Total Bytes Received:{client.BytesReceived} ({Service.GetHumanReadableSize(client.BytesReceived)}){Environment.NewLine}" +
            $"Total Bytes Sent:{client.BytesSent} ({Service.GetHumanReadableSize(client.BytesSent)}){Environment.NewLine}" +
            $"Current Connections:{currentConnections}",
            client.Identity);

        try
        {
            OnClientDisconnected(client.CreateHandle());
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(Disconnect), "Error during OnClientDisconnected user code.", client.Identity);
            Logger.Exception(exception);
        }

        TryRecycleClient(client);
    }

    private void Disconnect(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, nameof(Disconnect), "Client handle is stale.");
            return;
        }

        Disconnect(client);
    }

    private void Run()
    {
        if (!_isRunning)
        {
            return;
        }

        if (!TryStartListener())
        {
            Log(LogLevel.Error, nameof(Run), "Stopping server due to startup failure.");
            if (StopCore(nameof(Run), logAlreadyStopped: false))
            {
                Log(LogLevel.Info, nameof(Run), "Server stopped due to listener failure.");
            }

            return;
        }
        
        if (_socketTimeout > TimeSpan.Zero)
        {
            Thread timeoutThread = new Thread(CheckSocketTimeout)
            {
                Name = $"{_identity}{TimeoutThreadName}",
                IsBackground = true
            };

            lock (_lifecycleLock)
            {
                if (_isRunning)
                {
                    _timeoutThread = timeoutThread;
                    timeoutThread.Start();
                }
            }
        }

        StartAcceptLoop();
    }

    private bool TryStartListener()
    {
        IPEndPoint localEndPoint = new IPEndPoint(IpAddress, Port);
        CancellationToken cancellationToken = _cancellation.Token;

        for (int attempt = 0; _isRunning && attempt <= _settings.BindRetryCount; attempt++)
        {
            if (attempt > 0 && cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Socket listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _settings.SocketSettings.ConfigureSocket(listenSocket, Logger);

            try
            {
                listenSocket.Bind(localEndPoint);
                listenSocket.Listen(_settings.SocketSettings.Backlog);

                lock (_lifecycleLock)
                {
                    if (!_isRunning)
                    {
                        Service.CloseSocket(listenSocket);
                        return false;
                    }

                    _listenSocket = listenSocket;
                }

                Log(LogLevel.Info, nameof(TryStartListener), $"Listening on {IpAddress}:{Port}.");
                return true;
            }
            catch (Exception exception)
            {
                Service.CloseSocket(listenSocket);
                Logger.Exception(exception);

                if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                {
                    Log(LogLevel.Error, nameof(TryStartListener), $"Address is already in use ({IpAddress}:{Port}).");
                }

                if (attempt == _settings.BindRetryCount)
                {
                    break;
                }

                Log(LogLevel.Error, nameof(TryStartListener), "Listener startup failed, retrying in 1 second...");
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    break;
                }
            }
        }

        return false;
    }

    private bool StopCore(string function, bool logAlreadyStopped)
    {
        Thread? acceptThread;
        Thread? timeoutThread;
        Socket? listenSocket;
        CancellationTokenSource cancellationToCancel;

        lock (_lifecycleLock)
        {
            if (!_isRunning)
            {
                if (logAlreadyStopped)
                {
                    Log(LogLevel.Error, function, "Server already stopped.");
                }

                return false;
            }

            _isRunning = false;
            Interlocked.Increment(ref _runGeneration);

            cancellationToCancel = _cancellation;
            _cancellation = new CancellationTokenSource();

            acceptThread = _acceptThread;
            timeoutThread = _timeoutThread;
            _acceptThread = null;
            _timeoutThread = null;

            listenSocket = _listenSocket;
            _listenSocket = null;
        }

        Log(LogLevel.Info, function, "Shutting down listening socket...");
        Service.CloseSocket(listenSocket);

        try
        {
            cancellationToCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        List<AsyncEventClientHandle> handles = new List<AsyncEventClientHandle>(_settings.MaxConnections);
        _clientRegistry.SnapshotActiveHandles(handles);
        foreach (AsyncEventClientHandle handle in handles)
        {
            Disconnect(handle);
        }

        JoinThreadIfNotCurrent(acceptThread);
        JoinThreadIfNotCurrent(timeoutThread);

        WaitForDrain(function, ThreadTimeoutMs);
        cancellationToCancel.Dispose();
        return true;
    }

    private void JoinThreadIfNotCurrent(Thread? thread)
    {
        if (thread is null)
        {
            return;
        }

        if (ReferenceEquals(thread, Thread.CurrentThread))
        {
            return;
        }

        Service.JoinThread(thread, ThreadTimeoutMs, Logger);
    }

    private void StartAcceptLoop()
    {
        CancellationToken cancellationToken = _cancellation.Token;

        while (_isRunning)
        {
            if (!_acceptPool.TryAcquire(cancellationToken, out SocketAsyncEventArgs? acceptEventArgs))
            {
                return;
            }

            SocketAsyncEventArgs acquiredEventArgs = acceptEventArgs!;

            Socket? listenSocket = _listenSocket;
            if (listenSocket is null)
            {
                _acceptPool.Return(acquiredEventArgs, Volatile.Read(ref _runGeneration));
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = listenSocket.AcceptAsync(acquiredEventArgs);
            }
            catch (ObjectDisposedException)
            {
                _acceptPool.Return(acquiredEventArgs, Volatile.Read(ref _runGeneration));
                return;
            }
            catch (SocketException exception)
            {
                Logger.Exception(exception);
                _acceptPool.Return(acquiredEventArgs, Volatile.Read(ref _runGeneration));
                if (_isRunning)
                {
                    Log(LogLevel.Error, nameof(StartAcceptLoop), "AcceptAsync failed.");
                }

                continue;
            }

            if (!willRaiseEvent)
            {
                ProcessAccept(acquiredEventArgs);
            }
        }
    }

    private void AcceptCompleted(object? sender, SocketAsyncEventArgs acceptEventArgs)
    {
        ProcessAccept(acceptEventArgs);
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
    {
        Socket? acceptedSocket = acceptEventArgs.AcceptSocket;
        SocketError socketError = acceptEventArgs.SocketError;
        int callbackGeneration = acceptEventArgs.UserToken is int generation ? generation : 0;
        _acceptPool.Return(acceptEventArgs, Volatile.Read(ref _runGeneration));

        if (acceptedSocket is null)
        {
            if (socketError != SocketError.Success && socketError != SocketError.OperationAborted)
            {
                Log(LogLevel.Error, nameof(ProcessAccept), $"Accept completed without a socket. SocketError:{socketError}.");
            }

            return;
        }

        string clientIdentity = acceptedSocket.RemoteEndPoint is IPEndPoint endPoint
            ? $"[{endPoint.Address}:{endPoint.Port}]"
            : "[Unknown Client]";

        if (!_isRunning)
        {
            Log(LogLevel.Debug, nameof(ProcessAccept), "Server is not running, rejecting accepted socket.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (callbackGeneration != Volatile.Read(ref _runGeneration))
        {
            Log(LogLevel.Debug, nameof(ProcessAccept), "Run generation mismatch, rejecting accepted socket.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (socketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), $"Socket error: {socketError}.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        try
        {
            ConfigureAcceptedSocket(acceptedSocket, clientIdentity);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Failed to configure the accepted socket.", clientIdentity);
            Logger.Exception(exception);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        AsyncEventClient? client;
        AsyncEventClientHandle clientHandle;
        int activeConnections;
        try
        {
            if (!_clientRegistry.TryActivateClient(
                    acceptedSocket,
                    callbackGeneration,
                    out client,
                    out clientHandle,
                    out activeConnections))
            {
                Log(LogLevel.Error, nameof(ProcessAccept), "No available client slot in the pool.", clientIdentity);
                Service.CloseSocket(acceptedSocket);
                return;
            }
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Failed to activate the accepted client slot.", clientIdentity);
            Logger.Exception(exception);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (client is null)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Client activation returned no client instance.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        Log(LogLevel.Info, nameof(ProcessAccept), $"Active Client Connections:{activeConnections}.", clientIdentity);

        try
        {
            OnClientConnected(clientHandle);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Error during OnClientConnected user code.", clientIdentity);
            Logger.Exception(exception);
        }

        StartReceive(client);
    }
    
    private void ConfigureAcceptedSocket(Socket acceptedSocket, string clientIdentity)
    {
        _settings.SocketSettings.ConfigureSocket(acceptedSocket, Logger);

        if (_socketTimeout <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            acceptedSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Debug, nameof(ConfigureAcceptedSocket), $"Unable to enable SO_KEEPALIVE on accepted socket: {exception.Message}", clientIdentity);
        }
    }

    private void StartReceive(AsyncEventClient client)
    {
        while (true)
        {
            if (!client.IsAlive)
            {
                TryRecycleClient(client);
                return;
            }

            Socket? socket = client.ConnectionSocket;
            if (socket is null)
            {
                Disconnect(client);
                return;
            }

            client.IncrementPendingOperations();

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.ReceiveAsync(client.ReceiveEventArgs);
            }
            catch (ObjectDisposedException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(client);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueReceiving;
            try
            {
                continueReceiving = ProcessReceive(client);
            }
            finally
            {
                client.DecrementPendingOperations();
            }

            if (!continueReceiving)
            {
                TryRecycleClient(client);
                return;
            }
        }
    }

    private void ReceiveCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not AsyncEventClient client)
        {
            Log(LogLevel.Error, nameof(ReceiveCompleted), "Unexpected user token.");
            return;
        }

        bool continueReceiving;
        try
        {
            continueReceiving = ProcessReceive(client);
        }
        finally
        {
            client.DecrementPendingOperations();
        }

        if (continueReceiving)
        {
            StartReceive(client);
            return;
        }

        TryRecycleClient(client);
    }

    private bool ProcessReceive(AsyncEventClient client)
    {
        if (!client.IsAlive)
        {
            return false;
        }

        SocketAsyncEventArgs receiveEventArgs = client.ReceiveEventArgs;
        if (receiveEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), $"Socket error {receiveEventArgs.SocketError}.", client.Identity);
            Disconnect(client);
            return false;
        }

        if (receiveEventArgs.BytesTransferred <= 0)
        {
            Disconnect(client);
            return false;
        }

        byte[]? receiveBuffer = receiveEventArgs.Buffer;
        if (receiveBuffer is null)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Receive buffer is null.", client.Identity);
            Disconnect(client);
            return false;
        }

        client.RecordReceive(receiveEventArgs.BytesTransferred);

        try
        {
            OnReceivedData(client.CreateHandle(), receiveBuffer, receiveEventArgs.Offset, receiveEventArgs.BytesTransferred);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Error in OnReceivedData user code.", client.Identity);
            Logger.Exception(exception);
        }

        return client.IsAlive;
    }

    private void StartSend(AsyncEventClient client)
    {
        while (true)
        {
            if (!client.IsAlive)
            {
                TryRecycleClient(client);
                return;
            }

            if (!client.TryPrepareSendChunk(out int chunkSize))
            {
                return;
            }

            Socket? socket = client.ConnectionSocket;
            if (socket is null)
            {
                Disconnect(client);
                return;
            }

            SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
            sendEventArgs.SetBuffer(sendEventArgs.Offset, chunkSize);
            client.IncrementPendingOperations();

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.SendAsync(sendEventArgs);
            }
            catch (ObjectDisposedException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(client);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueSending;
            try
            {
                continueSending = ProcessSend(client);
            }
            finally
            {
                client.DecrementPendingOperations();
            }

            if (!continueSending)
            {
                TryRecycleClient(client);
                return;
            }
        }
    }

    private void SendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not AsyncEventClient client)
        {
            Log(LogLevel.Error, nameof(SendCompleted), "Unexpected user token.");
            return;
        }

        bool continueSending;
        try
        {
            continueSending = ProcessSend(client);
        }
        finally
        {
            client.DecrementPendingOperations();
        }

        if (continueSending)
        {
            StartSend(client);
            return;
        }

        TryRecycleClient(client);
    }

    private bool ProcessSend(AsyncEventClient client)
    {
        if (!client.IsAlive)
        {
            return false;
        }

        SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
        if (sendEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessSend), $"Socket error {sendEventArgs.SocketError}.", client.Identity);
            Disconnect(client);
            return false;
        }
        
        if (sendEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Send completed with zero bytes transferred.", client.Identity);
            Disconnect(client);
            return false;
        }

        client.RecordSend(sendEventArgs.BytesTransferred);
        return client.CompleteSend(sendEventArgs.BytesTransferred);
    }

    private void TryRecycleClient(AsyncEventClient client)
    {
        _clientRegistry.TryRecycle(client);
    }

    private void CheckSocketTimeout()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        List<AsyncEventClientHandle> handles = new List<AsyncEventClientHandle>(_settings.MaxConnections);

        while (_isRunning)
        {
            long now = Environment.TickCount64;
            _clientRegistry.SnapshotActiveHandles(handles);

            foreach (AsyncEventClientHandle handle in handles)
            {
                if (!handle.TryGetClient(out AsyncEventClient client))
                {
                    continue;
                }

                long lastActivityTicks = client.LastWriteTicks > client.LastReadTicks
                    ? client.LastWriteTicks
                    : client.LastReadTicks;

                long elapsedTicksSinceLastActivity = now - lastActivityTicks;
                if (elapsedTicksSinceLastActivity < 0)
                {
                    elapsedTicksSinceLastActivity = 0;
                }

                if (elapsedTicksSinceLastActivity > _socketTimeout.TotalMilliseconds)
                {
                    TimeSpan elapsed = TimeSpan.FromMilliseconds(elapsedTicksSinceLastActivity);
                    DateTime lastActiveUtc = DateTime.UtcNow - elapsed;
                    Log(
                        LogLevel.Error,
                        nameof(CheckSocketTimeout),
                        $"Client socket timed out after {elapsed.TotalSeconds} seconds. SocketTimeout:{_socketTimeout.TotalSeconds} LastActive(UTC):{lastActiveUtc:yyyy-MM-dd HH:mm:ss}",
                        client.Identity);
                    Disconnect(client);
                }
            }

            int timeoutMs = Math.Clamp((int)_socketTimeout.TotalMilliseconds, MinSocketTimeoutDelayMs, MaxSocketTimeoutDelayMs);
            cancellationToken.WaitHandle.WaitOne(timeoutMs);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        bool shouldStop;

        lock (_lifecycleLock)
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

        if (!WaitForDrain(nameof(Dispose), ThreadTimeoutMs))
        {
            Log(LogLevel.Error, nameof(Dispose), "Skipping resource disposal because client operations did not drain in time.");
            return;
        }

        _acceptPool.Dispose();
        _clientRegistry.Dispose();
        _cancellation.Dispose();
    }

    private AsyncEventClient CreateClient(int clientId)
    {
        return new AsyncEventClient(
            clientId,
            this,
            _bufferSlab.CreateReceiveEventArgs(clientId, ReceiveCompleted),
            _bufferSlab.CreateSendEventArgs(clientId, SendCompleted),
            _settings.MaxQueuedSendBytes);
    }

    private bool WaitForDrain(string function, int timeoutMs)
    {
        bool clientOperationsDrained = WaitForClientOperationsDrain(function, timeoutMs);
        bool clientPoolDrained = WaitForClientPoolDrain(function, timeoutMs);
        bool acceptPoolDrained = WaitForAcceptPoolDrain(function, timeoutMs);
        return clientOperationsDrained && clientPoolDrained && acceptPoolDrained;
    }

    private bool WaitForClientOperationsDrain(string function, int timeoutMs)
    {
        int waitedMs = 0;
        while (waitedMs < timeoutMs)
        {
            bool drained = true;
            IReadOnlyList<AsyncEventClient> clients = _clientRegistry.AllClients;
            for (int index = 0; index < clients.Count; index++)
            {
                AsyncEventClient client = clients[index];
                if (client.IsAlive || client.PendingOperations > 0)
                {
                    drained = false;
                    break;
                }
            }

            if (drained)
            {
                return true;
            }

            Thread.Sleep(10);
            waitedMs += 10;
        }

        Log(LogLevel.Error, function, "Timed out waiting for pending client operations to drain.");
        return false;
    }

    private bool WaitForClientPoolDrain(string function, int timeoutMs)
    {
        if (_clientRegistry.WaitForPoolDrain(timeoutMs))
        {
            return true;
        }

        Log(LogLevel.Error, function, "Timed out waiting for the client pool to drain.");
        return false;
    }

    private bool WaitForAcceptPoolDrain(string function, int timeoutMs)
    {
        if (_acceptPool.WaitForDrain(timeoutMs))
        {
            return true;
        }

        Log(LogLevel.Error, function, "Timed out waiting for the accept pool to drain.");
        return false;
    }
    
    private void Log(LogLevel level, string function, string message, string clientIdentity = "")
    {
        string prefix = _identity.Length > 0 || clientIdentity.Length > 0
            ? $"{_identity}{clientIdentity} "
            : string.Empty;
        Logger.Write(level, $"{prefix}{function} - {message}", null);
    }
}
