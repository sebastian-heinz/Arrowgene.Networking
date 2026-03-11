using System;
using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal sealed class BufferSlab
{
    private readonly byte[] _buffer;
    private readonly int _bufferSize;

    internal BufferSlab(ushort maxConnections, int bufferSize)
    {
        if (maxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        _bufferSize = bufferSize;
        _buffer = GC.AllocateArray<byte>(checked(maxConnections * bufferSize * 2), pinned: true);
    }

    internal SocketAsyncEventArgs CreateReceiveEventArgs(ushort clientId, EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        if (completedHandler is null)
        {
            throw new ArgumentNullException(nameof(completedHandler));
        }

        SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();
        eventArgs.Completed += completedHandler;
        eventArgs.SetBuffer(_buffer, checked(clientId * 2 * _bufferSize), _bufferSize);
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
        eventArgs.SetBuffer(_buffer, checked(((clientId * 2) + 1) * _bufferSize), _bufferSize);
        return eventArgs;
    }
}
