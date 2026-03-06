using System;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

internal sealed class AsyncEventServerBufferSlab
{
    internal AsyncEventServerBufferSlab(int segmentSize, int segmentCount)
    {
        if (segmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentSize));
        }

        if (segmentCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount));
        }

        int totalSize = checked(segmentSize * segmentCount);
        RawBuffer = GC.AllocateArray<byte>(totalSize, pinned: true);
        SegmentSize = segmentSize;
        SegmentCount = segmentCount;
    }

    internal byte[] RawBuffer { get; }

    internal int SegmentSize { get; }

    internal int SegmentCount { get; }

    internal int GetOffset(int index)
    {
        if ((uint)index >= (uint)SegmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return checked(index * SegmentSize);
    }
}
