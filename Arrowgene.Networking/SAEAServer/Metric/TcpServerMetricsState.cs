using System;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class TcpServerMetricsState
{
    private const int TransferSizeBucketCount = 7;
    private readonly long[] _disconnectsByReason;
    private readonly long[] _receiveSizeBuckets;
    private readonly long[] _sendSizeBuckets;
    private readonly long[] _socketErrorsByCode;
    private readonly int _socketErrorCodeMinimum;
    private int _captureEnabled;
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
        SocketError[] socketErrors = Enum.GetValues<SocketError>();
        int socketErrorCodeMinimum = int.MaxValue;
        int socketErrorCodeMaximum = int.MinValue;

        for (int index = 0; index < socketErrors.Length; index++)
        {
            int socketErrorCode = (int)socketErrors[index];
            if (socketErrorCode < socketErrorCodeMinimum)
            {
                socketErrorCodeMinimum = socketErrorCode;
            }

            if (socketErrorCode > socketErrorCodeMaximum)
            {
                socketErrorCodeMaximum = socketErrorCode;
            }
        }

        _disconnectsByReason = new long[Enum.GetValues<DisconnectReason>().Length];
        _receiveSizeBuckets = new long[TransferSizeBucketCount];
        _sendSizeBuckets = new long[TransferSizeBucketCount];
        _socketErrorCodeMinimum = socketErrorCodeMinimum;
        _socketErrorsByCode = new long[(socketErrorCodeMaximum - socketErrorCodeMinimum) + 1];
    }

    internal int DisconnectReasonCount => _disconnectsByReason.Length;

    internal int ReceiveSizeBucketCount => _receiveSizeBuckets.Length;

    internal int SendSizeBucketCount => _sendSizeBuckets.Length;

    internal int SocketErrorCodeMinimum => _socketErrorCodeMinimum;

    internal int SocketErrorCodeCount => _socketErrorsByCode.Length;

    internal void EnableCapture()
    {
        Volatile.Write(ref _captureEnabled, 1);
    }

    internal void DisableCapture()
    {
        Volatile.Write(ref _captureEnabled, 0);
    }

    internal bool IsCaptureEnabled()
    {
        return Volatile.Read(ref _captureEnabled) == 1;
    }

    internal void IncrementAcceptedConnections()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _acceptedConnections);
        Interlocked.Increment(ref _activeConnections);
    }

    internal void IncrementRejectedConnections()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _rejectedConnections);
    }

    internal void IncrementTimedOutConnections()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _timedOutConnections);
    }

    internal void IncrementSendQueueOverflows()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _sendQueueOverflows);
    }

    internal void IncrementSocketAcceptErrors()
    {
        IncrementSocketError(ref _socketAcceptErrors);
    }

    internal void RecordSocketAcceptError(SocketError socketError)
    {
        RecordSocketError(ref _socketAcceptErrors, socketError);
    }

    internal void RecordSocketReceiveError(SocketError socketError)
    {
        RecordSocketError(ref _socketReceiveErrors, socketError);
    }

    internal void RecordSocketSendError(SocketError socketError)
    {
        RecordSocketError(ref _socketSendErrors, socketError);
    }

    internal void RecordReceive(int bytesTransferred)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        if (bytesTransferred <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _receiveOperations);
        Interlocked.Add(ref _bytesReceived, bytesTransferred);
        Interlocked.Increment(ref _receiveSizeBuckets[GetTransferSizeBucketIndex(bytesTransferred)]);
    }

    internal void RecordSend(int bytesTransferred)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        if (bytesTransferred <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _sendOperations);
        Interlocked.Add(ref _bytesSent, bytesTransferred);
        Interlocked.Increment(ref _sendSizeBuckets[GetTransferSizeBucketIndex(bytesTransferred)]);
    }

    internal void FinalizeDisconnect(DisconnectReason disconnectReason)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _disconnectedConnections);
        Interlocked.Decrement(ref _activeConnections);
        Interlocked.Increment(ref _disconnectsByReason[(int)disconnectReason]);
    }

    internal void EnterAsyncCallback()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _inFlightAsyncCallbacks);
    }

    internal void ExitAsyncCallback()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

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
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _disconnectCleanupQueueDepth);
    }

    internal void DequeueDisconnectCleanup()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

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
        CopyCounterArray(
            _disconnectsByReason,
            destination,
            "Destination must be at least as large as the disconnect-reason counter array."
        );
    }

    internal void CopyReceiveSizeBuckets(long[] destination)
    {
        CopyCounterArray(
            _receiveSizeBuckets,
            destination,
            "Destination must be at least as large as the receive-size counter array."
        );
    }

    internal void CopySendSizeBuckets(long[] destination)
    {
        CopyCounterArray(
            _sendSizeBuckets,
            destination,
            "Destination must be at least as large as the send-size counter array."
        );
    }

    internal void CopySocketErrorsByCode(long[] destination)
    {
        CopyCounterArray(
            _socketErrorsByCode,
            destination,
            "Destination must be at least as large as the socket-error counter array."
        );
    }

    private void IncrementSocketError(ref long errorCounter)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref errorCounter);
    }

    private void RecordSocketError(ref long errorCounter, SocketError socketError)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref errorCounter);
        RecordSocketErrorCode(socketError);
    }

    private void RecordSocketErrorCode(SocketError socketError)
    {
        if (socketError == SocketError.Success)
        {
            return;
        }

        int index = ((int)socketError) - _socketErrorCodeMinimum;
        if ((uint)index >= (uint)_socketErrorsByCode.Length)
        {
            return;
        }

        Interlocked.Increment(ref _socketErrorsByCode[index]);
    }

    private static int GetTransferSizeBucketIndex(int bytesTransferred)
    {
        if (bytesTransferred <= 64)
        {
            return 0;
        }

        if (bytesTransferred <= 256)
        {
            return 1;
        }

        if (bytesTransferred <= 1024)
        {
            return 2;
        }

        if (bytesTransferred <= 4096)
        {
            return 3;
        }

        if (bytesTransferred <= 8192)
        {
            return 4;
        }

        if (bytesTransferred <= 16384)
        {
            return 5;
        }

        return 6;
    }

    private static void CopyCounterArray(long[] source, long[] destination, string lengthErrorMessage)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), lengthErrorMessage);
        }

        for (int index = 0; index < source.Length; index++)
        {
            destination[index] = Volatile.Read(ref source[index]);
        }
    }
}
