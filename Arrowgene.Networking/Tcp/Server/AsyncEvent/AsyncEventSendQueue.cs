using System;
using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

internal sealed class AsyncEventSendQueue
{
    private readonly object _sync;
    private readonly Queue<byte[]> _pendingMessages;
    private readonly int _maxQueuedBytes;
    private byte[]? _currentMessage;
    private int _currentOffset;
    private int _queuedBytes;
    private bool _sendInProgress;

    internal AsyncEventSendQueue(int maxQueuedBytes)
    {
        if (maxQueuedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
        }

        _sync = new object();
        _pendingMessages = new Queue<byte[]>();
        _maxQueuedBytes = maxQueuedBytes;
    }

    internal bool TryEnqueueCopy(byte[] data, out bool startSend, out bool queueOverflow)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        startSend = false;
        queueOverflow = false;

        if (data.Length == 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (data.Length > _maxQueuedBytes - _queuedBytes)
            {
                queueOverflow = true;
                return false;
            }

            byte[] copy = GC.AllocateUninitializedArray<byte>(data.Length);
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);

            _pendingMessages.Enqueue(copy);
            _queuedBytes += copy.Length;

            if (_sendInProgress)
            {
                return true;
            }

            _sendInProgress = true;
            startSend = true;
            return true;
        }
    }

    internal bool TryCopyNextChunk(byte[] targetBuffer, int targetOffset, int maxChunkSize, out int chunkSize)
    {
        if (targetBuffer is null)
        {
            throw new ArgumentNullException(nameof(targetBuffer));
        }

        lock (_sync)
        {
            if (_currentMessage is null)
            {
                if (_pendingMessages.Count == 0)
                {
                    _sendInProgress = false;
                    chunkSize = 0;
                    return false;
                }

                _currentMessage = _pendingMessages.Dequeue();
                _currentOffset = 0;
            }

            int remaining = _currentMessage.Length - _currentOffset;
            chunkSize = remaining <= maxChunkSize ? remaining : maxChunkSize;
            Buffer.BlockCopy(_currentMessage, _currentOffset, targetBuffer, targetOffset, chunkSize);
            return true;
        }
    }

    internal bool CompleteSend(int bytesTransferred)
    {
        lock (_sync)
        {
            if (_currentMessage is null)
            {
                return false;
            }

            int remaining = _currentMessage.Length - _currentOffset;
            if (bytesTransferred <= 0 || bytesTransferred > remaining)
            {
                throw new InvalidOperationException("Completed send does not match the queued payload state.");
            }

            _currentOffset += bytesTransferred;
            _queuedBytes -= bytesTransferred;

            if (_currentOffset < _currentMessage.Length)
            {
                return true;
            }

            _currentMessage = null;
            _currentOffset = 0;

            if (_pendingMessages.Count > 0)
            {
                return true;
            }

            _sendInProgress = false;
            return false;
        }
    }

    internal void ClearPendingMessages()
    {
        lock (_sync)
        {
            _pendingMessages.Clear();

            if (_currentMessage is null)
            {
                _queuedBytes = 0;
                _sendInProgress = false;
                _currentOffset = 0;
                return;
            }

            _queuedBytes = _currentMessage.Length - _currentOffset;
            _sendInProgress = true;
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _pendingMessages.Clear();
            _currentMessage = null;
            _currentOffset = 0;
            _queuedBytes = 0;
            _sendInProgress = false;
        }
    }
}
