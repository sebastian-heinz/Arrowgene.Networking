using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Integration coverage for tcpServer metrics snapshots.
/// </summary>
public sealed class TcpServerMetricsTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MediumTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Verifies connection lifecycle and transfer metrics are published.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksConnectionLifecycleAndTraffic()
    {
        RecordingConsumer consumer = new RecordingConsumer(echoReceivedData: true);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
                settings.BufferSize = 256;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            TcpServerMetricsSnapshot preTrafficSnapshot = host.TcpServer.GetMetricsSnapshot();

            byte[] payload = CreatePayload(256, 11);
            byte[] echoed = await host.RoundTripAsync(client, payload, MediumTimeout);

            Assert.Equal(payload, echoed);

            TcpServerMetricsSnapshot connectedSnapshot = await WaitForPublishedSnapshotAsync(
                host,
                snapshot =>
                    snapshot.SnapshotSequenceNumber > preTrafficSnapshot.SnapshotSequenceNumber
                    && snapshot.ServerStartedAtUtc == preTrafficSnapshot.ServerStartedAtUtc
                    && snapshot.AcceptedConnections >= 1
                    && snapshot.ActiveConnections == 1
                    && snapshot.ReceiveOperations >= 1
                    && snapshot.SendOperations >= 1
                    && snapshot.ReceiveOpsPerSecond > 0.0d
                    && snapshot.SendOpsPerSecond > 0.0d
                    && snapshot.BytesReceived >= payload.Length
                    && snapshot.BytesSent >= payload.Length
                    && snapshot.AvailableClientSlots == 0,
                MediumTimeout,
                "Timed out waiting for the connected traffic metrics snapshot."
            );

            Assert.Equal(10, connectedSnapshot.ReceiveSizeBuckets.Length);
            Assert.Equal(10, connectedSnapshot.SendSizeBuckets.Length);
            Assert.Equal(10, connectedSnapshot.ConnectionDurationBuckets.Length);
            Assert.True(connectedSnapshot.SnapshotSequenceNumber > preTrafficSnapshot.SnapshotSequenceNumber);
            Assert.Equal(preTrafficSnapshot.ServerStartedAtUtc, connectedSnapshot.ServerStartedAtUtc);
            Assert.True(connectedSnapshot.ServerStartedAtUtc <= connectedSnapshot.TimestampUtc);
            Assert.True(connectedSnapshot.AcceptedConnections >= 1);
            Assert.Equal(1, connectedSnapshot.ActiveConnections);
            Assert.True(connectedSnapshot.PeakActiveConnections >= connectedSnapshot.ActiveConnections);
            Assert.True(connectedSnapshot.ReceiveOperations >= 1);
            Assert.True(connectedSnapshot.SendOperations >= 1);
            Assert.True(connectedSnapshot.ReceiveOpsPerSecond > 0.0d);
            Assert.True(connectedSnapshot.SendOpsPerSecond > 0.0d);
            Assert.True(connectedSnapshot.BytesReceived >= payload.Length);
            Assert.True(connectedSnapshot.BytesSent >= payload.Length);
            Assert.Equal(0, connectedSnapshot.AvailableClientSlots);
            Assert.Equal(1, GetLaneConnectionTotal(connectedSnapshot));
            Assert.Equal(connectedSnapshot.ReceiveOperations, GetCounterTotal(connectedSnapshot.ReceiveSizeBuckets));
            Assert.Equal(connectedSnapshot.SendOperations, GetCounterTotal(connectedSnapshot.SendSizeBuckets));
            Assert.Equal(0, GetCounterTotal(connectedSnapshot.ConnectionDurationBuckets));
            Assert.False(connectedSnapshot.ConsumerMetrics.HasValue);

            host.DisposeClient(client);

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot disconnectedSnapshot = await WaitForSnapshotAsync(
                host,
                snapshot =>
                    snapshot.SnapshotSequenceNumber >= connectedSnapshot.SnapshotSequenceNumber
                    && snapshot.ServerStartedAtUtc == connectedSnapshot.ServerStartedAtUtc
                    && snapshot.ActiveConnections == 0
                    && snapshot.PeakActiveConnections >= snapshot.ActiveConnections
                    && snapshot.DisconnectedConnections >= 1
                    && snapshot.AvailableClientSlots == 1
                    && GetDisconnectCount(snapshot, DisconnectReason.RemoteClosed) >= 1,
                MediumTimeout,
                "Timed out waiting for the disconnected traffic metrics snapshot."
            );

            Assert.Equal(0, GetLaneConnectionTotal(disconnectedSnapshot));
            Assert.True(disconnectedSnapshot.SnapshotSequenceNumber >= connectedSnapshot.SnapshotSequenceNumber);
            Assert.Equal(connectedSnapshot.ServerStartedAtUtc, disconnectedSnapshot.ServerStartedAtUtc);
            Assert.True(disconnectedSnapshot.PeakActiveConnections >= disconnectedSnapshot.ActiveConnections);
            Assert.Equal(10, disconnectedSnapshot.ConnectionDurationBuckets.Length);
            Assert.Equal(
                disconnectedSnapshot.DisconnectedConnections,
                GetCounterTotal(disconnectedSnapshot.ConnectionDurationBuckets)
            );
            Assert.True(GetCounterTotal(disconnectedSnapshot.ConnectionDurationBuckets) >= 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies passive metrics reads do not force a new snapshot capture.
    /// </summary>
    [Fact]
    public void GetPublishedMetricsSnapshot_DoesNotAdvanceSnapshotSequence()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        TcpServerMetricsSnapshot capturedSnapshot = host.TcpServer.GetMetricsSnapshot();
        TcpServerMetricsSnapshot publishedSnapshot = host.TcpServer.GetPublishedMetricsSnapshot();

        Assert.Equal(capturedSnapshot.SnapshotSequenceNumber, publishedSnapshot.SnapshotSequenceNumber);
        Assert.Equal(capturedSnapshot.TimestampUtc, publishedSnapshot.TimestampUtc);
        Assert.Equal(capturedSnapshot.ServerStartedAtUtc, publishedSnapshot.ServerStartedAtUtc);
    }

    /// <summary>
    /// Verifies the aggregate queued-send-bytes gauge rises under backpressure and returns to zero after draining.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksTotalSendQueuedBytesGauge()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
                settings.BufferSize = 256;
                settings.MaxQueuedSendBytes = 8 * 1024 * 1024;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            ConnectedClientRecord connectedClient = consumer.GetConnectedClient(0);
            byte[] payload = CreatePayload(4 * 1024 * 1024, 57);
            connectedClient.Handle.Send(payload);

            TcpServerMetricsSnapshot queuedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.TotalSendQueuedBytes > 0,
                MediumTimeout,
                "Timed out waiting for queued send bytes to appear in the metrics snapshot."
            );

            Assert.True(queuedSnapshot.TotalSendQueuedBytes > 0);

            byte[] received = await host.ReadExactAsync(client, payload.Length, LongTimeout);
            Assert.Equal(payload, received);

            TcpServerMetricsSnapshot drainedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.TotalSendQueuedBytes == 0
                    && candidate.BytesSent >= payload.Length
                    && candidate.SendOperations >= 1,
                LongTimeout,
                "Timed out waiting for queued send bytes to drain from the metrics snapshot."
            );

            Assert.Equal(0, drainedSnapshot.TotalSendQueuedBytes);
            Assert.True(drainedSnapshot.BytesSent >= payload.Length);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }

        await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);
    }

    /// <summary>
    /// Verifies non-threaded consumers can opt in to metrics publication through <see cref="IConsumerMetrics"/>.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_UsesInterfaceDrivenConsumerMetricsForOptInConsumer()
    {
        MetricsAwareTestConsumer consumer = new MetricsAwareTestConsumer(metricsLaneCount: 1);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        await TestWait.UntilAsync(
            () => consumer.EnableCaptureCount >= 1,
            ShortTimeout,
            "Timed out waiting for consumer metrics capture to be enabled."
        );

        consumer.SetQueueDepth(0, 7);
        consumer.SetHandlerErrors(3);

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await TestWait.UntilAsync(
                () => consumer.ConnectedCount >= 1,
                ShortTimeout,
                "Timed out waiting for the opt-in metrics consumer to observe a connection."
            );

            host.DisposeClient(client);

            await TestWait.UntilAsync(
                () => consumer.DisconnectedCount >= 1,
                MediumTimeout,
                "Timed out waiting for the opt-in metrics consumer to observe a disconnect."
            );

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                    && consumerMetrics.QueueDepthByLane.Length == 1
                    && consumerMetrics.QueueDepthByLane.Span[0] == 7
                    && consumerMetrics.HandlerErrors == 3
                    && consumerMetrics.HandlerDurationBuckets.Length == 10
                    && GetCounterTotal(consumerMetrics.HandlerDurationBuckets) == 0
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.Connected) >= 1
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) >= 1,
                MediumTimeout,
                "Timed out waiting for interface-driven consumer metrics."
            );

            ConsumerMetricsSnapshot consumerMetrics = GetRequiredConsumerMetrics(snapshot);
            Assert.Equal(7, consumerMetrics.QueueDepthByLane.Span[0]);
            Assert.Equal(3, consumerMetrics.HandlerErrors);
            Assert.Equal(10, consumerMetrics.HandlerDurationBuckets.Length);
            Assert.Equal(0, GetCounterTotal(consumerMetrics.HandlerDurationBuckets));
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Connected) >= 1);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) >= 1);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies phase 2 snapshot gauges track accept-pool and client-pool availability.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksPhase2AvailabilityGauges()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        TcpServerMetricsSnapshot initialSnapshot = await WaitForSnapshotAsync(
            host,
            candidate => candidate.AcceptPoolAvailable == 0 && candidate.AvailableClientSlots == 2,
            MediumTimeout,
            "Timed out waiting for the initial phase 2 availability gauges."
        );

        Assert.Equal(0, initialSnapshot.AcceptPoolAvailable);
        Assert.Equal(2, initialSnapshot.AvailableClientSlots);

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            TcpServerMetricsSnapshot connectedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.ActiveConnections == 1
                    && candidate.AcceptPoolAvailable == 0
                    && candidate.AvailableClientSlots == 1,
                MediumTimeout,
                "Timed out waiting for the connected phase 2 availability gauges."
            );

            Assert.Equal(0, connectedSnapshot.AcceptPoolAvailable);
            Assert.Equal(1, connectedSnapshot.AvailableClientSlots);

            host.DisposeClient(client);

            TcpServerMetricsSnapshot disconnectedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.ActiveConnections == 0
                    && candidate.AcceptPoolAvailable == 0
                    && candidate.AvailableClientSlots == 2,
                MediumTimeout,
                "Timed out waiting for the disconnected phase 2 availability gauges."
            );

            Assert.Equal(0, disconnectedSnapshot.AcceptPoolAvailable);
            Assert.Equal(2, disconnectedSnapshot.AvailableClientSlots);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies connection-cap rejections are counted in the metrics snapshot.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksRejectedConnectionsAtConnectionCap()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        List<TcpClient> clients = new List<TcpClient>();

        try
        {
            for (int index = 0; index < 3; index++)
            {
                clients.Add(await host.ConnectClientAsync());
            }

            await consumer.WaitForConnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.AcceptedConnections >= 1
                    && candidate.RejectedConnections >= 2
                    && candidate.ActiveConnections == 1,
                MediumTimeout,
                "Timed out waiting for connection-cap rejection metrics."
            );

            Assert.Equal(1, snapshot.AcceptedConnections);
            Assert.True(snapshot.RejectedConnections >= 2);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            foreach (TcpClient client in clients)
            {
                host.DisposeClient(client);
            }
        }
    }

    /// <summary>
    /// Verifies timeout-driven disconnects are counted with the timeout reason.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksTimeoutDisconnectReason()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
                settings.ClientSocketTimeoutSeconds = 1;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);
            await consumer.WaitForDisconnectedCountAsync(1, LongTimeout);

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.ActiveConnections == 0
                    && candidate.TimedOutConnections >= 1
                    && GetDisconnectCount(candidate, DisconnectReason.Timeout) >= 1,
                LongTimeout,
                "Timed out waiting for timeout metrics."
            );

            Assert.True(snapshot.TimedOutConnections >= 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies graceful remote close signals are counted as zero-byte receives.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksZeroByteReceivesForRemoteClose()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            host.DisposeClient(client);

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.ActiveConnections == 0
                    && candidate.ZeroByteReceives >= 1
                    && candidate.ReceiveOperations == 0
                    && candidate.BytesReceived == 0
                    && GetDisconnectCount(candidate, DisconnectReason.RemoteClosed) >= 1,
                MediumTimeout,
                "Timed out waiting for zero-byte receive metrics."
            );

            Assert.True(snapshot.ZeroByteReceives >= 1);
            Assert.True(GetDisconnectCount(snapshot, DisconnectReason.RemoteClosed) >= 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies send queue overflows are counted and attributed to the correct disconnect reason.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksSendQueueOverflowDisconnectReason()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
                settings.BufferSize = 256;
                settings.MaxQueuedSendBytes = 512;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            ConnectedClientRecord connectedClient = consumer.GetConnectedClient(0);
            connectedClient.Handle.Send(CreatePayload(1024, 29));

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate =>
                    candidate.ActiveConnections == 0
                    && candidate.SendQueueOverflows >= 1
                    && GetDisconnectCount(candidate, DisconnectReason.SendQueueOverflow) >= 1,
                MediumTimeout,
                "Timed out waiting for send queue overflow metrics."
            );

            Assert.True(snapshot.SendQueueOverflows >= 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies stop-time disconnects do not change the published metrics counters.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_StopDoesNotAdvanceCountersAfterLeavingRunningState()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            TcpServerMetricsSnapshot runningSnapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.AcceptedConnections == 1 && candidate.ActiveConnections == 1,
                MediumTimeout,
                "Timed out waiting for the running metrics snapshot before stop."
            );

            host.TcpServer.Stop();
            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot stoppedSnapshot = host.TcpServer.GetMetricsSnapshot();

            Assert.Equal(runningSnapshot.AcceptedConnections, stoppedSnapshot.AcceptedConnections);
            Assert.Equal(runningSnapshot.RejectedConnections, stoppedSnapshot.RejectedConnections);
            Assert.Equal(runningSnapshot.DisconnectedConnections, stoppedSnapshot.DisconnectedConnections);
            Assert.Equal(runningSnapshot.TimedOutConnections, stoppedSnapshot.TimedOutConnections);
            Assert.Equal(runningSnapshot.SendQueueOverflows, stoppedSnapshot.SendQueueOverflows);
            Assert.Equal(
                GetDisconnectCount(runningSnapshot, DisconnectReason.Shutdown),
                GetDisconnectCount(stoppedSnapshot, DisconnectReason.Shutdown)
            );
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)((index * 17) + (seed * 31)));
        }

        return payload;
    }

    private static long GetDisconnectCount(
        TcpServerMetricsSnapshot snapshot,
        DisconnectReason disconnectReason)
    {
        return snapshot.DisconnectsByReason.Span[(int)disconnectReason];
    }

    private static long GetLaneConnectionTotal(TcpServerMetricsSnapshot snapshot)
    {
        long total = 0;
        ReadOnlySpan<long> laneActiveConnections = snapshot.LaneActiveConnections.Span;

        for (int index = 0; index < laneActiveConnections.Length; index++)
        {
            total += laneActiveConnections[index];
        }

        return total;
    }

    private static long GetCounterTotal(ReadOnlyMemory<long> counters)
    {
        long total = 0;
        ReadOnlySpan<long> values = counters.Span;

        for (int index = 0; index < values.Length; index++)
        {
            total += values[index];
        }

        return total;
    }

    private static ConsumerMetricsSnapshot GetRequiredConsumerMetrics(TcpServerMetricsSnapshot snapshot)
    {
        Assert.True(snapshot.ConsumerMetrics.HasValue);
        return snapshot.ConsumerMetrics.Value;
    }

    private static async Task<TcpServerMetricsSnapshot> WaitForSnapshotAsync(
        ServerTestHost host,
        Func<TcpServerMetricsSnapshot, bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        TcpServerMetricsSnapshot lastSnapshot = host.TcpServer.GetMetricsSnapshot();

        await TestWait.UntilAsync(
            () =>
            {
                lastSnapshot = host.TcpServer.GetMetricsSnapshot();
                return predicate(lastSnapshot);
            },
            timeout,
            $"{failureMessage}{Environment.NewLine}Last snapshot: {DescribeSnapshot(lastSnapshot)}"
        ).ConfigureAwait(false);

        return host.TcpServer.GetMetricsSnapshot();
    }

    private static async Task<TcpServerMetricsSnapshot> WaitForPublishedSnapshotAsync(
        ServerTestHost host,
        Func<TcpServerMetricsSnapshot, bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        TcpServerMetricsSnapshot lastSnapshot = host.TcpServer.GetPublishedMetricsSnapshot();

        await TestWait.UntilAsync(
            () =>
            {
                lastSnapshot = host.TcpServer.GetPublishedMetricsSnapshot();
                return predicate(lastSnapshot);
            },
            timeout,
            $"{failureMessage}{Environment.NewLine}Last snapshot: {DescribeSnapshot(lastSnapshot)}"
        ).ConfigureAwait(false);

        return host.TcpServer.GetPublishedMetricsSnapshot();
    }

    private static string DescribeSnapshot(TcpServerMetricsSnapshot snapshot)
    {
        return
            $"sequence={snapshot.SnapshotSequenceNumber}, " +
            $"startedAt={snapshot.ServerStartedAtUtc:O}, " +
            $"accepted={snapshot.AcceptedConnections}, " +
            $"rejected={snapshot.RejectedConnections}, " +
            $"active={snapshot.ActiveConnections}, " +
            $"peakActive={snapshot.PeakActiveConnections}, " +
            $"disconnected={snapshot.DisconnectedConnections}, " +
            $"timedOut={snapshot.TimedOutConnections}, " +
            $"queueOverflows={snapshot.SendQueueOverflows}, " +
            $"acceptErrors={snapshot.SocketAcceptErrors}, " +
            $"receiveErrors={snapshot.SocketReceiveErrors}, " +
            $"sendErrors={snapshot.SocketSendErrors}, " +
            $"zeroByteReceives={snapshot.ZeroByteReceives}, " +
            $"receiveOps={snapshot.ReceiveOperations}, " +
            $"sendOps={snapshot.SendOperations}, " +
            $"receiveOpsPerSecond={snapshot.ReceiveOpsPerSecond:F2}, " +
            $"sendOpsPerSecond={snapshot.SendOpsPerSecond:F2}, " +
            $"acceptsPerSecond={snapshot.AcceptsPerSecond:F2}, " +
            $"totalSendQueuedBytes={snapshot.TotalSendQueuedBytes}, " +
            $"bytesReceived={snapshot.BytesReceived}, " +
            $"bytesSent={snapshot.BytesSent}, " +
            $"laneActiveTotal={GetLaneConnectionTotal(snapshot)}, " +
            $"acceptPoolAvailable={snapshot.AcceptPoolAvailable}, " +
            $"availableClientSlots={snapshot.AvailableClientSlots}, " +
            $"timeoutDisconnects={GetDisconnectCount(snapshot, DisconnectReason.Timeout)}, " +
            $"overflowDisconnects={GetDisconnectCount(snapshot, DisconnectReason.SendQueueOverflow)}.";
    }
}
