using System;
using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

internal sealed class AsyncEventServerSendQueue
{
    private readonly object _sync;
    private readonly Queue<byte[]> _messages;
    private readonly int _maxQueuedBytes;
    private int _queuedBytes;
    private int _headOffset;
    private bool _sendInProgress;

    internal AsyncEventServerSendQueue(int maxQueuedBytes)
    {
        if (maxQueuedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
        }

        _sync = new object();
        _messages = new Queue<byte[]>();
        _maxQueuedBytes = maxQueuedBytes;
    }

    internal int MaxQueuedBytes => _maxQueuedBytes;

    internal int QueuedBytes
    {
        get
        {
            lock (_sync)
            {
                return _queuedBytes;
            }
        }
    }

    internal bool EnqueueCopy(ReadOnlySpan<byte> payload, out bool startSend, out bool overflow)
    {
        startSend = false;
        overflow = false;

        if (payload.Length == 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_queuedBytes > _maxQueuedBytes - payload.Length)
            {
                overflow = true;
                return false;
            }

            byte[] copy = GC.AllocateUninitializedArray<byte>(payload.Length);
            payload.CopyTo(copy);

            _messages.Enqueue(copy);
            _queuedBytes += copy.Length;
            startSend = !_sendInProgress;
            if (startSend)
            {
                _sendInProgress = true;
            }

            return true;
        }
    }

    internal bool TryCopyNextChunk(byte[] targetBuffer, int targetOffset, int maxCount, out int chunkSize)
    {
        lock (_sync)
        {
            if (_messages.Count == 0)
            {
                _sendInProgress = false;
                chunkSize = 0;
                return false;
            }

            byte[] current = _messages.Peek();
            int remaining = current.Length - _headOffset;
            chunkSize = remaining > maxCount ? maxCount : remaining;
            Buffer.BlockCopy(current, _headOffset, targetBuffer, targetOffset, chunkSize);
            return true;
        }
    }

    internal bool CompleteSend(int bytesTransferred)
    {
        lock (_sync)
        {
            if (_messages.Count == 0)
            {
                _sendInProgress = false;
                return false;
            }

            byte[] current = _messages.Peek();
            int remaining = current.Length - _headOffset;
            if (bytesTransferred <= 0 || bytesTransferred > remaining)
            {
                throw new InvalidOperationException("The completed send size does not match the queued payload state.");
            }

            _headOffset += bytesTransferred;
            _queuedBytes -= bytesTransferred;

            if (_headOffset == current.Length)
            {
                _messages.Dequeue();
                _headOffset = 0;
            }

            if (_messages.Count == 0)
            {
                _sendInProgress = false;
                return false;
            }

            return true;
        }
    }

    internal void DropBacklog()
    {
        lock (_sync)
        {
            if (_messages.Count == 0)
            {
                _headOffset = 0;
                _queuedBytes = 0;
                _sendInProgress = false;
                return;
            }

            if (!_sendInProgress)
            {
                _messages.Clear();
                _headOffset = 0;
                _queuedBytes = 0;
                return;
            }

            byte[] current = _messages.Dequeue();
            _messages.Clear();
            _messages.Enqueue(current);
            _queuedBytes = current.Length - _headOffset;
        }
    }

    internal void Reset()
    {
        lock (_sync)
        {
            _messages.Clear();
            _queuedBytes = 0;
            _headOffset = 0;
            _sendInProgress = false;
        }
    }
}
