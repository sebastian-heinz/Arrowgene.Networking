using System;
using Arrowgene.Networking;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

/// <summary>
/// Validated configuration for <see cref="SaeaServer"/>.
/// </summary>
public sealed class SaeaServerOptions : ICloneable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SaeaServerOptions"/> class with defaults.
    /// </summary>
    public SaeaServerOptions()
    {
        Identity = string.Empty;
        MaxConnections = 100;
        BufferSize = 2048;
        ConcurrentAccepts = 10;
        BindRetries = 10;
        MaxQueuedSendBytes = 16 * 1024 * 1024;
        DrainTimeoutMs = 10000;
        SocketTimeoutSeconds = -1;
        ListenerSocketSettings = new SocketSettings();
        ClientSocketSettings = new SocketSettings
        {
            NoDelay = true
        };
        DebugMode = false;
    }

    /// <summary>
    /// Initializes a deep copy of the provided options.
    /// </summary>
    /// <param name="other">The source options.</param>
    public SaeaServerOptions(SaeaServerOptions other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Identity = other.Identity;
        MaxConnections = other.MaxConnections;
        BufferSize = other.BufferSize;
        ConcurrentAccepts = other.ConcurrentAccepts;
        BindRetries = other.BindRetries;
        MaxQueuedSendBytes = other.MaxQueuedSendBytes;
        DrainTimeoutMs = other.DrainTimeoutMs;
        SocketTimeoutSeconds = other.SocketTimeoutSeconds;
        ListenerSocketSettings = new SocketSettings(other.ListenerSocketSettings);
        ClientSocketSettings = new SocketSettings(other.ClientSocketSettings);
        DebugMode = other.DebugMode;
    }

    /// <summary>
    /// Optional identity prefix used in thread names and log output.
    /// </summary>
    public string Identity { get; init; }

    /// <summary>
    /// Maximum simultaneous client connections.
    /// </summary>
    public int MaxConnections { get; init; }

    /// <summary>
    /// Receive and send buffer size per session direction.
    /// </summary>
    public int BufferSize { get; init; }

    /// <summary>
    /// Number of concurrent accept operations to keep posted.
    /// </summary>
    public int ConcurrentAccepts { get; init; }

    /// <summary>
    /// Number of bind retries before startup fails.
    /// </summary>
    public int BindRetries { get; init; }

    /// <summary>
    /// Maximum bytes allowed in a session send queue before the connection is closed.
    /// </summary>
    public int MaxQueuedSendBytes { get; init; }

    /// <summary>
    /// Maximum time to wait for accept and session drain during shutdown.
    /// </summary>
    public int DrainTimeoutMs { get; init; }

    /// <summary>
    /// Session idle timeout in seconds. Use -1 to disable idle disconnects.
    /// </summary>
    public int SocketTimeoutSeconds { get; init; }

    /// <summary>
    /// Socket settings applied to the listening socket.
    /// </summary>
    public SocketSettings ListenerSocketSettings { get; init; }

    /// <summary>
    /// Socket settings applied to accepted client sockets.
    /// </summary>
    public SocketSettings ClientSocketSettings { get; init; }

    /// <summary>
    /// Enables verbose diagnostic logging.
    /// </summary>
    public bool DebugMode { get; init; }

    /// <summary>
    /// Validates all options.
    /// </summary>
    public void Validate()
    {
        if (MaxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConnections), "MaxConnections must be greater than zero.");
        }

        if (BufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferSize), "BufferSize must be greater than zero.");
        }

        if (ConcurrentAccepts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrentAccepts), "ConcurrentAccepts must be greater than zero.");
        }

        if (BindRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BindRetries), "BindRetries must be greater than zero.");
        }

        if (MaxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendBytes), "MaxQueuedSendBytes must be greater than zero.");
        }

        if (DrainTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainTimeoutMs), "DrainTimeoutMs must be greater than zero.");
        }

        if (SocketTimeoutSeconds < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(SocketTimeoutSeconds), "SocketTimeoutSeconds must be -1 or greater.");
        }

        ArgumentNullException.ThrowIfNull(ListenerSocketSettings);
        ArgumentNullException.ThrowIfNull(ClientSocketSettings);
    }

    /// <inheritdoc />
    public object Clone()
    {
        return new SaeaServerOptions(this);
    }
}
