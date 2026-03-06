using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

/// <summary>
/// High-performance TCP server built on <see cref="SocketAsyncEventArgs"/>.
/// </summary>
/// <remarks>
/// This server keeps transport state and socket buffers preallocated, but enforces copy isolation at the
/// application boundary: received payloads are copied before delivery and outbound payloads are copied
/// when they are queued.
/// </remarks>
public sealed class SaeaServer : ISaeaAcceptHandler, ISaeaIoChannelOwner, IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(SaeaServer));

    private readonly object _lifecycleLock;
    private readonly ISaeaConnectionHandler _handler;
    private readonly SaeaServerOptions _options;
    private readonly PinnedBufferArena _bufferArena;
    private readonly SaeaSessionPool _sessionPool;
    private readonly SaeaSessionRegistry _sessionRegistry;
    private readonly SaeaAcceptLoop _acceptLoop;
    private readonly IdleConnectionMonitor? _idleConnectionMonitor;

    private CancellationTokenSource? _lifecycleCancellation;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaeaServer"/> class.
    /// </summary>
    /// <param name="ipAddress">The listening IP address.</param>
    /// <param name="port">The listening TCP port.</param>
    /// <param name="handler">The application handler.</param>
    /// <param name="options">The server configuration.</param>
    public SaeaServer(IPAddress ipAddress, ushort port, ISaeaConnectionHandler handler, SaeaServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        IpAddress = ipAddress;
        Port = port;
        _handler = handler;
        _options = new SaeaServerOptions(options);
        _lifecycleLock = new object();
        _bufferArena = new PinnedBufferArena(_options.MaxConnections * 2, _options.BufferSize);
        _sessionRegistry = new SaeaSessionRegistry(_options.MaxConnections);
        _sessionPool = new SaeaSessionPool(_options.MaxConnections, CreateChannel, this);
        _acceptLoop = new SaeaAcceptLoop(IpAddress, Port, _options, this);

        if (_options.SocketTimeoutSeconds > 0)
        {
            _idleConnectionMonitor = new IdleConnectionMonitor(
                TimeSpan.FromSeconds(_options.SocketTimeoutSeconds),
                _sessionRegistry,
                handle => DisconnectInternal(handle, SocketError.TimedOut),
                _options.Identity);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SaeaServer"/> class with default options.
    /// </summary>
    /// <param name="ipAddress">The listening IP address.</param>
    /// <param name="port">The listening TCP port.</param>
    /// <param name="handler">The application handler.</param>
    public SaeaServer(IPAddress ipAddress, ushort port, ISaeaConnectionHandler handler)
        : this(ipAddress, port, handler, new SaeaServerOptions())
    {
    }

    /// <summary>
    /// Listening IP address.
    /// </summary>
    public IPAddress IpAddress { get; }

    /// <summary>
    /// Listening TCP port.
    /// </summary>
    public ushort Port { get; }

    /// <summary>
    /// Gets a value indicating whether the server is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the current number of active sessions.
    /// </summary>
    public int ActiveSessionCount => _sessionRegistry.ActiveCount;

    /// <summary>
    /// Starts the server.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();

            if (_isRunning)
            {
                return;
            }

            if (!_sessionPool.WaitForDrain(_options.DrainTimeoutMs))
            {
                throw new InvalidOperationException("The previous server run did not drain fully.");
            }

            _sessionPool.IncrementAllGenerations();
            _sessionRegistry.Clear();

            _lifecycleCancellation?.Dispose();
            _lifecycleCancellation = new CancellationTokenSource();

            _acceptLoop.Start();
            _idleConnectionMonitor?.Start(_lifecycleCancellation.Token);
            _isRunning = true;
        }

        SafeInvoke(() => _handler.OnServerStarted(), nameof(ISaeaConnectionHandler.OnServerStarted));
    }

    /// <summary>
    /// Stops the server and disconnects all active sessions.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cancellationSource;
        lock (_lifecycleLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            cancellationSource = _lifecycleCancellation;
            _lifecycleCancellation = null;
        }

        cancellationSource?.Cancel();

        _acceptLoop.Stop(_options.DrainTimeoutMs);
        _idleConnectionMonitor?.Stop(_options.DrainTimeoutMs);

        List<SaeaSessionHandle> snapshot = new List<SaeaSessionHandle>(_options.MaxConnections);
        _sessionRegistry.Snapshot(snapshot);
        foreach (SaeaSessionHandle handle in snapshot)
        {
            DisconnectInternal(handle, SocketError.OperationAborted);
        }

        _sessionPool.WaitForDrain(_options.DrainTimeoutMs);
        cancellationSource?.Dispose();

        SafeInvoke(() => _handler.OnServerStopped(), nameof(ISaeaConnectionHandler.OnServerStopped));
    }

    /// <summary>
    /// Copies and queues a payload for sending.
    /// </summary>
    /// <param name="handle">The destination session.</param>
    /// <param name="data">The payload to copy and send.</param>
    public void Send(SaeaSessionHandle handle, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Send(handle, new ReadOnlySpan<byte>(data));
    }

    /// <summary>
    /// Copies and queues a payload for sending.
    /// </summary>
    /// <param name="handle">The destination session.</param>
    /// <param name="data">The payload to copy and send.</param>
    public void Send(SaeaSessionHandle handle, ReadOnlySpan<byte> data)
    {
        if (!handle.TryGetSession(out SaeaSession? session) || session is null)
        {
            return;
        }

        if (!session.IsConnected)
        {
            return;
        }

        SendQueueEnqueueResult result = session.Channel.EnqueueSend(data);
        if (result == SendQueueEnqueueResult.Overflow)
        {
            DisconnectInternal(handle, SocketError.NoBufferSpaceAvailable);
        }
    }

    /// <summary>
    /// Initiates a graceful disconnect for the specified session.
    /// </summary>
    /// <param name="handle">The session to disconnect.</param>
    public void Disconnect(SaeaSessionHandle handle)
    {
        DisconnectInternal(handle, SocketError.Success);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
        _acceptLoop.Dispose();
        _idleConnectionMonitor?.Dispose();
        _sessionPool.Dispose();
    }

    void ISaeaAcceptHandler.OnSocketAccepted(Socket socket)
    {
        if (!_isRunning)
        {
            Service.CloseSocket(socket);
            return;
        }

        if (!_sessionPool.TryAcquire(out SaeaSession? session) || session is null)
        {
            Service.CloseSocket(socket);
            return;
        }

        SaeaSessionHandle handle = default;
        bool registered = false;

        try
        {
            ConfigureAcceptedSocket(socket);

            int generation = _sessionPool.NextGeneration(session.SessionId);
            session.Initialize(socket, generation);
            handle = new SaeaSessionHandle(session, generation);
            _sessionRegistry.Register(handle);
            registered = true;

            SafeInvoke(() => _handler.OnConnected(handle), nameof(ISaeaConnectionHandler.OnConnected));
            session.Channel.StartReceiving();
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);

            if (registered)
            {
                _sessionRegistry.Unregister(handle);
            }

            if (session.TryBeginShutdown())
            {
                session.Channel.BeginShutdown();
            }

            _sessionPool.TryRecycle(session);
            Service.CloseSocket(socket);
        }
    }

    void ISaeaIoChannelOwner.OnReceiveCompleted(int sessionId, BufferSlice buffer)
    {
        SaeaSession session = _sessionPool.Sessions[sessionId];
        session.RecordReceive(buffer.Length);

        byte[] copiedPayload = GC.AllocateUninitializedArray<byte>(buffer.Length);
        Buffer.BlockCopy(buffer.Array, buffer.Offset, copiedPayload, 0, buffer.Length);

        SafeInvoke(() => _handler.OnReceived(new SaeaSessionHandle(session, session.Generation), copiedPayload), nameof(ISaeaConnectionHandler.OnReceived));
    }

    void ISaeaIoChannelOwner.OnSendCompleted(int sessionId, int bytesTransferred)
    {
        SaeaSession session = _sessionPool.Sessions[sessionId];
        session.RecordSend(bytesTransferred);
    }

    void ISaeaIoChannelOwner.OnChannelClosed(int sessionId, SocketError socketError)
    {
        SaeaSession session = _sessionPool.Sessions[sessionId];
        DisconnectInternal(new SaeaSessionHandle(session, session.Generation), socketError);
    }

    void ISaeaIoChannelOwner.OnChannelReadyForRecycle(int sessionId)
    {
        SaeaSession session = _sessionPool.Sessions[sessionId];
        _sessionPool.TryRecycle(session);
    }

    private SaeaIoChannel CreateChannel(int sessionId)
    {
        BufferSlice receiveBuffer = _bufferArena.GetSlice(sessionId * 2);
        BufferSlice sendBuffer = _bufferArena.GetSlice((sessionId * 2) + 1);
        return new SaeaIoChannel(sessionId, receiveBuffer, sendBuffer, this, _options.MaxQueuedSendBytes);
    }

    private void DisconnectInternal(SaeaSessionHandle handle, SocketError socketError)
    {
        if (!handle.TryGetSession(out SaeaSession? session) || session is null)
        {
            return;
        }

        if (!session.TryBeginShutdown())
        {
            return;
        }

        _sessionRegistry.Unregister(handle);
        session.Channel.BeginShutdown();
        SafeInvoke(() => _handler.OnDisconnected(handle, socketError), nameof(ISaeaConnectionHandler.OnDisconnected));
        _sessionPool.TryRecycle(session);
    }

    private void ConfigureAcceptedSocket(Socket socket)
    {
        _options.ClientSocketSettings.ConfigureSocket(socket, Logger);
        _options.ClientSocketSettings.SetSocketOptions(socket, Logger);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception)
        {
            if (_options.DebugMode)
            {
                Logger.Debug("Ignoring Socket Setting: KeepAlive");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SaeaServer));
        }
    }

    private void SafeInvoke(Action action, string callbackName)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);
            if (_options.DebugMode)
            {
                Logger.Debug($"Handler callback '{callbackName}' failed.");
            }
        }
    }
}
