using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

/// <summary>
/// A concrete, pooled, <see cref="SocketAsyncEventArgs"/>-based TCP server.
/// </summary>
public sealed class AsyncEventServer : IDisposable
{
    private readonly object _lifecycleLock;
    private readonly IAsyncEventServerHandler _handler;
    private readonly AsyncEventServerConfiguration _configuration;
    private readonly AsyncEventServerBufferSlab _bufferSlab;
    private readonly AsyncEventServerSessionPool _sessionPool;
    private readonly object _acceptLock;
    private readonly Stack<SocketAsyncEventArgs> _acceptPool;
    private readonly SocketAsyncEventArgs[] _allAcceptEventArgs;
    private readonly SemaphoreSlim _acceptSemaphore;
    private Socket? _listenSocket;
    private CancellationTokenSource? _lifecycleCancellation;
    private Thread? _acceptThread;
    private Thread? _idleThread;
    private volatile bool _isRunning;
    private bool _isDisposed;
    private int _runGeneration;

    /// <summary>
    /// Creates a server with the default configuration.
    /// </summary>
    /// <param name="ipAddress">The local address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="handler">The application handler.</param>
    public AsyncEventServer(IPAddress ipAddress, ushort port, IAsyncEventServerHandler handler)
        : this(ipAddress, port, handler, new AsyncEventServerConfiguration())
    {
    }

    /// <summary>
    /// Creates a server with an explicit configuration.
    /// </summary>
    /// <param name="ipAddress">The local address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="handler">The application handler.</param>
    /// <param name="configuration">The server configuration.</param>
    public AsyncEventServer(
        IPAddress ipAddress,
        ushort port,
        IAsyncEventServerHandler handler,
        AsyncEventServerConfiguration configuration)
    {
        IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
        Port = port;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _configuration = new AsyncEventServerConfiguration(configuration ?? throw new ArgumentNullException(nameof(configuration)));
        _lifecycleLock = new object();
        _acceptLock = new object();
        _acceptSemaphore = new SemaphoreSlim(_configuration.ConcurrentAccepts, _configuration.ConcurrentAccepts);
        _acceptPool = new Stack<SocketAsyncEventArgs>(_configuration.ConcurrentAccepts);
        _allAcceptEventArgs = new SocketAsyncEventArgs[_configuration.ConcurrentAccepts];
        _bufferSlab = new AsyncEventServerBufferSlab(_configuration.BufferSize, _configuration.MaxConnections * 2);
        _sessionPool = new AsyncEventServerSessionPool(
            _configuration.MaxConnections,
            _configuration.OrderingLaneCount,
            CreateSessionState);

        for (int index = 0; index < _allAcceptEventArgs.Length; index++)
        {
            SocketAsyncEventArgs acceptEventArgs = new SocketAsyncEventArgs
            {
                UserToken = new AsyncEventServerAcceptContext()
            };
            acceptEventArgs.Completed += OnAcceptCompleted;
            _allAcceptEventArgs[index] = acceptEventArgs;
            _acceptPool.Push(acceptEventArgs);
        }
    }

    /// <summary>
    /// Raised when an internal callback or handler invocation throws.
    /// </summary>
    public event Action<Exception>? UnhandledException;

    /// <summary>
    /// Gets the bound IP address.
    /// </summary>
    public IPAddress IpAddress { get; }

    /// <summary>
    /// Gets the bound TCP port.
    /// </summary>
    public ushort Port { get; }

    /// <summary>
    /// Gets whether the server is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the current active connection count.
    /// </summary>
    public int ActiveConnectionCount => _sessionPool.ActiveCount;

    /// <summary>
    /// Gets the immutable server configuration.
    /// </summary>
    public AsyncEventServerConfiguration Configuration => _configuration;

    /// <summary>
    /// Starts listening for connections.
    /// </summary>
    public void Start()
    {
        CancellationTokenSource cancellation;
        Socket listenSocket;
        Thread acceptThread;
        Thread? idleThread;

        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_isRunning)
            {
                throw new InvalidOperationException("The server is already running.");
            }

            if (!_sessionPool.IsDrained)
            {
                throw new InvalidOperationException("The previous server run has not fully drained.");
            }

            cancellation = new CancellationTokenSource();
            listenSocket = CreateListenSocket();
            acceptThread = CreateBackgroundThread(RunAcceptLoop, "Accept");
            idleThread = _configuration.IdleConnectionTimeout.HasValue
                ? CreateBackgroundThread(RunIdleLoop, "Idle")
                : null;

