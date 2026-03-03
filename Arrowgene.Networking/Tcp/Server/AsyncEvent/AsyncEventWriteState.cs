using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventWriteState
    {
        private const int MaxQueuedBytes = 16 * 1024 * 1024;

        private byte[] _data;
        private int _transferredCount;
        private int _outstandingCount;
        private int _queuedBytes;
        private readonly object _sendLock;
        private readonly Queue<byte[]> _sendQueue;
        private bool _sendInProgress;

        public AsyncEventWriteState()
        {
            _sendLock = new object();
            _sendQueue = new Queue<byte[]>();
            _sendInProgress = false;
        }

        public void Reset()
        {
            lock (_sendLock)
            {
                _sendQueue.Clear();
                _data = null;
                _outstandingCount = 0;
                _transferredCount = 0;
                _queuedBytes = 0;
                _sendInProgress = false;
            }
        }

        internal bool EnqueueSend(byte[] data, out bool queueOverflow)
        {
            lock (_sendLock)
            {
                queueOverflow = false;
                if (data.Length == 0)
                {
                    return false;
                }

                if (data.Length > MaxQueuedBytes - _queuedBytes)
                {
                    queueOverflow = true;
                    return false;
                }

                _sendQueue.Enqueue(data);
                _queuedBytes += data.Length;
                if (_sendInProgress)
                {
                    return false;
                }

                _sendInProgress = true;
                if (_outstandingCount == 0 && _sendQueue.Count > 0)
                {
                    _data = _sendQueue.Dequeue();
                    _outstandingCount = _data.Length;
                    _transferredCount = 0;
                }

                return true;
            }
        }

        internal bool TryGetSendChunk(int maxChunkSize, out byte[] data, out int offset, out int count)
        {
            lock (_sendLock)
            {
                if (_outstandingCount == 0)
                {
                    if (_sendQueue.Count == 0)
                    {
                        _sendInProgress = false;
                        data = null;
                        offset = 0;
                        count = 0;
                        return false;
                    }

                    _data = _sendQueue.Dequeue();
                    _outstandingCount = _data.Length;
                    _transferredCount = 0;
                }

                data = _data;
                offset = _transferredCount;
                count = _outstandingCount <= maxChunkSize ? _outstandingCount : maxChunkSize;
                return true;
            }
        }

        internal bool CompleteSend(int transferredCount)
        {
            lock (_sendLock)
            {
                _transferredCount += transferredCount;
                _outstandingCount -= transferredCount;
                _queuedBytes -= transferredCount;
                if (_queuedBytes < 0)
                {
                    _queuedBytes = 0;
                }

                if (_outstandingCount > 0)
                {
                    return true;
                }

                if (_sendQueue.Count > 0)
                {
                    _data = _sendQueue.Dequeue();
                    _outstandingCount = _data.Length;
                    _transferredCount = 0;
                    return true;
                }

                _data = null;
                _outstandingCount = 0;
                _transferredCount = 0;
                _sendInProgress = false;
                return false;
            }
        }

        internal void ClearQueuedSends()
        {
            lock (_sendLock)
            {
                _sendQueue.Clear();

                if (_outstandingCount > 0)
                {
                    // Keep the in-flight chunk state intact so completion callbacks remain consistent.
                    _queuedBytes = _outstandingCount;
                    _sendInProgress = true;
                    return;
                }

                _data = null;
                _queuedBytes = 0;
                _transferredCount = 0;
                _sendInProgress = false;
            }
        }
    }
}
