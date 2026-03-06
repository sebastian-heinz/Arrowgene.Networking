using System;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

/// <summary>
/// Immutable configuration for <see cref="AsyncEventServer"/>.
/// </summary>
public sealed class AsyncEventServerConfiguration
{
    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    public AsyncEventServerConfiguration()
        : this(
            identity: string.Empty,
            maxConnections: 100,
            bufferSize: 4096,
            backlog: 128,
            concurrentAccepts: Math.Max(1, Math.Min(Environment.ProcessorCount, 16)),
            bindRetryCount: 10,
            bindRetryDelayMs: 250,
            maxQueuedSendBytes: 16 * 1024 * 1024,
            orderingLaneCount: 1,
            idleConnectionTimeout: null,
            clientNoDelay: true,
            clientKeepAlive: true,
            reuseAddress: true,
            receiveSocketBufferSize: null,
            sendSocketBufferSize: null,
            stopDrainTimeoutMs: 10000)
    {
    }

    /// <summary>
    /// Creates a fully specified configuration.
    /// </summary>
    public AsyncEventServerConfiguration(
        string identity,
        int maxConnections,
        int bufferSize,
        int backlog,
        int concurrentAccepts,
        int bindRetryCount,
        int bindRetryDelayMs,
        int maxQueuedSendBytes,
        int orderingLaneCount,
        TimeSpan? idleConnectionTimeout,
        bool clientNoDelay,
        bool clientKeepAlive,
        bool reuseAddress,
        int? receiveSocketBufferSize,
        int? sendSocketBufferSize,
        int stopDrainTimeoutMs)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        MaxConnections = maxConnections;
        BufferSize = bufferSize;
        Backlog = backlog;
        ConcurrentAccepts = concurrentAccepts;
        BindRetryCount = bindRetryCount;
        BindRetryDelayMs = bindRetryDelayMs;
        MaxQueuedSendBytes = maxQueuedSendBytes;
        OrderingLaneCount = orderingLaneCount;
        IdleConnectionTimeout = idleConnectionTimeout;
        ClientNoDelay = clientNoDelay;
        ClientKeepAlive = clientKeepAlive;
        ReuseAddress = reuseAddress;
        ReceiveSocketBufferSize = receiveSocketBufferSize;
        SendSocketBufferSize = sendSocketBufferSize;
        StopDrainTimeoutMs = stopDrainTimeoutMs;

        Validate();
    }

    /// <summary>
    /// Creates a defensive copy of another configuration.
    /// </summary>
    /// <param name="other">The source configuration.</param>
    public AsyncEventServerConfiguration(AsyncEventServerConfiguration other)
        : this(
            other?.Identity ?? throw new ArgumentNullException(nameof(other)),
            other.MaxConnections,
            other.BufferSize,
            other.Backlog,
            other.ConcurrentAccepts,
            other.BindRetryCount,
            other.BindRetryDelayMs,
            other.MaxQueuedSendBytes,
            other.OrderingLaneCount,
            other.IdleConnectionTimeout,
            other.ClientNoDelay,
            other.ClientKeepAlive,
            other.ReuseAddress,
            other.ReceiveSocketBufferSize,
            other.SendSocketBufferSize,
            other.StopDrainTimeoutMs)
    {
    }

    /// <summary>
    /// Gets the diagnostic identity used for background thread names.
    /// </summary>
    public string Identity { get; }

    /// <summary>
    /// Gets the maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; }

    /// <summary>
    /// Gets the size of each per-direction socket buffer.
    /// </summary>
    public int BufferSize { get; }

    /// <summary>
    /// Gets the listen backlog.
    /// </summary>
    public int Backlog { get; }

    /// <summary>
    /// Gets the number of concurrent accept operations.
    /// </summary>
    public int ConcurrentAccepts { get; }

    /// <summary>
    /// Gets the number of bind retries before start fails.
    /// </summary>
    public int BindRetryCount { get; }

    /// <summary>
    /// Gets the delay between bind retries in milliseconds.
    /// </summary>
    public int BindRetryDelayMs { get; }

    /// <summary>
    /// Gets the maximum queued outbound bytes per connection.
    /// </summary>
    public int MaxQueuedSendBytes { get; }

    /// <summary>
    /// Gets the number of ordering lanes assigned across accepted connections.
    /// </summary>
    public int OrderingLaneCount { get; }

    /// <summary>
    /// Gets the idle timeout. A null value disables idle disconnects.
    /// </summary>
    public TimeSpan? IdleConnectionTimeout { get; }

    /// <summary>
    /// Gets whether <see cref="System.Net.Sockets.Socket.NoDelay"/> is enabled for client sockets.
    /// </summary>
    public bool ClientNoDelay { get; }

    /// <summary>
    /// Gets whether <see cref="System.Net.Sockets.SocketOptionName.KeepAlive"/> is enabled for client sockets.
    /// </summary>
    public bool ClientKeepAlive { get; }

    /// <summary>
    /// Gets whether <see cref="System.Net.Sockets.SocketOptionName.ReuseAddress"/> is enabled for the listener.
    /// </summary>
    public bool ReuseAddress { get; }

    /// <summary>
    /// Gets the optional receive socket buffer size for accepted sockets.
    /// </summary>
    public int? ReceiveSocketBufferSize { get; }

    /// <summary>
    /// Gets the optional send socket buffer size for accepted sockets.
    /// </summary>
    public int? SendSocketBufferSize { get; }

    /// <summary>
    /// Gets the maximum wait time for shutdown drain operations.
    /// </summary>
    public int StopDrainTimeoutMs { get; }

    /// <summary>
    /// Validates all configuration values.
    /// </summary>
    public void Validate()
    {
        if (MaxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        }

        if (BufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferSize));
        }

        if (Backlog <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Backlog));
        }

        if (ConcurrentAccepts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrentAccepts));
        }

        if (BindRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BindRetryCount));
        }

        if (BindRetryDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BindRetryDelayMs));
        }

        if (MaxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendBytes));
        }

        if (OrderingLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OrderingLaneCount));
        }

        if (IdleConnectionTimeout.HasValue && IdleConnectionTimeout.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(IdleConnectionTimeout));
        }

        if (ReceiveSocketBufferSize.HasValue && ReceiveSocketBufferSize.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ReceiveSocketBufferSize));
        }

        if (SendSocketBufferSize.HasValue && SendSocketBufferSize.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SendSocketBufferSize));
        }

        if (StopDrainTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StopDrainTimeoutMs));
        }
    }
}