            _lifecycleCancellation = cancellation;
            _listenSocket = listenSocket;
            _acceptThread = acceptThread;
            _idleThread = idleThread;
            Interlocked.Increment(ref _runGeneration);
            _isRunning = true;
        }

        try
        {
            acceptThread.Start();
            if (idleThread is not null)
            {
                idleThread.Start();
            }
        }
        catch
        {
            lock (_lifecycleLock)
            {
                _isRunning = false;
                Interlocked.Increment(ref _runGeneration);
                _lifecycleCancellation = null;
                _listenSocket = null;
                _acceptThread = null;
                _idleThread = null;
            }

            cancellation.Cancel();
            cancellation.Dispose();
            CloseSocket(listenSocket);
            throw;
        }

        InvokeHandler(_handler.OnServerStarted);
    }

    /// <summary>
    /// Stops the server and disconnects all active connections.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Thread? acceptThread;
        Thread? idleThread;
        Socket? listenSocket;

        lock (_lifecycleLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            Interlocked.Increment(ref _runGeneration);

            cancellation = _lifecycleCancellation;
            _lifecycleCancellation = null;
            acceptThread = _acceptThread;
            _acceptThread = null;
            idleThread = _idleThread;
            _idleThread = null;
            listenSocket = _listenSocket;
            _listenSocket = null;
        }

        cancellation?.Cancel();
        CloseSocket(listenSocket);

        if (!JoinThread(acceptThread, _configuration.StopDrainTimeoutMs))
        {
            RaiseUnhandledException(new TimeoutException("The accept loop did not stop within the configured drain timeout."));
        }

        if (!JoinThread(idleThread, _configuration.StopDrainTimeoutMs))
        {
            RaiseUnhandledException(new TimeoutException("The idle monitor did not stop within the configured drain timeout."));
        }

        List<AsyncEventServerSessionState> activeSessions = new List<AsyncEventServerSessionState>();
        _sessionPool.SnapshotActiveSessions(activeSessions);
        for (int index = 0; index < activeSessions.Count; index++)
        {
            Disconnect(activeSessions[index], SocketError.OperationAborted);
        }

        if (!_sessionPool.WaitForDrain(_configuration.StopDrainTimeoutMs))
        {
            RaiseUnhandledException(new TimeoutException("The connection pool did not drain within the configured shutdown timeout."));
        }

        if (!WaitForAcceptDrain(_configuration.StopDrainTimeoutMs))
        {
            RaiseUnhandledException(new TimeoutException("The accept operation pool did not drain within the configured shutdown timeout."));
        }

        cancellation?.Dispose();
        InvokeHandler(_handler.OnServerStopped);
    }

    /// <summary>
    /// Sends a byte array to a connection.
    /// </summary>
    /// <param name="connection">The target connection.</param>
    /// <param name="payload">The payload to send.</param>
    public void Send(AsyncEventServerConnection connection, byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        Send(connection, payload.AsSpan());
    }

    /// <summary>
    /// Sends a span to a connection.
    /// </summary>
    /// <param name="connection">The target connection.</param>
    /// <param name="payload">The payload to send.</param>
    public void Send(AsyncEventServerConnection connection, ReadOnlySpan<byte> payload)
    {
        connection.Send(payload);
    }

    /// <summary>
    /// Disconnects a connection.
    /// </summary>
    /// <param name="connection">The connection to disconnect.</param>
    public void Disconnect(AsyncEventServerConnection connection)
    {
        connection.Close();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();

        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        for (int index = 0; index < _allAcceptEventArgs.Length; index++)
        {
            _allAcceptEventArgs[index].Dispose();
        }

        _acceptSemaphore.Dispose();
        _sessionPool.Dispose();
    }

    internal void OnPayloadReceived(AsyncEventServerSessionState session, byte[] payload)
    {
        AsyncEventServerConnection connection = session.CreateConnection();
        InvokeHandler(() => _handler.OnReceived(connection, payload));
    }

    internal void Disconnect(AsyncEventServerSessionState session, SocketError error)
    {
        if (!session.TryBeginShutdown(error))
        {
            TryRecycleSession(session);
            return;
        }

        _sessionPool.UnregisterActive(session);
        session.DropQueuedSends();
        session.ShutdownSocket();
        AsyncEventServerConnection connection = session.CreateConnection();
        InvokeHandler(() => _handler.OnDisconnected(connection, error));
        TryRecycleSession(session);
    }

    internal void TryRecycleSession(AsyncEventServerSessionState session)
    {
        _sessionPool.TryRecycle(session);
    }

    private AsyncEventServerSessionState CreateSessionState(int sessionId)
    {
        int receiveOffset = _bufferSlab.GetOffset(sessionId * 2);
        int sendOffset = _bufferSlab.GetOffset((sessionId * 2) + 1);
        return new AsyncEventServerSessionState(
            sessionId,
            this,
            _bufferSlab.RawBuffer,
            receiveOffset,
            sendOffset,
            _configuration.BufferSize,
            _configuration.MaxQueuedSendBytes);
    }

    private Socket CreateListenSocket()
    {
        SocketException? lastException = null;
        int attempts = _configuration.BindRetryCount + 1;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Socket listenSocket = new Socket(IpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                ConfigureListenSocket(listenSocket);
                listenSocket.Bind(new IPEndPoint(IpAddress, Port));
                listenSocket.Listen(_configuration.Backlog);
                return listenSocket;
            }
            catch (SocketException exception)
            {
                lastException = exception;
                CloseSocket(listenSocket);

                if (attempt + 1 >= attempts)
                {
                    break;
                }

                if (_configuration.BindRetryDelayMs > 0)
                {
                    Thread.Sleep(_configuration.BindRetryDelayMs);
                }
            }
            catch
            {
                CloseSocket(listenSocket);
                throw;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.AddressNotAvailable);
    }

    private void ConfigureListenSocket(Socket listenSocket)
    {
        if (_configuration.ReuseAddress)
        {
            listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
    }

    private void ConfigureClientSocket(Socket clientSocket)
    {
        clientSocket.NoDelay = _configuration.ClientNoDelay;
        clientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, _configuration.ClientKeepAlive);

        if (_configuration.ReceiveSocketBufferSize.HasValue)
        {
            clientSocket.ReceiveBufferSize = _configuration.ReceiveSocketBufferSize.Value;
        }

        if (_configuration.SendSocketBufferSize.HasValue)
        {
            clientSocket.SendBufferSize = _configuration.SendSocketBufferSize.Value;
        }
    }

    private void RunAcceptLoop()
    {
        CancellationTokenSource? source = _lifecycleCancellation;
        if (source is null)
        {
            return;
        }

        CancellationToken cancellationToken = source.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _acceptSemaphore.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            SocketAsyncEventArgs? acceptEventArgs = RentAcceptEventArgs();
            if (acceptEventArgs is null)
            {
                _acceptSemaphore.Release();
                if (cancellationToken.WaitHandle.WaitOne(1))
                {
                    break;
                }

                continue;
            }

            AsyncEventServerAcceptContext context = (AsyncEventServerAcceptContext)acceptEventArgs.UserToken!;
            context.RunGeneration = Volatile.Read(ref _runGeneration);

            Socket? listenSocket = _listenSocket;
            if (listenSocket is null || !_isRunning)
            {
                ReturnAcceptEventArgs(acceptEventArgs);
                _acceptSemaphore.Release();
                continue;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = listenSocket.AcceptAsync(acceptEventArgs);
            }
            catch (ObjectDisposedException)
            {
                ReturnAcceptEventArgs(acceptEventArgs);
                _acceptSemaphore.Release();
                break;
            }
            catch (SocketException exception)
            {
                ReturnAcceptEventArgs(acceptEventArgs);
                _acceptSemaphore.Release();

                if (!_isRunning || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                RaiseUnhandledException(exception);
                Thread.Sleep(10);
                continue;
            }

            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArgs);
            }
        }
    }

    private void RunIdleLoop()
    {
        TimeSpan timeout = _configuration.IdleConnectionTimeout ?? TimeSpan.Zero;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        CancellationTokenSource? source = _lifecycleCancellation;
        if (source is null)
        {
            return;
        }

        CancellationToken cancellationToken = source.Token;
        List<AsyncEventServerSessionState> snapshot = new List<AsyncEventServerSessionState>();
        double halfIntervalMs = timeout.TotalMilliseconds / 2d;
        int intervalMs = halfIntervalMs < 1000d
            ? 1000
            : halfIntervalMs > 30000d
                ? 30000
                : (int)halfIntervalMs;
        long timeoutMs = (long)timeout.TotalMilliseconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.WaitHandle.WaitOne(intervalMs))
            {
                break;
            }

            _sessionPool.SnapshotActiveSessions(snapshot);
            long currentTick = Environment.TickCount64;
            for (int index = 0; index < snapshot.Count; index++)
            {
                AsyncEventServerSessionState session = snapshot[index];
                long lastActivity = session.LastReceiveTick > session.LastSendTick
                    ? session.LastReceiveTick
                    : session.LastSendTick;

                if (currentTick - lastActivity >= timeoutMs)
                {
                    Disconnect(session, SocketError.TimedOut);
                }
            }
        }
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
    {
        AsyncEventServerAcceptContext context = (AsyncEventServerAcceptContext)acceptEventArgs.UserToken!;
        SocketError socketError = acceptEventArgs.SocketError;
        Socket? acceptedSocket = acceptEventArgs.AcceptSocket;
        acceptEventArgs.AcceptSocket = null;

        ReturnAcceptEventArgs(acceptEventArgs);
        _acceptSemaphore.Release();

        if (acceptedSocket is null)
        {
            if (socketError != SocketError.Success && socketError != SocketError.OperationAborted)
            {
                RaiseUnhandledException(new SocketException((int)socketError));
            }

            return;
        }

        if (!_isRunning || context.RunGeneration != Volatile.Read(ref _runGeneration) || socketError != SocketError.Success)
        {
            CloseSocket(acceptedSocket);
            return;
        }

        try
        {
            ConfigureClientSocket(acceptedSocket);
        }
        catch (Exception exception)
        {
            CloseSocket(acceptedSocket);
            RaiseUnhandledException(exception);
            return;
        }

        if (!_sessionPool.TryAcquire(out AsyncEventServerSessionState? session, out int generation, out int orderingLane))
        {
            CloseSocket(acceptedSocket);
            return;
        }

        session.Initialize(acceptedSocket, orderingLane, generation);
        _sessionPool.RegisterActive(session, orderingLane);

        AsyncEventServerConnection connection = session.CreateConnection();
        InvokeHandler(() => _handler.OnConnected(connection));

        if (session.IsConnected)
        {
            session.StartIo();
        }
        else
        {
            TryRecycleSession(session);
        }
    }

    private SocketAsyncEventArgs? RentAcceptEventArgs()
    {
        lock (_acceptLock)
        {
            if (_acceptPool.Count == 0)
            {
                return null;
            }

            return _acceptPool.Pop();
        }
    }

    private void ReturnAcceptEventArgs(SocketAsyncEventArgs acceptEventArgs)
    {
        lock (_acceptLock)
        {
            _acceptPool.Push(acceptEventArgs);
        }
    }

    private bool WaitForAcceptDrain(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            lock (_acceptLock)
            {
                if (_acceptPool.Count == _allAcceptEventArgs.Length)
                {
                    return true;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }
    }

    private Thread CreateBackgroundThread(ThreadStart entryPoint, string suffix)
    {
        string identity = string.IsNullOrWhiteSpace(_configuration.Identity)
            ? nameof(AsyncEventServer)
            : _configuration.Identity;

        return new Thread(entryPoint)
        {
            IsBackground = true,
            Name = identity + "." + suffix
        };
    }

    private static bool JoinThread(Thread? thread, int timeoutMs)
    {
        if (thread is null)
        {
            return true;
        }

        if (ReferenceEquals(thread, Thread.CurrentThread))
        {
            return true;
        }

        return thread.Join(timeoutMs);
    }

    private static void CloseSocket(Socket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Close();
        }
        catch
        {
        }
    }

    private void InvokeHandler(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            RaiseUnhandledException(exception);
        }
    }

    private void RaiseUnhandledException(Exception exception)
    {
        Action<Exception>? callbacks = UnhandledException;
        if (callbacks is null)
        {
            return;
        }

        Delegate[] invocationList = callbacks.GetInvocationList();
        for (int index = 0; index < invocationList.Length; index++)
        {
            try
            {
                ((Action<Exception>)invocationList[index])(exception);
            }
            catch
            {
            }
        }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs acceptEventArgs)
    {
        ProcessAccept(acceptEventArgs);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(AsyncEventServer));
        }
    }
}
