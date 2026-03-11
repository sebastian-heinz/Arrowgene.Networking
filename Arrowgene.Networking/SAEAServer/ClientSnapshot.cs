using System;
using System.Net;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Immutable snapshot of a client captured at a specific point in time.
/// </summary>
/// <param name="ClientId">The internal client identifier.</param>
/// <param name="Generation">The client generation used to detect recycled clients.</param>
/// <param name="Identity">The client identity used in logs.</param>
/// <param name="RemoteIpAddress">The remote IP address of the client.</param>
/// <param name="Port">The remote TCP port of the client.</param>
/// <param name="IsAlive">Whether the client was still considered alive when captured.</param>
/// <param name="ConnectedAt">The UTC timestamp when the client connected.</param>
/// <param name="LastReadMs">The tick count of the last successful receive operation.</param>
/// <param name="LastWriteMs">The tick count of the last successful send operation.</param>
/// <param name="BytesReceived">The total bytes received from the client.</param>
/// <param name="BytesSent">The total bytes sent to the client.</param>
/// <param name="PendingOperations">The number of socket operations still tracked for the client.</param>
/// <param name="UnitOfOrder">The ordering lane assigned to the client.</param>
public readonly record struct ClientSnapshot(
    ushort ClientId,
    uint Generation,
    string Identity,
    IPAddress RemoteIpAddress,
    ushort Port,
    bool IsAlive,
    DateTime ConnectedAt,
    long LastReadMs,
    long LastWriteMs,
    ulong BytesReceived,
    ulong BytesSent,
    int PendingOperations,
    int UnitOfOrder
)
{
    public bool IsConnected()
    {
        return IsAlive;
    }
}