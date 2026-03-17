using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for the metrics collector snapshot metadata.
/// </summary>
public sealed class TcpServerMetricsCollectorTests
{
    /// <summary>
    /// Verifies snapshots expose a stable server start time and a monotonically increasing sequence.
    /// </summary>
    [Fact]
    public void CaptureSnapshot_TracksStableStartTimeAndIncreasingSequence()
    {
        TcpServerMetricsCollector collector = CreateCollector(
            out _,
            out ClientRegistry clientRegistry,
            out AcceptPool acceptPool
        );

        try
        {
            collector.Start("MetricsCollectorTests");
            collector.Stop();

            TcpServerMetricsSnapshot firstSnapshot = collector.GetSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot secondSnapshot = collector.GetSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot thirdSnapshot = collector.GetSnapshot();

            Assert.Equal(1, firstSnapshot.SnapshotSequenceNumber);
            Assert.Equal(2, secondSnapshot.SnapshotSequenceNumber);
            Assert.Equal(3, thirdSnapshot.SnapshotSequenceNumber);

            Assert.Equal(firstSnapshot.ServerStartedAtUtc, secondSnapshot.ServerStartedAtUtc);
            Assert.Equal(firstSnapshot.ServerStartedAtUtc, thirdSnapshot.ServerStartedAtUtc);

            Assert.True(firstSnapshot.TimestampUtc >= firstSnapshot.ServerStartedAtUtc);
            Assert.True(secondSnapshot.TimestampUtc >= secondSnapshot.ServerStartedAtUtc);
            Assert.True(thirdSnapshot.TimestampUtc >= thirdSnapshot.ServerStartedAtUtc);
            Assert.Equal(0.0d, firstSnapshot.ReceiveOpsPerSecond);
            Assert.Equal(0.0d, firstSnapshot.SendOpsPerSecond);
            Assert.Equal(0.0d, firstSnapshot.AcceptsPerSecond);
            Assert.Equal(0.0d, secondSnapshot.ReceiveOpsPerSecond);
            Assert.Equal(0.0d, secondSnapshot.SendOpsPerSecond);
            Assert.Equal(0.0d, secondSnapshot.AcceptsPerSecond);
            Assert.Equal(0.0d, thirdSnapshot.ReceiveOpsPerSecond);
            Assert.Equal(0.0d, thirdSnapshot.SendOpsPerSecond);
            Assert.Equal(0.0d, thirdSnapshot.AcceptsPerSecond);
        }
        finally
        {
            collector.Dispose();
            acceptPool.Dispose();
            clientRegistry.Dispose();
        }
    }

    /// <summary>
    /// Verifies receive, send, and accept operation rates are derived from counter deltas across a snapshot interval.
    /// </summary>
    [Fact]
    public void CaptureSnapshot_DerivesOperationRatesFromCounterDeltas()
    {
        TcpServerMetricsCollector collector = CreateCollector(
            out TcpServerMetricsState metricsState,
            out ClientRegistry clientRegistry,
            out AcceptPool acceptPool
        );
        metricsState.EnableCapture();

        try
        {
            collector.Start("MetricsCollectorTests");
            collector.Stop();

            TcpServerMetricsSnapshot baselineSnapshot = collector.GetSnapshot();

            metricsState.IncrementAcceptedConnections();
            metricsState.IncrementAcceptedConnections();
            metricsState.RecordReceive(4);
            metricsState.RecordReceive(8);
            metricsState.RecordReceive(12);
            metricsState.RecordSend(5);
            metricsState.RecordSend(10);
            metricsState.RecordSend(15);
            metricsState.RecordSend(20);

            Thread.Sleep(50);
            collector.CaptureSnapshot();

            TcpServerMetricsSnapshot measuredSnapshot = collector.GetSnapshot();
            double elapsedSeconds = (measuredSnapshot.TimestampUtc - baselineSnapshot.TimestampUtc).TotalSeconds;

            Assert.True(elapsedSeconds > 0.0d);
            Assert.Equal(3.0d / elapsedSeconds, measuredSnapshot.ReceiveOpsPerSecond, 9);
            Assert.Equal(4.0d / elapsedSeconds, measuredSnapshot.SendOpsPerSecond, 9);
            Assert.Equal(2.0d / elapsedSeconds, measuredSnapshot.AcceptsPerSecond, 9);
        }
        finally
        {
            collector.Dispose();
            acceptPool.Dispose();
            clientRegistry.Dispose();
        }
    }

    private static TcpServerMetricsCollector CreateCollector(
        out TcpServerMetricsState metricsState,
        out ClientRegistry clientRegistry,
        out AcceptPool acceptPool)
    {
        metricsState = new TcpServerMetricsState();
        clientRegistry = new ClientRegistry(1, 1, CreateClient);
        acceptPool = new AcceptPool(1, IgnoreAcceptCompletion);
        return new TcpServerMetricsCollector(metricsState, clientRegistry, acceptPool, null, 1);
    }

    private static Client CreateClient(ushort clientId)
    {
        TcpServer tcpServer = (TcpServer)RuntimeHelpers.GetUninitializedObject(typeof(TcpServer));
        SocketAsyncEventArgs receiveEventArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();
        return new Client(tcpServer, clientId, receiveEventArgs, sendEventArgs, 1024);
    }

    private static void IgnoreAcceptCompletion(object? sender, SocketAsyncEventArgs eventArgs)
    {
    }
}
