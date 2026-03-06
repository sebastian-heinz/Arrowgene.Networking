using System;
using System.Net;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

/// <summary>
/// Represents a connected TCP session managed by <see cref="SaeaServer"/>.
/// </summary>
public interface ISaeaSession
{
    /// <summary>
    /// Stable session slot identifier within the preallocated pool.
    /// </summary>
    int SessionId { get; }

    /// <summary>
    /// Human-readable identifier for diagnostics.
    /// </summary>
    string Identity { get; }

    /// <summary>
    /// Remote IPv4 or IPv6 address.
    /// </summary>
    IPAddress RemoteIpAddress { get; }

    /// <summary>
    /// Remote TCP port.
    /// </summary>
    ushort RemotePort { get; }

    /// <summary>
    /// UTC timestamp when the connection was accepted.
    /// </summary>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// Environment tick count for the last successful receive operation.
    /// </summary>
    long LastReceiveTicks { get; }

    /// <summary>
    /// Environment tick count for the last successful send operation.
    /// </summary>
    long LastSendTicks { get; }

    /// <summary>
    /// Total bytes received during the lifetime of the session.
    /// </summary>
    ulong BytesReceived { get; }

    /// <summary>
    /// Total bytes sent during the lifetime of the session.
    /// </summary>
    ulong BytesSent { get; }

    /// <summary>
    /// Indicates whether the session is still connected and can send or receive.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Copies and enqueues a payload for sending.
    /// </summary>
    /// <param name="data">The payload to copy.</param>
    void Send(byte[] data);

    /// <summary>
    /// Copies and enqueues a payload for sending.
    /// </summary>
    /// <param name="data">The payload to copy.</param>
    void Send(ReadOnlySpan<byte> data);

    /// <summary>
    /// Initiates a graceful disconnect.
    /// </summary>
    void Close();
}
