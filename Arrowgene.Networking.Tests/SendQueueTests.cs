using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for send-queue queued-byte tracking.
/// </summary>
public sealed class SendQueueTests
{
    /// <summary>
    /// Verifies queued-byte totals track enqueues, completed sends, and reset.
    /// </summary>
    [Fact]
    public void GetQueuedBytes_TracksQueueLifecycle()
    {
        SendQueue queue = new SendQueue(1024);
        byte[] first = new byte[100];
        byte[] second = new byte[40];
        byte[] buffer = new byte[256];

        bool firstEnqueued = queue.Enqueue(first, out bool firstStartsSend, out bool firstOverflow);
        bool secondEnqueued = queue.Enqueue(second, out bool secondStartsSend, out bool secondOverflow);

        Assert.True(firstEnqueued);
        Assert.True(firstStartsSend);
        Assert.False(firstOverflow);
        Assert.True(secondEnqueued);
        Assert.False(secondStartsSend);
        Assert.False(secondOverflow);
        Assert.Equal(140, queue.GetQueuedBytes());

        bool copied = queue.CopyNextChunk(buffer, 0, 60, out int chunkSize);
        Assert.True(copied);
        Assert.Equal(60, chunkSize);
        Assert.Equal(140, queue.GetQueuedBytes());

        bool continueSending = queue.CompleteSend(chunkSize);
        Assert.True(continueSending);
        Assert.Equal(80, queue.GetQueuedBytes());

        copied = queue.CopyNextChunk(buffer, 0, 100, out chunkSize);
        Assert.True(copied);
        Assert.Equal(40, chunkSize);

        continueSending = queue.CompleteSend(chunkSize);
        Assert.True(continueSending);
        Assert.Equal(40, queue.GetQueuedBytes());

        copied = queue.CopyNextChunk(buffer, 0, 100, out chunkSize);
        Assert.True(copied);
        Assert.Equal(40, chunkSize);

        continueSending = queue.CompleteSend(chunkSize);
        Assert.False(continueSending);
        Assert.Equal(0, queue.GetQueuedBytes());

        queue.Enqueue(first, out _, out _);
        Assert.Equal(100, queue.GetQueuedBytes());

        queue.Reset();
        Assert.Equal(0, queue.GetQueuedBytes());
    }
}
