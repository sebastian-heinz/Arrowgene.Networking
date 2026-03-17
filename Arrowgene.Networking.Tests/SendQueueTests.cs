using System;
using System.Net.Sockets;
using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for send-queue queued-byte tracking.
/// </summary>
public sealed class SendQueueTests
{
    /// <summary>
    /// Verifies queued-byte totals track enqueues, completed sends, and reset for shared pooled storage.
    /// </summary>
    [Fact]
    public void SharedSendQueue_TracksQueueLifecycle()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        byte[] first = CreatePayload(100, 1);
        byte[] second = CreatePayload(40, 101);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        EnqueueResult firstEnqueued = queue.Enqueue(first, 0, first.Length);
        EnqueueResult secondEnqueued = queue.Enqueue(second, 0, second.Length);

        Assert.Equal(EnqueueResult.SendNow, firstEnqueued);
        Assert.Equal(EnqueueResult.Queued, secondEnqueued);
        Assert.Equal(140, queue.QueuedBytes);

        bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.True(prepared);
        Assert.Equal(100, chunkSize);
        Assert.Equal(first, CopyBoundBytes(sendEventArgs));
        Assert.Equal(140, queue.QueuedBytes);

        bool continueSending = queue.CompleteSend(60);
        Assert.True(continueSending);
        Assert.Equal(80, queue.QueuedBytes);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(40, chunkSize);
        Assert.Equal(CopyBytes(first, 60, 40), CopyBoundBytes(sendEventArgs));

        continueSending = queue.CompleteSend(chunkSize);
        Assert.True(continueSending);
        Assert.Equal(40, queue.QueuedBytes);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(40, chunkSize);
        Assert.Equal(second, CopyBoundBytes(sendEventArgs));

        continueSending = queue.CompleteSend(chunkSize);
        Assert.False(continueSending);
        Assert.Equal(0, queue.QueuedBytes);

        queue.Enqueue(first, 0, first.Length);
        Assert.Equal(100, queue.QueuedBytes);

