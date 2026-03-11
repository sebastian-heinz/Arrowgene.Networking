using System;
using System.Net;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Immutable snapshot of a client captured at a specific point in time.
/// </summary>
public readonly record struct ClientSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClientSnapshot"/> struct.
    /// </summary>
    /// <param name="clientId">The internal client identifier.</param>
    /// <param name="generation">The client generation used to detect recycled clients.</param>
    /// <param name="identity">The client identity used in logs.</param>
    /// <param name="remoteIpAddress">The remote IP address of the client.</param>
    /// <param name="port">The remote TCP port of the client.</param>
    /// <param name="isAlive">Whether the client was still considered alive when captured.</param>
    /// <param name="connectedAt">The UTC timestamp when the client connected.</param>
    /// <param name="lastReadMs">The tick count of the last successful receive operation.</param>
    /// <param name="lastWriteMs">The tick count of the last successful send operation.</param>
    /// <param name="bytesReceived">The total bytes received from the client.</param>
    /// <param name="bytesSent">The total bytes sent to the client.</param>
    /// <param name="pendingOperations">The number of socket operations still tracked for the client.</param>
    /// <param name="unitOfOrder">The ordering lane assigned to the client.</param>
    public ClientSnapshot(
        ushort clientId,
        uint generation,
        string identity,
        IPAddress remoteIpAddress,
        ushort port,
        bool isAlive,
        DateTime connectedAt,
        long lastReadMs,
        long lastWriteMs,
        ulong bytesReceived,
        ulong bytesSent,
        int pendingOperations,
        int unitOfOrder)
    {
        ClientId = clientId;
        Generation = generation;
        Identity = identity;
        RemoteIpAddress = remoteIpAddress;
        Port = port;
        IsAlive = isAlive;
        ConnectedAt = connectedAt;
        LastReadMs = lastReadMs;
        LastWriteMs = lastWriteMs;
        BytesReceived = bytesReceived;
        BytesSent = bytesSent;
        PendingOperations = pendingOperations;
        UnitOfOrder = unitOfOrder;
        UniqueId = UniqueIdManager.Pack(clientId, generation);
    }

    /// <summary>
    /// Gets the internal client identifier.
    /// </summary>
    public ushort ClientId { get; }

    /// <summary>
    /// Gets the client generation used to detect recycled clients.
    /// </summary>
    public uint Generation { get; }

    /// <summary>
    /// Gets the client identity used in logs.
    /// </summary>
    public string Identity { get; }

    /// <summary>
    /// Gets the remote IP address of the client.
    /// </summary>
    public IPAddress RemoteIpAddress { get; }

    /// <summary>
    /// Gets the remote TCP port of the client.
    /// </summary>
    public ushort Port { get; }

    /// <summary>
    /// Gets a value indicating whether the client was still considered alive when captured.
    /// </summary>
    public bool IsAlive { get; }

    /// <summary>
    /// Gets the UTC timestamp when the client connected.
    /// </summary>
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// Gets the tick count of the last successful receive operation.
    /// </summary>
    public long LastReadMs { get; }

    /// <summary>
    /// Gets the tick count of the last successful send operation.
    /// </summary>
    public long LastWriteMs { get; }

    /// <summary>
    /// Gets the total bytes received from the client.
    /// </summary>
    public ulong BytesReceived { get; }

    /// <summary>
    /// Gets the total bytes sent from the client.
    /// </summary>
    public ulong BytesSent { get; }

    /// <summary>
    /// Gets the number of socket operations still tracked for the client.
    /// </summary>
    public int PendingOperations { get; }

    /// <summary>
    /// Gets the ordering lane assigned to the client.
    /// </summary>
    public int UnitOfOrder { get; }

    /// <summary>
    /// Gets the packed unique identifier captured when the snapshot was created.
    /// </summary>
    public long UniqueId { get; }

    /// <summary>
    /// Deconstructs the snapshot into its component values.
    /// </summary>
    public void Deconstruct(
        out ushort clientId,
        out uint generation,
        out string identity,
        out IPAddress remoteIpAddress,
        out ushort port,
        out bool isAlive,
        out DateTime connectedAt,
        out long lastReadMs,
        out long lastWriteMs,
        out ulong bytesReceived,
        out ulong bytesSent,
        out int pendingOperations,
        out int unitOfOrder)
    {
        clientId = ClientId;
        generation = Generation;
        identity = Identity;
        remoteIpAddress = RemoteIpAddress;
        port = Port;
        isAlive = IsAlive;
        connectedAt = ConnectedAt;
        lastReadMs = LastReadMs;
        lastWriteMs = LastWriteMs;
        bytesReceived = BytesReceived;
        bytesSent = BytesSent;
        pendingOperations = PendingOperations;
        unitOfOrder = UnitOfOrder;
    }
}