using System;
using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal sealed class ArenaBackedSendQueue : ISendQueue
{
    private readonly object _sync;
    private readonly byte[] _buffer;
    private readonly int _baseOffset;
    private readonly int _capacity;
    private int _readPos;
    private int _writePos;
    private int _queuedBytes;
    private bool _sendInProgress;

    internal ArenaBackedSendQueue(byte[] buffer, int baseOffset, int capacity)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (baseOffset < 0 || capacity <= 0 || buffer.Length - baseOffset < capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _sync = new object();
        _buffer = buffer;
        _baseOffset = baseOffset;
        _capacity = capacity;
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
            if (length > _capacity - _queuedBytes)
            {
                return EnqueueResult.Overflow;
            }

            int firstPart = Math.Min(length, _capacity - _writePos);
            Buffer.BlockCopy(data, offset, _buffer, _baseOffset + _writePos, firstPart);
            if (firstPart < length)
            {
                Buffer.BlockCopy(data, offset + firstPart, _buffer, _baseOffset, length - firstPart);
            }

            _writePos = (_writePos + length) % _capacity;
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
            if (_queuedBytes == 0)
            {
                _sendInProgress = false;
                chunkSize = 0;
                return false;
            }

            chunkSize = Math.Min(_queuedBytes, _capacity - _readPos);
            sendEventArgs.SetBuffer(_buffer, _baseOffset + _readPos, chunkSize);
            return true;
        }
    }

    public bool CompleteSend(int bytesTransferred)
    {
        lock (_sync)
        {
            if (bytesTransferred <= 0 || bytesTransferred > _queuedBytes)
            {
                throw new InvalidOperationException("Completed send does not match the queued payload state.");
            }

            _readPos = (_readPos + bytesTransferred) % _capacity;
            _queuedBytes -= bytesTransferred;

            if (_queuedBytes > 0)
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
            _readPos = 0;
            _writePos = 0;
            _queuedBytes = 0;
            _sendInProgress = false;
        }
    }
}