        queue.Reset();
        Assert.Equal(0, queue.QueuedBytes);
    }

    /// <summary>
    /// Verifies pinned arena storage wraps correctly and exposes the next contiguous segment without copying.
    /// </summary>
    [Fact]
    public void ArenaBackedSendQueue_WrapsAndTracksQueueLifecycle()
    {
        byte[] arena = GC.AllocateArray<byte>(64, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 8, 16);
        byte[] first = CreatePayload(12, 1);
        byte[] second = CreatePayload(10, 101);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        EnqueueResult firstEnqueued = queue.Enqueue(first, 0, first.Length);
        Assert.Equal(EnqueueResult.SendNow, firstEnqueued);
        Assert.Equal(12, queue.QueuedBytes);

        bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.True(prepared);
        Assert.Equal(12, chunkSize);
        Assert.Equal(first, CopyBoundBytes(sendEventArgs));

        bool continueSending = queue.CompleteSend(10);
        Assert.True(continueSending);
        Assert.Equal(2, queue.QueuedBytes);

        EnqueueResult secondEnqueued = queue.Enqueue(second, 0, second.Length);
        Assert.Equal(EnqueueResult.Queued, secondEnqueued);
        Assert.Equal(12, queue.QueuedBytes);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(6, chunkSize);
        Assert.Equal(Concat(CopyBytes(first, 10, 2), CopyBytes(second, 0, 4)), CopyBoundBytes(sendEventArgs));

        continueSending = queue.CompleteSend(chunkSize);
        Assert.True(continueSending);
        Assert.Equal(6, queue.QueuedBytes);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(6, chunkSize);
        Assert.Equal(CopyBytes(second, 4, 6), CopyBoundBytes(sendEventArgs));

        continueSending = queue.CompleteSend(chunkSize);
        Assert.False(continueSending);
        Assert.Equal(0, queue.QueuedBytes);

        queue.Reset();
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void SharedSendQueue_OverflowRejects()
    {
        SharedSendQueue queue = new SharedSendQueue(50);
        byte[] data = CreatePayload(30, 1);

        EnqueueResult first = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, first);
        Assert.Equal(30, queue.QueuedBytes);

        EnqueueResult second = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.Overflow, second);
        Assert.Equal(30, queue.QueuedBytes);
    }

    [Fact]
    public void ArenaBackedSendQueue_OverflowRejects()
    {
        byte[] arena = GC.AllocateArray<byte>(64, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 50);
        byte[] data = CreatePayload(30, 1);

        EnqueueResult first = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, first);
        Assert.Equal(30, queue.QueuedBytes);

        EnqueueResult second = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.Overflow, second);
        Assert.Equal(30, queue.QueuedBytes);
    }

    [Fact]
    public void SharedSendQueue_EnqueueWithOffsetAndLength()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        byte[] data = CreatePayload(100, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        EnqueueResult result = queue.Enqueue(data, 10, 20);
        Assert.Equal(EnqueueResult.SendNow, result);
        Assert.Equal(20, queue.QueuedBytes);

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.Equal(20, chunkSize);
        Assert.Equal(CopyBytes(data, 10, 20), CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void ArenaBackedSendQueue_EnqueueWithOffsetAndLength()
    {
        byte[] arena = GC.AllocateArray<byte>(128, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 64);
        byte[] data = CreatePayload(100, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        EnqueueResult result = queue.Enqueue(data, 10, 20);
        Assert.Equal(EnqueueResult.SendNow, result);
        Assert.Equal(20, queue.QueuedBytes);

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.Equal(20, chunkSize);
        Assert.Equal(CopyBytes(data, 10, 20), CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void ArenaBackedSendQueue_ManyOneBytesSendsToCapacity()
    {
        int capacity = 64;
        byte[] arena = GC.AllocateArray<byte>(capacity, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, capacity);
        byte[] one = new byte[] { 0xAB };

        EnqueueResult first = queue.Enqueue(one, 0, 1);
        Assert.Equal(EnqueueResult.SendNow, first);

        for (int i = 1; i < capacity; i++)
        {
            EnqueueResult r = queue.Enqueue(one, 0, 1);
            Assert.Equal(EnqueueResult.Queued, r);
        }

        Assert.Equal(capacity, queue.QueuedBytes);

        EnqueueResult overflow = queue.Enqueue(one, 0, 1);
        Assert.Equal(EnqueueResult.Overflow, overflow);
        Assert.Equal(capacity, queue.QueuedBytes);
    }

    [Fact]
    public void SharedSendQueue_SendNowAfterDrain()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        byte[] data = CreatePayload(10, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(data, 0, data.Length));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(data, 0, data.Length));

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        queue.CompleteSend(chunkSize);
        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        bool more = queue.CompleteSend(chunkSize);
        Assert.False(more);

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(data, 0, data.Length));
    }

    [Fact]
    public void ArenaBackedSendQueue_SendNowAfterDrain()
    {
        byte[] arena = GC.AllocateArray<byte>(64, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 64);
        byte[] data = CreatePayload(10, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(data, 0, data.Length));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(data, 0, data.Length));

        // circular buffer sees 20 contiguous bytes — drain in a loop
        while (queue.QueuedBytes > 0)
        {
            queue.TryGetNextChunk(sendEventArgs, out int chunk);
            queue.CompleteSend(chunk);
        }

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(data, 0, data.Length));
    }

    [Fact]
    public void SharedSendQueue_OverflowThenFreesThenAccepts()
    {
        SharedSendQueue queue = new SharedSendQueue(20);
        byte[] data = CreatePayload(15, 1);
        byte[] small = CreatePayload(10, 50);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.Overflow, queue.Enqueue(small, 0, small.Length));

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        queue.CompleteSend(chunkSize);

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(small, 0, small.Length));
        Assert.Equal(10, queue.QueuedBytes);
    }

    [Fact]
    public void ArenaBackedSendQueue_OverflowThenFreesThenAccepts()
    {
        byte[] arena = GC.AllocateArray<byte>(20, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 20);
        byte[] data = CreatePayload(15, 1);
        byte[] small = CreatePayload(10, 50);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.Overflow, queue.Enqueue(small, 0, small.Length));

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        queue.CompleteSend(chunkSize);

        Assert.Equal(EnqueueResult.SendNow, queue.Enqueue(small, 0, small.Length));
        Assert.Equal(10, queue.QueuedBytes);
    }

    [Fact]
    public void ArenaBackedSendQueue_EnqueueDuringInFlightWritesToFreeRegion()
    {
        byte[] arena = GC.AllocateArray<byte>(32, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 32);
        byte[] first = CreatePayload(20, 1);
        byte[] second = CreatePayload(8, 101);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(first, 0, first.Length);

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.Equal(20, chunkSize);
        byte[] snapshot = CopyBoundBytes(sendEventArgs);

        queue.Enqueue(second, 0, second.Length);

        Assert.Equal(snapshot, CopyBoundBytes(sendEventArgs));

        queue.CompleteSend(chunkSize);

        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(8, chunkSize);
        Assert.Equal(second, CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void SharedSendQueue_CallerMutationDoesNotAffectQueued()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        byte[] data = CreatePayload(10, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        byte[] expected = (byte[])data.Clone();
        data[0] = 0xFF;
        data[5] = 0xFF;

        queue.TryGetNextChunk(sendEventArgs, out _);
        Assert.Equal(expected, CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void ArenaBackedSendQueue_CallerMutationDoesNotAffectQueued()
    {
        byte[] arena = GC.AllocateArray<byte>(64, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 64);
        byte[] data = CreatePayload(10, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        byte[] expected = (byte[])data.Clone();
        data[0] = 0xFF;
        data[5] = 0xFF;

        queue.TryGetNextChunk(sendEventArgs, out _);
        Assert.Equal(expected, CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void ArenaBackedSendQueue_WrapProducesCorrectContiguousChunks()
    {
        byte[] arena = GC.AllocateArray<byte>(16, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 16);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        byte[] fill = CreatePayload(12, 1);
        queue.Enqueue(fill, 0, fill.Length);

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        queue.CompleteSend(chunkSize);

        byte[] wrap = CreatePayload(10, 50);
        queue.Enqueue(wrap, 0, wrap.Length);

        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(4, chunkSize);
        Assert.Equal(CopyBytes(wrap, 0, 4), CopyBoundBytes(sendEventArgs));

        queue.CompleteSend(chunkSize);

        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(6, chunkSize);
        Assert.Equal(CopyBytes(wrap, 4, 6), CopyBoundBytes(sendEventArgs));

        bool more = queue.CompleteSend(chunkSize);
        Assert.False(more);
    }

    [Fact]
    public void ArenaBackedSendQueue_ResetRestoresCursors()
    {
        byte[] arena = GC.AllocateArray<byte>(32, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 32);
        byte[] data = CreatePayload(20, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        queue.CompleteSend(10);

        queue.Reset();
        Assert.Equal(0, queue.QueuedBytes);

        byte[] after = CreatePayload(32, 50);
        queue.Enqueue(after, 0, after.Length);
        Assert.Equal(32, queue.QueuedBytes);

        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(32, chunkSize);
        Assert.Equal(after, CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void ArenaBackedSendQueue_WrapUtilizesAllSpace()
    {
        byte[] arena = GC.AllocateArray<byte>(20, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 20);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        // enqueue and fully drain an 18-byte message — _writePos = 18, _readPos = 18
        byte[] a = CreatePayload(18, 1);
        queue.Enqueue(a, 0, a.Length);
        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.Equal(18, chunkSize);
        queue.CompleteSend(chunkSize);
        Assert.Equal(0, queue.QueuedBytes);

        // enqueue a 4-byte message — must wrap: 2 bytes at positions 18–19, 2 bytes at 0–1
        byte[] b = CreatePayload(4, 50);
        EnqueueResult result = queue.Enqueue(b, 0, b.Length);
        Assert.Equal(EnqueueResult.SendNow, result);
        Assert.Equal(4, queue.QueuedBytes);

        // first chunk: contiguous run from _readPos (18) to buffer end (20) = 2 bytes
        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(2, chunkSize);
        Assert.Equal(CopyBytes(b, 0, 2), CopyBoundBytes(sendEventArgs));

        bool more = queue.CompleteSend(chunkSize);
        Assert.True(more);
        Assert.Equal(2, queue.QueuedBytes);

        // second chunk: remaining 2 bytes from position 0
        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(2, chunkSize);
        Assert.Equal(CopyBytes(b, 2, 2), CopyBoundBytes(sendEventArgs));

        more = queue.CompleteSend(chunkSize);
        Assert.False(more);
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void SharedSendQueue_ResetReturnsAllBuffers()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        byte[] data = CreatePayload(50, 1);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(data, 0, data.Length);
        queue.Enqueue(data, 0, data.Length);
        queue.TryGetNextChunk(sendEventArgs, out _);

        queue.Reset();
        Assert.Equal(0, queue.QueuedBytes);

        EnqueueResult result = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, result);
    }

    [Fact]
    public void ArenaBackedSendQueue_InterleavedEnqueueAndSendCycles()
    {
        byte[] arena = GC.AllocateArray<byte>(16, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 16);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        for (int cycle = 0; cycle < 10; cycle++)
        {
            byte startByte = unchecked((byte)(cycle * 7));
            byte[] payload = CreatePayload(12, startByte);

            EnqueueResult result = queue.Enqueue(payload, 0, payload.Length);
            Assert.Equal(EnqueueResult.SendNow, result);
            Assert.Equal(12, queue.QueuedBytes);

            int totalSent = 0;
            while (totalSent < 12)
            {
                bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
                Assert.True(prepared);
                Assert.True(chunkSize > 0);
                totalSent += chunkSize;
                queue.CompleteSend(chunkSize);
            }

            Assert.Equal(12, totalSent);
            Assert.Equal(0, queue.QueuedBytes);
        }
    }

    [Fact]
    public void ArenaBackedSendQueue_ExactCapacityFillThenOverflows()
    {
        byte[] arena = GC.AllocateArray<byte>(20, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 20);
        byte[] data = CreatePayload(20, 1);

        EnqueueResult result = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, result);
        Assert.Equal(20, queue.QueuedBytes);

        byte[] one = new byte[] { 0xFF };
        Assert.Equal(EnqueueResult.Overflow, queue.Enqueue(one, 0, 1));
        Assert.Equal(20, queue.QueuedBytes);
    }

    [Fact]
    public void SharedSendQueue_ExactCapacityFillThenOverflows()
    {
        SharedSendQueue queue = new SharedSendQueue(20);
        byte[] data = CreatePayload(20, 1);

        EnqueueResult result = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, result);
        Assert.Equal(20, queue.QueuedBytes);

        byte[] one = new byte[] { 0xFF };
        Assert.Equal(EnqueueResult.Overflow, queue.Enqueue(one, 0, 1));
        Assert.Equal(20, queue.QueuedBytes);
    }

    [Fact]
    public void ArenaBackedSendQueue_MessageLandsExactlyAtBoundary()
    {
        byte[] arena = GC.AllocateArray<byte>(20, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 20);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        // fill exactly to the boundary — _writePos wraps to 0
        byte[] a = CreatePayload(20, 1);
        queue.Enqueue(a, 0, a.Length);

        queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.Equal(20, chunkSize);
        Assert.Equal(a, CopyBoundBytes(sendEventArgs));
        queue.CompleteSend(chunkSize);

        // next enqueue starts cleanly from position 0
        byte[] b = CreatePayload(7, 50);
        queue.Enqueue(b, 0, b.Length);

        queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.Equal(7, chunkSize);
        Assert.Equal(b, CopyBoundBytes(sendEventArgs));

        queue.CompleteSend(chunkSize);
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void ArenaBackedSendQueue_OneBytePartialSends()
    {
        byte[] arena = GC.AllocateArray<byte>(32, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 32);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        byte[] data = CreatePayload(10, 1);
        queue.Enqueue(data, 0, data.Length);

        byte[] received = new byte[10];
        int receivedOffset = 0;

        while (queue.QueuedBytes > 0)
        {
            bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
            Assert.True(prepared);

            // simulate kernel sending 1 byte at a time
            byte[] bound = CopyBoundBytes(sendEventArgs);
            received[receivedOffset] = bound[0];
            receivedOffset++;

            queue.CompleteSend(1);
        }

        Assert.Equal(10, receivedOffset);
        Assert.Equal(data, received);
    }

    [Fact]
    public void SharedSendQueue_OneBytePartialSends()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        byte[] data = CreatePayload(10, 1);
        queue.Enqueue(data, 0, data.Length);

        byte[] received = new byte[10];
        int receivedOffset = 0;

        while (queue.QueuedBytes > 0)
        {
            bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
            Assert.True(prepared);

            byte[] bound = CopyBoundBytes(sendEventArgs);
            received[receivedOffset] = bound[0];
            receivedOffset++;

            queue.CompleteSend(1);
        }

        Assert.Equal(10, receivedOffset);
        Assert.Equal(data, received);
    }

    [Fact]
    public void ArenaBackedSendQueue_CompleteSendExceedingQueuedThrows()
    {
        byte[] arena = GC.AllocateArray<byte>(32, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 32);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(CreatePayload(5, 1), 0, 5);
        queue.TryGetNextChunk(sendEventArgs, out _);

        Assert.Throws<InvalidOperationException>(() => queue.CompleteSend(6));
    }

    [Fact]
    public void SharedSendQueue_CompleteSendExceedingMessageThrows()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(CreatePayload(5, 1), 0, 5);
        queue.TryGetNextChunk(sendEventArgs, out _);

        Assert.Throws<InvalidOperationException>(() => queue.CompleteSend(6));
    }

    [Fact]
    public void ArenaBackedSendQueue_TryGetNextChunkOnEmptyAfterReset()
    {
        byte[] arena = GC.AllocateArray<byte>(32, pinned: true);
        ArenaBackedSendQueue queue = new ArenaBackedSendQueue(arena, 0, 32);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(CreatePayload(10, 1), 0, 10);
        queue.Reset();

        bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.False(prepared);
        Assert.Equal(0, chunkSize);

        // state is clean — next enqueue works
        byte[] data = CreatePayload(15, 50);
        EnqueueResult result = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, result);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(15, chunkSize);
        Assert.Equal(data, CopyBoundBytes(sendEventArgs));
    }

    [Fact]
    public void SharedSendQueue_TryGetNextChunkOnEmptyAfterReset()
    {
        SharedSendQueue queue = new SharedSendQueue(1024);
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();

        queue.Enqueue(CreatePayload(10, 1), 0, 10);
        queue.Reset();

        bool prepared = queue.TryGetNextChunk(sendEventArgs, out int chunkSize);
        Assert.False(prepared);
        Assert.Equal(0, chunkSize);

        byte[] data = CreatePayload(15, 50);
        EnqueueResult result = queue.Enqueue(data, 0, data.Length);
        Assert.Equal(EnqueueResult.SendNow, result);

        prepared = queue.TryGetNextChunk(sendEventArgs, out chunkSize);
        Assert.True(prepared);
        Assert.Equal(15, chunkSize);
        Assert.Equal(data, CopyBoundBytes(sendEventArgs));
    }

    private static byte[] CopyBoundBytes(SocketAsyncEventArgs sendEventArgs)
    {
        byte[]? nullableSource = sendEventArgs.Buffer;
        if (nullableSource is null)
        {
            throw new InvalidOperationException("The send event args has no bound buffer.");
        }

        byte[] source = nullableSource;
        int offset = sendEventArgs.Offset;
        int count = sendEventArgs.Count;
        byte[] copy = new byte[count];
        Buffer.BlockCopy(source, offset, copy, 0, count);
        return copy;
    }

    private static byte[] CopyBytes(byte[] source, int offset, int count)
    {
        byte[] copy = new byte[count];
        Buffer.BlockCopy(source, offset, copy, 0, count);
        return copy;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    private static byte[] CreatePayload(int length, byte start)
    {
        byte[] payload = new byte[length];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)(start + index));
        }

        return payload;
    }
}
