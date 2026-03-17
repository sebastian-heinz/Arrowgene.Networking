using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal sealed class SharedSendQueue : ISendQueue
{
    private readonly object _sync;
    private readonly Queue<(byte[] Buffer, int Length)> _pendingMessages;
    private readonly int _maxQueuedBytes;
    private byte[]? _currentBuffer;
    private int _currentLength;
    private int _currentOffset;
    private int _queuedBytes;
    private bool _sendInProgress;

    internal SharedSendQueue(int maxQueuedBytes)
    {
        if (maxQueuedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
        }

        _sync = new object();
        _pendingMessages = new Queue<(byte[] Buffer, int Length)>();
        _maxQueuedBytes = maxQueuedBytes;
    }

    public int QueuedBytes
    {
        get
        {
            lock (_sync)
            {
                return _queuedBytes;
            }
        }
    }

    public EnqueueResult Enqueue(byte[] data, int offset, int length)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (offset < 0 || length <= 0 || data.Length - offset < length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        lock (_sync)
        {
            if (length > _maxQueuedBytes - _queuedBytes)
            {
                return EnqueueResult.Overflow;
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(data, offset, rented, 0, length);

            _pendingMessages.Enqueue((rented, length));
            _queuedBytes += length;

            if (_sendInProgress)
            {
                return EnqueueResult.Queued;
            }

            _sendInProgress = true;
            return EnqueueResult.SendNow;
        }
    }

    public bool TryGetNextChunk(SocketAsyncEventArgs sendEventArgs, out int chunkSize)
    {
        if (sendEventArgs is null)
        {
            throw new ArgumentNullException(nameof(sendEventArgs));
        }

        lock (_sync)
        {
            if (_currentBuffer is null)
            {
                if (_pendingMessages.Count == 0)
                {
                    _sendInProgress = false;
                    chunkSize = 0;
                    return false;
                }

                (byte[] Buffer, int Length) next = _pendingMessages.Dequeue();
                _currentBuffer = next.Buffer;
                _currentLength = next.Length;
                _currentOffset = 0;
            }

            chunkSize = _currentLength - _currentOffset;
            sendEventArgs.SetBuffer(_currentBuffer, _currentOffset, chunkSize);
            return true;
        }
    }

    public bool CompleteSend(int bytesTransferred)
    {
        lock (_sync)
        {
            if (_currentBuffer is null)
            {
                throw new InvalidOperationException("Completed send does not match the queued payload state.");
            }

            int remaining = _currentLength - _currentOffset;
            if (bytesTransferred <= 0 || bytesTransferred > remaining)
            {
                throw new InvalidOperationException("Completed send does not match the queued payload state.");
            }

            _currentOffset += bytesTransferred;
            _queuedBytes -= bytesTransferred;

            if (_currentOffset < _currentLength)
            {
                return true;
            }

            byte[] completedBuffer = _currentBuffer;
            _currentBuffer = null;
            _currentLength = 0;
            _currentOffset = 0;
            ArrayPool<byte>.Shared.Return(completedBuffer);

            if (_pendingMessages.Count > 0)
            {
                return true;
            }

            _sendInProgress = false;
            return false;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_currentBuffer is not null)
            {
                byte[] currentBuffer = _currentBuffer;
                _currentBuffer = null;
                ArrayPool<byte>.Shared.Return(currentBuffer);
            }

            while (_pendingMessages.Count > 0)
            {
                (byte[] Buffer, int Length) pending = _pendingMessages.Dequeue();
                ArrayPool<byte>.Shared.Return(pending.Buffer);
            }

            _currentLength = 0;
            _currentOffset = 0;
            _queuedBytes = 0;
            _sendInProgress = false;
        }
    }
}
