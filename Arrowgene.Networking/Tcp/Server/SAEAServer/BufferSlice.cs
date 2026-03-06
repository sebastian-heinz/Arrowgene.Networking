using System;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal readonly struct BufferSlice
{
    internal BufferSlice(byte[] array, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (offset < 0 || offset > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (length < 0 || offset + length > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Array = array;
        Offset = offset;
        Length = length;
    }

    internal byte[] Array { get; }

    internal int Offset { get; }

    internal int Length { get; }

    internal Span<byte> AsSpan()
    {
        return new Span<byte>(Array, Offset, Length);
    }
}
