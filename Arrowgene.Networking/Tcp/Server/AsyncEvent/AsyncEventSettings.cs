using System;
using System.Runtime.Serialization;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

/// <summary>
/// Configuration for <see cref="AsyncEventServer"/>.
/// </summary>
[DataContract]
public sealed class AsyncEventSettings : ICloneable
{
    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    public AsyncEventSettings()
    {
        Identity = string.Empty;
        MaxConnections = 100;
        BufferSize = 2000;
        Retries = 10;
        MaxUnitOfOrder = 1;
        SocketTimeoutSeconds = -1;
        SocketSettings = new SocketSettings();
        DebugMode = false;
        ConcurrentAccepts = 10;
        MaxQueuedSendBytes = 16 * 1024 * 1024;
    }

    /// <summary>
    /// Creates a deep copy of an existing configuration.
    /// </summary>
    /// <param name="settings">The configuration to copy.</param>
    public AsyncEventSettings(AsyncEventSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        Identity = settings.Identity;
        MaxConnections = settings.MaxConnections;
        BufferSize = settings.BufferSize;
        Retries = settings.Retries;
        MaxUnitOfOrder = settings.MaxUnitOfOrder;
        SocketTimeoutSeconds = settings.SocketTimeoutSeconds;
        SocketSettings = new SocketSettings(settings.SocketSettings);
        DebugMode = settings.DebugMode;
        ConcurrentAccepts = settings.ConcurrentAccepts;
        MaxQueuedSendBytes = settings.MaxQueuedSendBytes;
    }

    /// <summary>
    /// Gets or sets the optional server identity used in logs.
    /// </summary>
    [DataMember(Order = 0)]
    public string Identity { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent connections.
    /// </summary>
    [DataMember(Order = 1)]
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the pinned receive and send buffer size per direction.
    /// </summary>
    [DataMember(Order = 3)]
    public int BufferSize { get; set; }

    /// <summary>
    /// Gets or sets the number of listener bind retries.
    /// </summary>
    [DataMember(Order = 4)]
    public int Retries { get; set; }

    /// <summary>
    /// Gets or sets the number of ordering lanes used for connected clients.
    /// </summary>
    [DataMember(Order = 5)]
    public int MaxUnitOfOrder { get; set; }

    /// <summary>
    /// Gets or sets the idle socket timeout in seconds. Use -1 to disable it.
    /// </summary>
    [DataMember(Order = 9)]
    public int SocketTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the socket configuration applied to listener and accepted sockets.
    /// </summary>
    [DataMember(Order = 10)]
    public SocketSettings SocketSettings { get; set; }

    /// <summary>
    /// Gets or sets whether debug logging is enabled.
    /// </summary>
    [DataMember(Order = 11)]
    public bool DebugMode { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent accept operations.
    /// </summary>
    [DataMember(Order = 12)]
    public int ConcurrentAccepts { get; set; }

    /// <summary>
    /// Gets or sets the maximum queued outbound bytes per client.
    /// </summary>
    [DataMember(Order = 13)]
    public int MaxQueuedSendBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of listener bind retries.
    /// </summary>
    public int BindRetryCount
    {
        get => Retries;
        set => Retries = value;
    }

    /// <summary>
    /// Gets or sets the number of ordering lanes used for connected clients.
    /// </summary>
    public int OrderingLaneCount
    {
        get => MaxUnitOfOrder;
        set => MaxUnitOfOrder = value;
    }

    /// <summary>
    /// Gets or sets the idle socket timeout in seconds. Use -1 to disable it.
    /// </summary>
    public int IdleSocketTimeoutSeconds
    {
        get => SocketTimeoutSeconds;
        set => SocketTimeoutSeconds = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent accept operations.
    /// </summary>
    public int AcceptConcurrency
    {
        get => ConcurrentAccepts;
        set => ConcurrentAccepts = value;
    }

    /// <summary>
    /// Validates the configuration values.
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

        if (Retries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Retries), "Retries must be zero or greater.");
        }

        if (MaxUnitOfOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUnitOfOrder), "MaxUnitOfOrder must be greater than zero.");
        }

        if (ConcurrentAccepts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrentAccepts), "ConcurrentAccepts must be greater than zero.");
        }

        if (MaxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendBytes), "MaxQueuedSendBytes must be greater than zero.");
        }

        if (SocketSettings is null)
        {
            throw new ArgumentNullException(nameof(SocketSettings));
        }
    }

    /// <inheritdoc />
    public object Clone()
    {
        return new AsyncEventSettings(this);
    }
}
