using System;
using System.Runtime.Serialization;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Configuration for <see cref="Server"/>.
/// </summary>
[DataContract]
public sealed class ServerSettings : ICloneable
{
    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    public ServerSettings()
    {
        Identity = string.Empty;
        MaxConnections = 100;
        BufferSize = 2000;
        OrderingLaneCount = 4;
        ConcurrentAccepts = 10;
        MaxQueuedSendBytes = 16 * 1024 * 1024;
        ListenSocketRetries = 5;
        ListenSocketSettings = new SocketSettings();
        ClientSocketTimeoutSeconds = -1;
        ClientSocketSettings = new SocketSettings();
        DebugMode = false;
    }

    /// <summary>
    /// Creates a deep copy of an existing configuration.
    /// </summary>
    /// <param name="settings">The configuration to copy.</param>
    public ServerSettings(ServerSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        Identity = settings.Identity;
        MaxConnections = settings.MaxConnections;
        BufferSize = settings.BufferSize;
        OrderingLaneCount = settings.OrderingLaneCount;
        ConcurrentAccepts = settings.ConcurrentAccepts;
        MaxQueuedSendBytes = settings.MaxQueuedSendBytes;
        ListenSocketRetries = settings.ListenSocketRetries;
        ListenSocketSettings = new SocketSettings(settings.ListenSocketSettings);
        ClientSocketTimeoutSeconds = settings.ClientSocketTimeoutSeconds;
        ClientSocketSettings = new SocketSettings(settings.ClientSocketSettings);
        DebugMode = settings.DebugMode;
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
    [DataMember(Order = 2)]
    public int BufferSize { get; set; }

    /// <summary>
    /// Gets or sets the number of ordering lanes used for connected clients.
    /// </summary>
    [DataMember(Order = 3)]
    public int OrderingLaneCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent accept operations.
    /// </summary>
    [DataMember(Order = 4)]
    public int ConcurrentAccepts { get; set; }

    /// <summary>
    /// Gets or sets the maximum queued outbound bytes per client.
    /// </summary>
    [DataMember(Order = 5)]
    public int MaxQueuedSendBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of listener bind retries.
    /// </summary>
    [DataMember(Order = 20)]
    public int ListenSocketRetries { get; set; }

    /// <summary>
    /// Gets or sets the socket configuration applied to listener and accepted sockets.
    /// </summary>
    [DataMember(Order = 21)]
    public SocketSettings ListenSocketSettings { get; set; }

    /// <summary>
    /// Gets or sets the idle socket timeout in seconds. Use -1 to disable it.
    /// </summary>
    [DataMember(Order = 40)]
    public int ClientSocketTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the socket configuration applied to listener and accepted sockets.
    /// </summary>
    [DataMember(Order = 41)]
    public SocketSettings ClientSocketSettings { get; set; }

    /// <summary>
    /// Gets or sets whether debug logging is enabled.
    /// </summary>
    [DataMember(Order = 90)]
    public bool DebugMode { get; set; }

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

        if (OrderingLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OrderingLaneCount), "OrderingLaneCount must be greater than zero.");
        }
        
        if (ConcurrentAccepts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrentAccepts),
                "ConcurrentAccepts must be greater than zero.");
        }

        if (MaxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendBytes),
                "MaxQueuedSendBytes must be greater than zero.");
        }

        if (ListenSocketRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ListenSocketRetries),
                "ListenSocketRetries must be zero or greater.");
        }

        if (ListenSocketSettings is null)
        {
            throw new ArgumentNullException(nameof(ListenSocketSettings));
        }

        if (ClientSocketSettings is null)
        {
            throw new ArgumentNullException(nameof(ClientSocketSettings));
        }
    }

    /// <inheritdoc />
    public object Clone()
    {
        return new ServerSettings(this);
    }
}