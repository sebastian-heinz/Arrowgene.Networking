using System;
using System.Threading;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class TcpServerMetricsState
{
    private readonly long[] _disconnectsByReason;
    private long _acceptedConnections;
    private long _rejectedConnections;
    private long _activeConnections;
    private long _disconnectedConnections;
    private long _timedOutConnections;
    private long _sendQueueOverflows;
    private long _socketAcceptErrors;
    private long _socketReceiveErrors;
    private long _socketSendErrors;
    private long _receiveOperations;
    private long _sendOperations;
    private long _bytesReceived;
    private long _bytesSent;
    private long _inFlightAsyncCallbacks;
    private long _disconnectCleanupQueueDepth;

    internal TcpServerMetricsState()
    {
        _disconnectsByReason = new long[Enum.GetValues<DisconnectReason>().Length];
    }

    internal int DisconnectReasonCount => _disconnectsByReason.Length;

    internal void IncrementAcceptedConnections()
    {
        Interlocked.Increment(ref _acceptedConnections);
        Interlocked.Increment(ref _activeConnections);
    }

    internal void IncrementRejectedConnections()
    {
        Interlocked.Increment(ref _rejectedConnections);
    }

    internal void IncrementTimedOutConnections()
    {
        Interlocked.Increment(ref _timedOutConnections);
    }

    internal void IncrementSendQueueOverflows()
    {
        Interlocked.Increment(ref _sendQueueOverflows);
    }

    internal void IncrementSocketAcceptErrors()
    {
        Interlocked.Increment(ref _socketAcceptErrors);
    }

    internal void IncrementSocketReceiveErrors()
    {
        Interlocked.Increment(ref _socketReceiveErrors);
    }

    internal void IncrementSocketSendErrors()
    {
        Interlocked.Increment(ref _socketSendErrors);
    }

    internal void RecordReceive(int bytesTransferred)
    {
        if (bytesTransferred <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _receiveOperations);
        Interlocked.Add(ref _bytesReceived, bytesTransferred);
    }

    internal void RecordSend(int bytesTransferred)
    {
        if (bytesTransferred <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _sendOperations);
        Interlocked.Add(ref _bytesSent, bytesTransferred);
    }

    internal void FinalizeDisconnect(DisconnectReason disconnectReason)
    {
        Interlocked.Increment(ref _disconnectedConnections);
        Interlocked.Decrement(ref _activeConnections);
        Interlocked.Increment(ref _disconnectsByReason[(int)disconnectReason]);
    }

    internal void EnterAsyncCallback()
    {
        Interlocked.Increment(ref _inFlightAsyncCallbacks);
    }

    internal void ExitAsyncCallback()
    {
        long current;
        do
        {
            current = Volatile.Read(ref _inFlightAsyncCallbacks);
            if (current <= 0)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _inFlightAsyncCallbacks, current - 1, current) != current);
    }

    internal void EnqueueDisconnectCleanup()
    {
        Interlocked.Increment(ref _disconnectCleanupQueueDepth);
    }

    internal void DequeueDisconnectCleanup()
    {
        long current;
        do
        {
            current = Volatile.Read(ref _disconnectCleanupQueueDepth);
            if (current <= 0)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _disconnectCleanupQueueDepth, current - 1, current) != current);
    }

    internal void ResetCurrentGauges()
    {
        Interlocked.Exchange(ref _activeConnections, 0);
        Interlocked.Exchange(ref _inFlightAsyncCallbacks, 0);
        Interlocked.Exchange(ref _disconnectCleanupQueueDepth, 0);
    }

    internal long GetAcceptedConnections()
    {
        return Interlocked.Read(ref _acceptedConnections);
    }

    internal long GetRejectedConnections()
    {
        return Interlocked.Read(ref _rejectedConnections);
    }

    internal long GetActiveConnections()
    {
        return Interlocked.Read(ref _activeConnections);
    }

    internal long GetDisconnectedConnections()
    {
        return Interlocked.Read(ref _disconnectedConnections);
    }

    internal long GetTimedOutConnections()
    {
        return Interlocked.Read(ref _timedOutConnections);
    }

    internal long GetSendQueueOverflows()
    {
        return Interlocked.Read(ref _sendQueueOverflows);
    }

    internal long GetSocketAcceptErrors()
    {
        return Interlocked.Read(ref _socketAcceptErrors);
    }

    internal long GetSocketReceiveErrors()
    {
        return Interlocked.Read(ref _socketReceiveErrors);
    }

    internal long GetSocketSendErrors()
    {
        return Interlocked.Read(ref _socketSendErrors);
    }

    internal long GetReceiveOperations()
    {
        return Interlocked.Read(ref _receiveOperations);
    }

    internal long GetSendOperations()
    {
        return Interlocked.Read(ref _sendOperations);
    }

    internal long GetBytesReceived()
    {
        return Interlocked.Read(ref _bytesReceived);
    }

    internal long GetBytesSent()
    {
        return Interlocked.Read(ref _bytesSent);
    }

    internal long GetInFlightAsyncCallbacks()
    {
        return Interlocked.Read(ref _inFlightAsyncCallbacks);
    }

    internal long GetDisconnectCleanupQueueDepth()
    {
        return Interlocked.Read(ref _disconnectCleanupQueueDepth);
    }

    internal void CopyDisconnectsByReason(long[] destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < _disconnectsByReason.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination),
                "Destination must be at least as large as the disconnect-reason counter array.");
        }

        for (int index = 0; index < _disconnectsByReason.Length; index++)
        {
            destination[index] = Volatile.Read(ref _disconnectsByReason[index]);
        }
    }
}
