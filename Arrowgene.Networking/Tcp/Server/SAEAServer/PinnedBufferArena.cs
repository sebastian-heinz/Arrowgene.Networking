using System;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class PinnedBufferArena
{
    private readonly byte[] _buffer;
    private readonly int _sliceLength;

    internal PinnedBufferArena(int sliceCount, int sliceLength)
    {
        if (sliceCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceCount));
        }

        if (sliceLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceLength));
        }

        checked
        {
            _buffer = GC.AllocateArray<byte>(sliceCount * sliceLength, true);
        }

        _sliceLength = sliceLength;
    }

    internal BufferSlice GetSlice(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int offset = checked(index * _sliceLength);
        if (offset + _sliceLength > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new BufferSlice(_buffer, offset, _sliceLength);
    }
}
