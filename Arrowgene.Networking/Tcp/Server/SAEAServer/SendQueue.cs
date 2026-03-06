using System;
using System.Buffers;
using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SendQueue
{
    private readonly ArrayPool<byte> _bufferPool;
    private readonly object _sendLock;
    private readonly Queue<SendBufferLease> _queue;

    private byte[]? _currentBuffer;
    private int _currentLength;
    private int _currentOffset;
    private int _queuedBytes;
    private bool _sendInProgress;

    internal SendQueue(int maxQueuedBytes)
    {
        if (maxQueuedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
        }

        MaxQueuedBytes = maxQueuedBytes;
        _bufferPool = ArrayPool<byte>.Shared;
        _sendLock = new object();
        _queue = new Queue<SendBufferLease>();
    }

    internal int MaxQueuedBytes { get; }

    internal int QueuedBytes
    {
        get
        {
            lock (_sendLock)
            {
                return _queuedBytes;
            }
        }
    }

    internal SendQueueEnqueueResult Enqueue(ReadOnlySpan<byte> data)
    {
        lock (_sendLock)
        {
            if (data.Length == 0)
            {
                return SendQueueEnqueueResult.Rejected;
            }

            if (data.Length > MaxQueuedBytes - _queuedBytes)
            {
                return SendQueueEnqueueResult.Overflow;
            }

            byte[] rentedBuffer = _bufferPool.Rent(data.Length);
            data.CopyTo(rentedBuffer.AsSpan(0, data.Length));

            _queue.Enqueue(new SendBufferLease(rentedBuffer, data.Length));
            _queuedBytes += data.Length;

            if (_sendInProgress)
            {
                return SendQueueEnqueueResult.Queued;
            }

            _sendInProgress = true;
            if (_currentBuffer is null)
            {
                PromoteNextBufferLocked();
            }

            return SendQueueEnqueueResult.Started;
        }
    }

    internal bool TryGetChunk(int maxChunkSize, out byte[]? data, out int offset, out int count)
    {
        lock (_sendLock)
        {
            if (maxChunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxChunkSize));
            }

            if (_currentBuffer is null)
            {
                PromoteNextBufferLocked();
            }

            if (_currentBuffer is null)
            {
                data = null;
                offset = 0;
                count = 0;
                return false;
            }

            data = _currentBuffer;
            offset = _currentOffset;
            count = Math.Min(maxChunkSize, _currentLength - _currentOffset);
            return true;
        }
    }

    internal bool CompleteChunk(int bytesTransferred)
    {
        lock (_sendLock)
        {
            if (_currentBuffer is null)
            {
                return false;
            }

            if (bytesTransferred <= 0)
            {
                return false;
            }

            _currentOffset += bytesTransferred;
            _queuedBytes -= bytesTransferred;
            if (_queuedBytes < 0)
            {
                _queuedBytes = 0;
            }

            if (_currentOffset < _currentLength)
            {
                return true;
            }

            ReturnCurrentBufferLocked();
            PromoteNextBufferLocked();
            return _currentBuffer is not null;
        }
    }

    internal void ClearQueued()
    {
        lock (_sendLock)
        {
            while (_queue.Count > 0)
            {
                SendBufferLease bufferLease = _queue.Dequeue();
                _bufferPool.Return(bufferLease.Buffer);
            }

            if (_currentBuffer is not null)
            {
                _queuedBytes = _currentLength - _currentOffset;
                _sendInProgress = true;
                return;
            }

            _queuedBytes = 0;
            _sendInProgress = false;
        }
    }

    internal void Reset()
    {
        lock (_sendLock)
        {
            while (_queue.Count > 0)
            {
                SendBufferLease bufferLease = _queue.Dequeue();
                _bufferPool.Return(bufferLease.Buffer);
            }

            ReturnCurrentBufferLocked();
            _queuedBytes = 0;
            _sendInProgress = false;
        }
    }

    private void PromoteNextBufferLocked()
    {
        if (_queue.Count == 0)
        {
            _sendInProgress = false;
            return;
        }

        SendBufferLease bufferLease = _queue.Dequeue();
        _currentBuffer = bufferLease.Buffer;
        _currentLength = bufferLease.Length;
        _currentOffset = 0;
        _sendInProgress = true;
    }

    private void ReturnCurrentBufferLocked()
    {
        if (_currentBuffer is null)
        {
            _currentLength = 0;
            _currentOffset = 0;
            return;
        }

        _bufferPool.Return(_currentBuffer);
        _currentBuffer = null;
        _currentLength = 0;
        _currentOffset = 0;
    }
}
