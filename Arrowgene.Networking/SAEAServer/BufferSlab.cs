using System;
using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal sealed class BufferSlab
{
    private readonly byte[] _buffer;
    private readonly int _bufferSize;
    private readonly int _perClientStride;
    private readonly SendStorageMode _sendStorageMode;

    internal BufferSlab(
        ushort maxConnections,
        int bufferSize,
        int maxQueuedSendBytes,
        SendStorageMode sendStorageMode
    )
    {
        if (maxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        if (maxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedSendBytes));
        }

        if (!Enum.IsDefined(typeof(SendStorageMode), sendStorageMode))
        {
            throw new ArgumentOutOfRangeException(nameof(sendStorageMode));
        }

        _bufferSize = bufferSize;
        _sendStorageMode = sendStorageMode;
        _perClientStride = sendStorageMode == SendStorageMode.HardCapped
            ? checked(bufferSize + maxQueuedSendBytes)
            : bufferSize;
        _buffer = GC.AllocateArray<byte>(checked(maxConnections * _perClientStride), pinned: true);
    }

    internal SocketAsyncEventArgs CreateReceiveEventArgs(ushort clientId, EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        if (completedHandler is null)
        {
            throw new ArgumentNullException(nameof(completedHandler));
        }

        SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();
        eventArgs.Completed += completedHandler;
        eventArgs.SetBuffer(_buffer, checked(clientId * _perClientStride), _bufferSize);
        return eventArgs;
    }

    internal SocketAsyncEventArgs CreateSendEventArgs(ushort clientId, EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        if (completedHandler is null)
        {
            throw new ArgumentNullException(nameof(completedHandler));
        }

        SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();
        eventArgs.Completed += completedHandler;
        return eventArgs;
    }

    internal ISendQueue CreateSendQueue(ushort clientId, int maxQueuedSendBytes)
    {
        if (maxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedSendBytes));
        }

        return _sendStorageMode switch
        {
            SendStorageMode.Shared => new SharedSendQueue(maxQueuedSendBytes),
            SendStorageMode.HardCapped => new ArenaBackedSendQueue(
                _buffer,
                checked((clientId * _perClientStride) + _bufferSize),
                maxQueuedSendBytes
            ),
            _ => throw new InvalidOperationException("Unsupported send storage mode.")
        };
    }
}
