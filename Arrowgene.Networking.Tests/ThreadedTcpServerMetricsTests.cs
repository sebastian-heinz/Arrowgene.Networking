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
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(
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
                && consumerMetrics.HandlerErrors == 0,
            MediumTimeout,
            "Timed out waiting for the queued threaded consumer metrics snapshot."
        );

        ConsumerMetricsSnapshot queuedConsumerMetrics = GetRequiredConsumerMetrics(queuedSnapshot);
        Assert.Equal(1, queuedConsumerMetrics.QueueDepthByLane.Span[0]);
        Assert.Equal(0, queuedConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
        Assert.Equal(0, queuedConsumerMetrics.HandlerErrors);

        consumer.ReleaseBlockedDisconnect();
        await consumer.WaitForDisconnectCountAsync(2, MediumTimeout);

        TcpServerMetricsSnapshot drainedSnapshot = await WaitForSnapshotAsync(
            host,
            snapshot => snapshot.ConsumerMetrics is ConsumerMetricsSnapshot consumerMetrics
                && consumerMetrics.QueueDepthByLane.Length == 1
                && consumerMetrics.QueueDepthByLane.Span[0] == 0
                && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) == 2
                && consumerMetrics.HandlerErrors == 0,
            MediumTimeout,
            "Timed out waiting for the drained threaded consumer metrics snapshot."
        );

        ConsumerMetricsSnapshot drainedConsumerMetrics = GetRequiredConsumerMetrics(drainedSnapshot);
        Assert.Equal(0, drainedConsumerMetrics.QueueDepthByLane.Span[0]);
        Assert.Equal(2, drainedConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
        Assert.Equal(0, drainedConsumerMetrics.HandlerErrors);
    }

    /// <summary>
    /// Verifies threaded consumer handler failures are counted without killing the lane.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedConsumerHandlerErrors()
    {
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(
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
                && consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) == 1,
            MediumTimeout,
            "Timed out waiting for the threaded consumer handler error metrics snapshot."
        );

        ConsumerMetricsSnapshot snapshotConsumerMetrics = GetRequiredConsumerMetrics(snapshot);
        Assert.Equal(1, consumer.HandlerExceptionCount);
        Assert.Equal(1, snapshotConsumerMetrics.HandlerErrors);
        Assert.Equal(1, snapshotConsumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected));
    }

    /// <summary>
    /// Verifies threaded consumer metrics reflect connected, received, and disconnected server events.
    /// </summary>
    [Fact]
    public async Task MetricsSnapshot_TracksThreadedConsumerServerEventTypes()
    {
        ThreadedEchoRecordingConsumer consumer = new ThreadedEchoRecordingConsumer(
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
                    && consumerMetrics.HandlerErrors == 0,
                MediumTimeout,
                "Timed out waiting for the threaded consumer server event metrics snapshot."
            );

            ConsumerMetricsSnapshot consumerMetrics = GetRequiredConsumerMetrics(snapshot);
            Assert.Equal(0, consumerMetrics.QueueDepthByLane.Span[0]);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Connected) >= 1);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.ReceivedData) >= 1);
            Assert.True(consumerMetrics.GetEventsProcessedCount(ClientEventType.Disconnected) >= 1);
            Assert.Equal(0, consumerMetrics.HandlerErrors);
        }
        finally
        {
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

    private static string DescribeSnapshot(TcpServerMetricsSnapshot snapshot)
    {
        ConsumerMetricsSnapshot? consumerMetrics = snapshot.ConsumerMetrics;
        long disconnectedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.Disconnected) ?? 0;
        long connectedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.Connected) ?? 0;
        long receivedProcessed = consumerMetrics?.GetEventsProcessedCount(ClientEventType.ReceivedData) ?? 0;
        long queueDepth = 0;
        if (consumerMetrics is ConsumerMetricsSnapshot metrics && metrics.QueueDepthByLane.Length > 0)
        {
            queueDepth = metrics.QueueDepthByLane.Span[0];
        }

        return
            $"consumerHandlerErrors={consumerMetrics?.HandlerErrors ?? 0}, " +
            $"connectedProcessed={connectedProcessed}, " +
            $"receivedProcessed={receivedProcessed}, " +
            $"disconnectedProcessed={disconnectedProcessed}, " +
            $"laneZeroQueueDepth={queueDepth}.";
    }

    private static ConsumerMetricsSnapshot GetRequiredConsumerMetrics(TcpServerMetricsSnapshot snapshot)
    {
        Assert.True(snapshot.ConsumerMetrics.HasValue);
        return snapshot.ConsumerMetrics.Value;
    }
}
