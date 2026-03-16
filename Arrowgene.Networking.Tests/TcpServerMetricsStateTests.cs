using System.Net.Sockets;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for the mutable server metrics state.
/// </summary>
public sealed class TcpServerMetricsStateTests
{
    /// <summary>
    /// Verifies transfer sizes are bucketed and counted on both receive and send paths.
    /// </summary>
    [Fact]
    public void RecordReceiveAndSend_TracksTransferSizeBuckets()
    {
        TcpServerMetricsState state = new TcpServerMetricsState();
        state.EnableCapture();

        int[] receiveSamples = new int[]
        {
            1,
            64,
            65,
            256,
            257,
            1024,
            1025,
            4096,
            4097,
            8192,
            8193,
            16384,
            16385,
            20000
        };
        int[] sendSamples = new int[]
        {
            64,
            256,
            1024,
            4096,
            8192,
            16384,
            16385
        };

        state.RecordReceive(0);
        state.RecordSend(-1);

        for (int index = 0; index < receiveSamples.Length; index++)
        {
            state.RecordReceive(receiveSamples[index]);
        }

        for (int index = 0; index < sendSamples.Length; index++)
        {
            state.RecordSend(sendSamples[index]);
        }

        long[] receiveBuckets = new long[state.ReceiveSizeBucketCount];
        long[] sendBuckets = new long[state.SendSizeBucketCount];
        state.CopyReceiveSizeBuckets(receiveBuckets);
        state.CopySendSizeBuckets(sendBuckets);

        Assert.Equal(receiveSamples.Length, state.GetReceiveOperations());
        Assert.Equal(Sum(receiveSamples), state.GetBytesReceived());
        Assert.Equal(new long[] { 2, 2, 2, 2, 2, 2, 2 }, receiveBuckets);

        Assert.Equal(sendSamples.Length, state.GetSendOperations());
        Assert.Equal(Sum(sendSamples), state.GetBytesSent());
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1 }, sendBuckets);
    }

    /// <summary>
    /// Verifies socket error totals and per-code counters are tracked separately.
    /// </summary>
    [Fact]
    public void RecordSocketErrors_TracksPerCodeCountsAndGenericTotals()
    {
        TcpServerMetricsState state = new TcpServerMetricsState();
        state.EnableCapture();

        state.RecordSocketAcceptError(SocketError.ConnectionReset);
        state.RecordSocketAcceptError(SocketError.ConnectionReset);
        state.IncrementSocketAcceptErrors();
        state.RecordSocketReceiveError(SocketError.OperationAborted);
        state.RecordSocketSendError(SocketError.ConnectionAborted);

        long[] socketErrorsByCode = new long[state.SocketErrorCodeCount];
        state.CopySocketErrorsByCode(socketErrorsByCode);

        Assert.Equal(3, state.GetSocketAcceptErrors());
        Assert.Equal(1, state.GetSocketReceiveErrors());
        Assert.Equal(1, state.GetSocketSendErrors());
        Assert.Equal(2, GetSocketErrorCount(state, socketErrorsByCode, SocketError.ConnectionReset));
        Assert.Equal(1, GetSocketErrorCount(state, socketErrorsByCode, SocketError.OperationAborted));
        Assert.Equal(1, GetSocketErrorCount(state, socketErrorsByCode, SocketError.ConnectionAborted));
        Assert.Equal(0, GetSocketErrorCount(state, socketErrorsByCode, SocketError.Success));
    }

    private static long Sum(int[] values)
    {
        long total = 0;

        for (int index = 0; index < values.Length; index++)
        {
            total += values[index];
        }

        return total;
    }

    private static long GetSocketErrorCount(
        TcpServerMetricsState state,
        long[] socketErrorsByCode,
        SocketError socketError)
    {
        int index = ((int)socketError) - state.SocketErrorCodeMinimum;
        if ((uint)index >= (uint)socketErrorsByCode.Length)
        {
            return 0;
        }

        return socketErrorsByCode[index];
    }
}
