using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Integration coverage for phase 3 threaded consumer metrics.
/// </summary>
public sealed class ThreadedTcpServerMetricsTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MediumTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies threaded consumer queue depth and processed disconnect events are published.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedConsumerQueueDepthAndProcessedEvents()
    {
        using ThreadedDisconnectTest consumer = new ThreadedDisconnectTest(
            orderingLaneCount: 1,
            queueCapacityPerLane: 2,
            blockFirstDisconnect: true
        );
        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );
        IConsumer queuedConsumer = consumer;

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));
        await consumer.WaitForFirstDisconnectEnteredAsync(ShortTimeout);
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 2));

        TcpServerMetricsSnapshot queuedSnapshot = await WaitForSnapshotAsync(
            host,
            snapshot => snapshot.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                && consumerMetrics.QueueDepthByLane.Length == 1
                && consumerMetrics.QueueDepthByLane.Span[0] == 1
                && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) == 0
                && consumerMetrics.HandlerDurationBuckets.Length == 10
                && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                && GetCounterTotal(consumerMetrics.HandlerDurationBuckets) == 0
                && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets) == 0
                && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets) == 0
                && consumerMetrics.HandlerErrors == 0,
            MediumTimeout,
            "Timed out waiting for the queued threaded consumer metrics snapshot."
        );

        ConsumerMetricsSnapshot queuedConsumerMetrics = GetRequiredConsumerMetrics(queuedSnapshot);
        Assert.Equal(1, queuedConsumerMetrics.QueueDepthByLane.Span[0]);
        Assert.Equal(0, queuedConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
        Assert.Equal(10, queuedConsumerMetrics.HandlerDurationBuckets.Length);
        Assert.Equal(10, queuedConsumerMetrics.ReceivedDataQueueDelayBuckets.Length);
        Assert.Equal(10, queuedConsumerMetrics.ReceivedDataHandlerDurationBuckets.Length);
        Assert.Equal(0, GetCounterTotal(queuedConsumerMetrics.HandlerDurationBuckets));
        Assert.Equal(0, GetCounterTotal(queuedConsumerMetrics.ReceivedDataQueueDelayBuckets));
        Assert.Equal(0, GetCounterTotal(queuedConsumerMetrics.ReceivedDataHandlerDurationBuckets));
        Assert.Equal(0, queuedConsumerMetrics.HandlerErrors);

        consumer.ReleaseBlockedDisconnect();
        await consumer.WaitForDisconnectCountAsync(2, MediumTimeout);

        TcpServerMetricsSnapshot drainedSnapshot = await WaitForSnapshotAsync(
            host,
            snapshot => snapshot.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                && consumerMetrics.QueueDepthByLane.Length == 1
                && consumerMetrics.QueueDepthByLane.Span[0] == 0
                && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) == 2
                && consumerMetrics.HandlerDurationBuckets.Length == 10
                && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                && GetCounterTotal(consumerMetrics.HandlerDurationBuckets) == 2
                && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets) == 0
                && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets) == 0
                && consumerMetrics.HandlerErrors == 0,
            MediumTimeout,
            "Timed out waiting for the drained threaded consumer metrics snapshot."
        );

        ConsumerMetricsSnapshot drainedConsumerMetrics = GetRequiredConsumerMetrics(drainedSnapshot);
        Assert.Equal(0, drainedConsumerMetrics.QueueDepthByLane.Span[0]);
        Assert.Equal(2, drainedConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
        Assert.Equal(10, drainedConsumerMetrics.HandlerDurationBuckets.Length);
        Assert.Equal(10, drainedConsumerMetrics.ReceivedDataQueueDelayBuckets.Length);
        Assert.Equal(10, drainedConsumerMetrics.ReceivedDataHandlerDurationBuckets.Length);
        Assert.Equal(2, GetCounterTotal(drainedConsumerMetrics.HandlerDurationBuckets));
        Assert.Equal(0, GetCounterTotal(drainedConsumerMetrics.ReceivedDataQueueDelayBuckets));
        Assert.Equal(0, GetCounterTotal(drainedConsumerMetrics.ReceivedDataHandlerDurationBuckets));
        Assert.Equal(0, drainedConsumerMetrics.HandlerErrors);
    }

    /// <summary>
    /// Verifies threaded consumer handler failures are counted without killing the lane.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedConsumerHandlerErrors()
    {
        using ThreadedDisconnectTest consumer = new ThreadedDisconnectTest(
            orderingLaneCount: 1,
            throwOnFirstDisconnect: true
        );
        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.OrderingLaneCount = 1;
                settings.ConcurrentAccepts = 1;
            }
        );
        IConsumer queuedConsumer = consumer;

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 2));

        await consumer.WaitForDisconnectCountAsync(1, MediumTimeout);

        TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
            host,
            candidate => candidate.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                && consumerMetrics.QueueDepthByLane.Length == 1
                && consumerMetrics.QueueDepthByLane.Span[0] == 0
                && consumerMetrics.HandlerErrors == 1
                && consumerMetrics.HandlerDurationBuckets.Length == 10
                && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                && GetCounterTotal(consumerMetrics.HandlerDurationBuckets) == 1
                && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets) == 0
                && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets) == 0
                && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) == 1,
            MediumTimeout,
            "Timed out waiting for the threaded consumer handler error metrics snapshot."
        );

        ConsumerMetricsSnapshot snapshotConsumerMetrics = GetRequiredConsumerMetrics(snapshot);
        Assert.Equal(1, consumer.HandlerExceptionCount);
        Assert.Equal(1, snapshotConsumerMetrics.HandlerErrors);
        Assert.Equal(10, snapshotConsumerMetrics.HandlerDurationBuckets.Length);
        Assert.Equal(10, snapshotConsumerMetrics.ReceivedDataQueueDelayBuckets.Length);
        Assert.Equal(10, snapshotConsumerMetrics.ReceivedDataHandlerDurationBuckets.Length);
        Assert.Equal(1, GetCounterTotal(snapshotConsumerMetrics.HandlerDurationBuckets));
        Assert.Equal(0, GetCounterTotal(snapshotConsumerMetrics.ReceivedDataQueueDelayBuckets));
        Assert.Equal(0, GetCounterTotal(snapshotConsumerMetrics.ReceivedDataHandlerDurationBuckets));
        Assert.Equal(1, snapshotConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
    }

    /// <summary>
    /// Verifies threaded consumer metrics reflect connected, received, and disconnected server events.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedConsumerServerEventTypes()
    {
        ThreadedEchoRecording consumer = new ThreadedEchoRecording(
            orderingLaneCount: 1,
            echoReceivedData: true
        );

        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
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

            byte[] payload = CreatePayload(128, 17);
            byte[] echoed = await host.RoundTripAsync(client, payload, MediumTimeout);
            Assert.Equal(payload, echoed);

            host.DisposeClient(client);
            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot snapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                    && consumerMetrics.QueueDepthByLane.Length == 1
                    && consumerMetrics.QueueDepthByLane.Span[0] == 0
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.Connected) >= 1
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData) >= 1
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) >= 1
                    && consumerMetrics.HandlerDurationBuckets.Length == 10
                    && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                    && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                    && GetCounterTotal(consumerMetrics.HandlerDurationBuckets)
                        == GetConsumerEventCountTotal(consumerMetrics)
                    && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets)
                        == consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData)
                    && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets)
                        == consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData)
                    && consumerMetrics.HandlerErrors == 0,
                MediumTimeout,
                "Timed out waiting for the threaded consumer server event metrics snapshot."
            );

            ConsumerMetricsSnapshot consumerMetrics = GetRequiredConsumerMetrics(snapshot);
            Assert.Equal(0, consumerMetrics.QueueDepthByLane.Span[0]);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Connected) >= 1);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData) >= 1);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) >= 1);
            Assert.Equal(10, consumerMetrics.HandlerDurationBuckets.Length);
            Assert.Equal(10, consumerMetrics.ReceivedDataQueueDelayBuckets.Length);
            Assert.Equal(10, consumerMetrics.ReceivedDataHandlerDurationBuckets.Length);
            Assert.Equal(GetConsumerEventCountTotal(consumerMetrics), GetCounterTotal(consumerMetrics.HandlerDurationBuckets));
            Assert.Equal(
                consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData),
                GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets)
            );
            Assert.Equal(
                consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData),
                GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets)
            );
            Assert.Equal(0, consumerMetrics.HandlerErrors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies threaded consumer metrics publish received-data queue delays from handoff attempt to handler start.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedReceivedDataQueueDelayAndHandlerDuration()
    {
        ThreadedEchoRecording consumer = new ThreadedEchoRecording(
            orderingLaneCount: 1,
            blockFirstReceive: true
        );

        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
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

            byte[] firstPayload = CreatePayload(64, 31);
            byte[] secondPayload = CreatePayload(64, 47);
            await host.WriteAsync(client, firstPayload, ShortTimeout);
            await consumer.WaitForBlockedReceiveAsync(ShortTimeout);
            await host.WriteAsync(client, secondPayload, ShortTimeout);

            TcpServerMetricsSnapshot queuedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                    && consumerMetrics.QueueDepthByLane.Length == 1
                    && consumerMetrics.QueueDepthByLane.Span[0] == 1
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData) == 0
                    && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                    && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                    && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets) == 1
                    && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets) == 0,
                MediumTimeout,
                "Timed out waiting for queued threaded receive-delay metrics."
            );

            ConsumerMetricsSnapshot queuedConsumerMetrics = GetRequiredConsumerMetrics(queuedSnapshot);
            Assert.Equal(1, queuedConsumerMetrics.QueueDepthByLane.Span[0]);
            Assert.Equal(1, GetCounterTotal(queuedConsumerMetrics.ReceivedDataQueueDelayBuckets));
            Assert.Equal(0, GetCounterTotal(queuedConsumerMetrics.ReceivedDataHandlerDurationBuckets));

            consumer.ReleaseBlockedReceive();
            await consumer.WaitForTotalReceivedBytesAsync(firstPayload.Length + secondPayload.Length, MediumTimeout);

            TcpServerMetricsSnapshot drainedSnapshot = await WaitForSnapshotAsync(
                host,
                candidate => candidate.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                    && consumerMetrics.QueueDepthByLane.Length == 1
                    && consumerMetrics.QueueDepthByLane.Span[0] == 0
                    && consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData) == 2
                    && consumerMetrics.ReceivedDataQueueDelayBuckets.Length == 10
                    && consumerMetrics.ReceivedDataHandlerDurationBuckets.Length == 10
                    && GetCounterTotal(consumerMetrics.ReceivedDataQueueDelayBuckets) == 2
                    && GetCounterTotal(consumerMetrics.ReceivedDataHandlerDurationBuckets) == 2
                    && GetCounterTotalBeyondFirstBucket(consumerMetrics.ReceivedDataQueueDelayBuckets) >= 1,
                MediumTimeout,
                "Timed out waiting for drained threaded receive-delay metrics."
            );

            ConsumerMetricsSnapshot drainedConsumerMetrics = GetRequiredConsumerMetrics(drainedSnapshot);
            Assert.Equal(0, drainedConsumerMetrics.QueueDepthByLane.Span[0]);
            Assert.Equal(2, drainedConsumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData));
            Assert.Equal(2, GetCounterTotal(drainedConsumerMetrics.ReceivedDataQueueDelayBuckets));
            Assert.Equal(2, GetCounterTotal(drainedConsumerMetrics.ReceivedDataHandlerDurationBuckets));
            Assert.True(GetCounterTotalBeyondFirstBucket(drainedConsumerMetrics.ReceivedDataQueueDelayBuckets) >= 1);
        }
        finally
        {
            consumer.ReleaseBlockedReceive();
            host.DisposeClient(client);
        }
    }

    private static ClientSnapshot CreateSnapshot(int unitOfOrder, ushort clientId)
    {
        return new ClientSnapshot(
            clientId,
            1,
            $"Client-{clientId}",
            IPAddress.Loopback,
            checked((ushort)(3000 + clientId)),
            false,
            DateTime.UtcNow,
            0,
            0,
            0,
            0,
            0,
            0,
            unitOfOrder
        );
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)((index * 19) + (seed * 7)));
        }

        return payload;
    }

    private static async Task<TcpServerMetricsSnapshot> WaitForSnapshotAsync(
        ThreadedConsumerTestHost host,
        Func<TcpServerMetricsSnapshot, bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        TcpServerMetricsSnapshot lastSnapshot = host.MetricsCollector.GetMetricsSnapshot();

        await TestWait.UntilAsync(
            () =>
            {
                lastSnapshot = host.MetricsCollector.GetMetricsSnapshot();
                return predicate(lastSnapshot);
            },
            timeout,
            $"{failureMessage}{Environment.NewLine}Last snapshot: {DescribeSnapshot(lastSnapshot)}"
        ).ConfigureAwait(false);

        return host.MetricsCollector.GetMetricsSnapshot();
    }

    private static string DescribeSnapshot(TcpServerMetricsSnapshot snapshot)
    {
        ConsumerMetricsSnapshot? consumerMetrics = snapshot.ConsumerMetrics;
        long disconnectedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.Disconnected) ?? 0;
        long connectedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.Connected) ?? 0;
        long receivedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.ReceivedData) ?? 0;
        long queueDepth = 0;
        long handlerDurationTotal = 0;
        long receivedDataQueueDelayTotal = 0;
        long receivedDataHandlerDurationTotal = 0;
        if (consumerMetrics is ConsumerMetricsSnapshot metrics && metrics.QueueDepthByLane.Length > 0)
        {
            queueDepth = metrics.QueueDepthByLane.Span[0];
            handlerDurationTotal = GetCounterTotal(metrics.HandlerDurationBuckets);
            receivedDataQueueDelayTotal = GetCounterTotal(metrics.ReceivedDataQueueDelayBuckets);
            receivedDataHandlerDurationTotal = GetCounterTotal(metrics.ReceivedDataHandlerDurationBuckets);
        }

        return
            $"consumerHandlerErrors={consumerMetrics?.HandlerErrors ?? 0}, " +
            $"connectedProcessed={connectedProcessed}, " +
            $"receivedProcessed={receivedProcessed}, " +
            $"disconnectedProcessed={disconnectedProcessed}, " +
            $"handlerDurations={handlerDurationTotal}, " +
            $"receivedDataQueueDelays={receivedDataQueueDelayTotal}, " +
            $"receivedDataHandlerDurations={receivedDataHandlerDurationTotal}, " +
            $"laneZeroQueueDepth={queueDepth}.";
    }

    private static ConsumerMetricsSnapshot GetRequiredConsumerMetrics(TcpServerMetricsSnapshot snapshot)
    {
        Assert.True(snapshot.ConsumerMetrics.HasValue);
        return snapshot.ConsumerMetrics.Value;
    }

    private static long GetConsumerEventCountTotal(ConsumerMetricsSnapshot snapshot)
    {
        return
            snapshot.GetEventsProcessedCount(ClientEventType.Connected) +
            snapshot.GetEventsProcessedCount(ClientEventType.ReceivedData) +
            snapshot.GetEventsProcessedCount(ClientEventType.Disconnected) +
            snapshot.GetEventsProcessedCount(ClientEventType.Error);
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

    private static long GetCounterTotalBeyondFirstBucket(ReadOnlyMemory<long> counters)
    {
        long total = 0;
        ReadOnlySpan<long> values = counters.Span;

        for (int index = 1; index < values.Length; index++)
        {
            total += values[index];
        }

        return total;
    }
}
