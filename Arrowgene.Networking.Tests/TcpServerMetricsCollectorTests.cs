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
            out TcpServerMetricsState metricsState,
            out ClientRegistry clientRegistry,
            out AcceptPool acceptPool
        );

        try
        {
            metricsState.EnableCapture();
            collector.Start("MetricsCollectorTests");
            collector.Stop();

            TcpServerMetricsSnapshot firstSnapshot = collector.GetPublishedMetricsSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot secondSnapshot = collector.GetPublishedMetricsSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot thirdSnapshot = collector.GetPublishedMetricsSnapshot();

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
            Assert.Equal(0, firstSnapshot.TotalSendQueuedBytes);
            Assert.Equal(0, secondSnapshot.TotalSendQueuedBytes);
            Assert.Equal(0, thirdSnapshot.TotalSendQueuedBytes);
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

            TcpServerMetricsSnapshot baselineSnapshot = collector.GetPublishedMetricsSnapshot();

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

            TcpServerMetricsSnapshot measuredSnapshot = collector.GetPublishedMetricsSnapshot();

            Assert.True(measuredSnapshot.ReceiveOpsPerSecond > 0.0d);
            Assert.True(measuredSnapshot.SendOpsPerSecond > 0.0d);
            Assert.True(measuredSnapshot.AcceptsPerSecond > 0.0d);
            Assert.Equal(
                measuredSnapshot.ReceiveOpsPerSecond / 3.0d,
                measuredSnapshot.SendOpsPerSecond / 4.0d,
                9
            );
            Assert.Equal(
                measuredSnapshot.ReceiveOpsPerSecond / 3.0d,
                measuredSnapshot.AcceptsPerSecond / 2.0d,
                9
            );
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
        TestServerMetricsCapture capture = new TestServerMetricsCapture(metricsState, clientRegistry, acceptPool);
        return new TcpServerMetricsCollector(capture);
    }

    private static Client CreateClient(ushort clientId)
    {
        TcpServer tcpServer = (TcpServer)RuntimeHelpers.GetUninitializedObject(typeof(TcpServer));
        SocketAsyncEventArgs receiveEventArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();
        SharedSendQueue sendQueue = new SharedSendQueue(1024);
        return new Client(tcpServer, clientId, receiveEventArgs, sendEventArgs, sendQueue);
    }

    private static void IgnoreAcceptCompletion(object? sender, SocketAsyncEventArgs eventArgs)
    {
    }

    private sealed class TestServerMetricsCapture : IMetricsCapture<TcpServerMetricsSnapshot>
    {
        private readonly TcpServerMetricsState _metricsState;
        private readonly ClientRegistry _clientRegistry;
        private readonly AcceptPool _acceptPool;

        internal TestServerMetricsCapture(
            TcpServerMetricsState metricsState,
            ClientRegistry clientRegistry,
            AcceptPool acceptPool)
        {
            _metricsState = metricsState;
            _clientRegistry = clientRegistry;
            _acceptPool = acceptPool;
        }

        public void EnableCapture()
        {
            _metricsState.EnableCapture();
        }

        public void DisableCapture()
        {
            _metricsState.DisableCapture();
        }

        public TcpServerMetricsSnapshot CreateSnapshot(double elapsedSeconds)
        {
            long activeConnections = _metricsState.GetActiveConnections();
            long peakActiveConnections = _metricsState.GetAndResetPeakActiveConnections(activeConnections);
            long totalSendQueuedBytes = _clientRegistry.GetTotalSendQueuedBytes();
            long acceptedConnections = _metricsState.GetAcceptedConnections();
            long bytesReceived = _metricsState.GetBytesReceived();
            long bytesSent = _metricsState.GetBytesSent();
            long receiveOperations = _metricsState.GetReceiveOperations();
            long sendOperations = _metricsState.GetSendOperations();
            long[] disconnectsByReason = new long[_metricsState.DisconnectReasonCount];
            long[] laneActiveConnections = new long[1];
            long[] connectionDurationBuckets = new long[_metricsState.ConnectionDurationBucketsCount];
            long[] receiveSizeBuckets = new long[_metricsState.ReceiveSizeBucketCount];
            long[] sendSizeBuckets = new long[_metricsState.SendSizeBucketCount];
            long[] socketErrorsByCode = new long[_metricsState.SocketErrorCodeCount];
            _metricsState.SetTotalSendQueuedBytes(totalSendQueuedBytes);
            _metricsState.CopyDisconnectsByReason(disconnectsByReason);
            _metricsState.CopyConnectionDurationBuckets(connectionDurationBuckets);
            _metricsState.CopyReceiveSizeBuckets(receiveSizeBuckets);
            _metricsState.CopySendSizeBuckets(sendSizeBuckets);
            _metricsState.CopySocketErrorsByCode(socketErrorsByCode);
            _clientRegistry.SnapshotLaneLoads(laneActiveConnections);

            _metricsState.SnapshotRates(
                elapsedSeconds,
                bytesReceived,
                bytesSent,
                receiveOperations,
                sendOperations,
                acceptedConnections,
                out double receiveBytesPerSecond,
                out double sendBytesPerSecond,
                out double receiveOpsPerSecond,
                out double sendOpsPerSecond,
                out double acceptsPerSecond
            );

            return new TcpServerMetricsSnapshot(
                DateTime.UtcNow,
                _metricsState.GetServerStartedAtUtc(),
                _metricsState.IncrementSnapshotSequenceNumber(),
                acceptedConnections,
                _metricsState.GetRejectedConnections(),
                activeConnections,
                peakActiveConnections,
                _metricsState.GetDisconnectedConnections(),
                _metricsState.GetTimedOutConnections(),
                _metricsState.GetSendQueueOverflows(),
                _metricsState.GetSocketAcceptErrors(),
                _metricsState.GetSocketReceiveErrors(),
                _metricsState.GetSocketSendErrors(),
                _metricsState.GetZeroByteReceives(),
                receiveOperations,
                sendOperations,
                bytesReceived,
                bytesSent,
                receiveBytesPerSecond,
                sendBytesPerSecond,
                receiveOpsPerSecond,
                sendOpsPerSecond,
                acceptsPerSecond,
                _metricsState.GetTotalSendQueuedBytes(),
                _metricsState.GetInFlightAsyncCallbacks(),
                _metricsState.GetDisconnectCleanupQueueDepth(),
                _acceptPool.CurrentCount,
                _clientRegistry.GetAvailableClientSlotCount(),
                null,
                disconnectsByReason,
                laneActiveConnections,
                connectionDurationBuckets,
                receiveSizeBuckets,
                sendSizeBuckets,
                socketErrorsByCode,
                _metricsState.SocketErrorCodeMinimum
            );
        }
    }
}
