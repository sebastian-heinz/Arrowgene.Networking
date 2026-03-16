using System;
using System.Net.Sockets;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.SAEAServer.Metric;

/// <summary>
/// Represents an immutable point-in-time view of server metrics.
/// </summary>
public readonly struct TcpServerMetricsSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TcpServerMetricsSnapshot"/> struct.
    /// </summary>
    /// <param name="timestampUtc">The UTC timestamp when the snapshot was published.</param>
    /// <param name="acceptedConnections">The total number of accepted connections.</param>
    /// <param name="rejectedConnections">The total number of rejected connections.</param>
    /// <param name="activeConnections">The current number of active connections.</param>
    /// <param name="disconnectedConnections">The total number of finalized disconnects.</param>
    /// <param name="timedOutConnections">The total number of timeout-initiated disconnects.</param>
    /// <param name="sendQueueOverflows">The total number of send queue overflow events.</param>
    /// <param name="socketAcceptErrors">The total number of accept-path socket errors.</param>
    /// <param name="socketReceiveErrors">The total number of receive-path socket errors.</param>
    /// <param name="socketSendErrors">The total number of send-path socket errors.</param>
    /// <param name="receiveOperations">The total number of successful receive operations.</param>
    /// <param name="sendOperations">The total number of successful send operations.</param>
    /// <param name="bytesReceived">The total number of bytes received.</param>
    /// <param name="bytesSent">The total number of bytes sent.</param>
    /// <param name="receiveBytesPerSecond">The derived inbound byte rate for the most recent sample interval.</param>
    /// <param name="sendBytesPerSecond">The derived outbound byte rate for the most recent sample interval.</param>
    /// <param name="inFlightAsyncCallbacks">The current number of in-flight async socket callbacks.</param>
    /// <param name="disconnectCleanupQueueDepth">The current number of queued deferred disconnect cleanups.</param>
    /// <param name="acceptPoolAvailable">The current number of available accept event args in the accept pool.</param>
    /// <param name="availableClientSlots">The current number of available pooled client slots.</param>
    /// <param name="disconnectsByReason">Disconnect counters indexed by <see cref="DisconnectReason"/>.</param>
    /// <param name="laneActiveConnections">Current connection counts indexed by ordering lane.</param>
    /// <param name="receiveSizeBuckets">Receive-size histogram buckets.</param>
    /// <param name="sendSizeBuckets">Send-size histogram buckets.</param>
    /// <param name="socketErrorsByCode">Socket error counters indexed by the raw <see cref="SocketError"/> value offset from <paramref name="socketErrorCodeMinimum"/>.</param>
    /// <param name="socketErrorCodeMinimum">The minimum raw <see cref="SocketError"/> value represented in <paramref name="socketErrorsByCode"/>.</param>
    public TcpServerMetricsSnapshot(
        DateTime timestampUtc,
        long acceptedConnections,
        long rejectedConnections,
        long activeConnections,
        long disconnectedConnections,
        long timedOutConnections,
        long sendQueueOverflows,
        long socketAcceptErrors,
        long socketReceiveErrors,
        long socketSendErrors,
        long receiveOperations,
        long sendOperations,
        long bytesReceived,
        long bytesSent,
        double receiveBytesPerSecond,
        double sendBytesPerSecond,
        long inFlightAsyncCallbacks,
        long disconnectCleanupQueueDepth,
        long acceptPoolAvailable,
        long availableClientSlots,
        long[] disconnectsByReason,
        long[] laneActiveConnections,
        long[] receiveSizeBuckets,
        long[] sendSizeBuckets,
        long[] socketErrorsByCode,
        int socketErrorCodeMinimum)
    {
        TimestampUtc = timestampUtc;
        AcceptedConnections = acceptedConnections;
        RejectedConnections = rejectedConnections;
        ActiveConnections = activeConnections;
        DisconnectedConnections = disconnectedConnections;
        TimedOutConnections = timedOutConnections;
        SendQueueOverflows = sendQueueOverflows;
        SocketAcceptErrors = socketAcceptErrors;
        SocketReceiveErrors = socketReceiveErrors;
        SocketSendErrors = socketSendErrors;
        ReceiveOperations = receiveOperations;
        SendOperations = sendOperations;
        BytesReceived = bytesReceived;
        BytesSent = bytesSent;
        ReceiveBytesPerSecond = receiveBytesPerSecond;
        SendBytesPerSecond = sendBytesPerSecond;
        InFlightAsyncCallbacks = inFlightAsyncCallbacks;
        DisconnectCleanupQueueDepth = disconnectCleanupQueueDepth;
        AcceptPoolAvailable = acceptPoolAvailable;
        AvailableClientSlots = availableClientSlots;
        DisconnectsByReason = disconnectsByReason ?? throw new ArgumentNullException(nameof(disconnectsByReason));
        LaneActiveConnections = laneActiveConnections ?? throw new ArgumentNullException(nameof(laneActiveConnections));
        ReceiveSizeBuckets = receiveSizeBuckets ?? throw new ArgumentNullException(nameof(receiveSizeBuckets));
        SendSizeBuckets = sendSizeBuckets ?? throw new ArgumentNullException(nameof(sendSizeBuckets));
        SocketErrorsByCode = socketErrorsByCode ?? throw new ArgumentNullException(nameof(socketErrorsByCode));
        SocketErrorCodeMinimum = socketErrorCodeMinimum;
    }

    /// <summary>
    /// Gets the UTC timestamp when the snapshot was published.
    /// </summary>
    public DateTime TimestampUtc { get; }

    /// <summary>
    /// Gets the total number of accepted connections.
    /// </summary>
    public long AcceptedConnections { get; }

    /// <summary>
    /// Gets the total number of rejected connections.
    /// </summary>
    public long RejectedConnections { get; }

    /// <summary>
    /// Gets the current number of active connections.
    /// </summary>
    public long ActiveConnections { get; }

    /// <summary>
    /// Gets the total number of finalized disconnects.
    /// </summary>
    public long DisconnectedConnections { get; }

    /// <summary>
    /// Gets the total number of timeout-initiated disconnects.
    /// </summary>
    public long TimedOutConnections { get; }

    /// <summary>
    /// Gets the total number of send queue overflow events.
    /// </summary>
    public long SendQueueOverflows { get; }

    /// <summary>
    /// Gets the total number of accept-path socket errors.
    /// </summary>
    public long SocketAcceptErrors { get; }

    /// <summary>
    /// Gets the total number of receive-path socket errors.
    /// </summary>
    public long SocketReceiveErrors { get; }

    /// <summary>
    /// Gets the total number of send-path socket errors.
    /// </summary>
    public long SocketSendErrors { get; }

    /// <summary>
    /// Gets the total number of successful receive operations.
    /// </summary>
    public long ReceiveOperations { get; }

    /// <summary>
    /// Gets the total number of successful send operations.
    /// </summary>
    public long SendOperations { get; }

    /// <summary>
    /// Gets the total number of bytes received.
    /// </summary>
    public long BytesReceived { get; }

    /// <summary>
    /// Gets the total number of bytes sent.
    /// </summary>
    public long BytesSent { get; }

    /// <summary>
    /// Gets the inbound byte rate for the most recent sample interval.
    /// </summary>
    public double ReceiveBytesPerSecond { get; }

    /// <summary>
    /// Gets the outbound byte rate for the most recent sample interval.
    /// </summary>
    public double SendBytesPerSecond { get; }

    /// <summary>
    /// Gets the current number of in-flight async socket callbacks.
    /// </summary>
    public long InFlightAsyncCallbacks { get; }

    /// <summary>
    /// Gets the current number of queued deferred disconnect cleanups.
    /// </summary>
    public long DisconnectCleanupQueueDepth { get; }

    /// <summary>
    /// Gets the current number of available accept event args in the accept pool.
    /// </summary>
    public long AcceptPoolAvailable { get; }

    /// <summary>
    /// Gets the current number of available pooled client slots.
    /// </summary>
    public long AvailableClientSlots { get; }

    /// <summary>
    /// Gets disconnect counters indexed by <see cref="DisconnectReason"/>.
    /// </summary>
    public ReadOnlyMemory<long> DisconnectsByReason { get; }

    /// <summary>
    /// Gets current connection counts indexed by ordering lane.
    /// </summary>
    public ReadOnlyMemory<long> LaneActiveConnections { get; }

    /// <summary>
    /// Gets receive-size histogram buckets using the ranges 0..64, 65..256, 257..1024, 1025..4096, 4097..8192, 8193..16384, and 16385+.
    /// </summary>
    public ReadOnlyMemory<long> ReceiveSizeBuckets { get; }

    /// <summary>
    /// Gets send-size histogram buckets using the ranges 0..64, 65..256, 257..1024, 1025..4096, 4097..8192, 8193..16384, and 16385+.
    /// </summary>
    public ReadOnlyMemory<long> SendSizeBuckets { get; }

    /// <summary>
    /// Gets socket error counters indexed by the raw <see cref="SocketError"/> value offset from <see cref="SocketErrorCodeMinimum"/>.
    /// </summary>
    public ReadOnlyMemory<long> SocketErrorsByCode { get; }

    /// <summary>
    /// Gets the minimum raw <see cref="SocketError"/> value represented in <see cref="SocketErrorsByCode"/>.
    /// </summary>
    public int SocketErrorCodeMinimum { get; }

    /// <summary>
    /// Gets the count recorded for a specific <see cref="SocketError"/> code.
    /// </summary>
    /// <param name="socketError">The socket error to query.</param>
    /// <returns>The number of times the error has been recorded in the snapshot.</returns>
    public long GetSocketErrorCount(SocketError socketError)
    {
        int index = ((int)socketError) - SocketErrorCodeMinimum;
        if ((uint)index >= (uint)SocketErrorsByCode.Length)
        {
            return 0;
        }

        return SocketErrorsByCode.Span[index];
    }
}
