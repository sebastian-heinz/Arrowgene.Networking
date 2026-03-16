using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
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

            byte[] payload = CreatePayload(256, 11);
            byte[] echoed = await host.RoundTripAsync(client, payload, MediumTimeout);

            Assert.Equal(payload, echoed);

            TcpServerMetricsSnapshot connectedSnapshot = await WaitForSnapshotAsync(
                host,
                snapshot =>
                    snapshot.AcceptedConnections >= 1
                    && snapshot.ActiveConnections == 1
                    && snapshot.ReceiveOperations >= 1
                    && snapshot.SendOperations >= 1
                    && snapshot.BytesReceived >= payload.Length
                    && snapshot.BytesSent >= payload.Length
                    && snapshot.AvailableClientSlots == 0
                    && GetLaneConnectionTotal(snapshot) == 1,
                MediumTimeout,
                "Timed out waiting for the connected traffic metrics snapshot."
            );

            Assert.Equal(7, connectedSnapshot.ReceiveSizeBuckets.Length);
            Assert.Equal(7, connectedSnapshot.SendSizeBuckets.Length);
            Assert.Equal(connectedSnapshot.ReceiveOperations, GetCounterTotal(connectedSnapshot.ReceiveSizeBuckets));
            Assert.Equal(connectedSnapshot.SendOperations, GetCounterTotal(connectedSnapshot.SendSizeBuckets));

            host.DisposeClient(client);

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpServerMetricsSnapshot disconnectedSnapshot = await WaitForSnapshotAsync(
                host,
                snapshot =>
                    snapshot.ActiveConnections == 0
                    && snapshot.DisconnectedConnections >= 1
                    && snapshot.AvailableClientSlots == 1
                    && GetDisconnectCount(snapshot, DisconnectReason.RemoteClosed) >= 1,
                MediumTimeout,
                "Timed out waiting for the disconnected traffic metrics snapshot."
            );

            Assert.Equal(0, GetLaneConnectionTotal(disconnectedSnapshot));
            Assert.Empty(consumer.Errors);
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

    private static string DescribeSnapshot(TcpServerMetricsSnapshot snapshot)
    {
        return
            $"accepted={snapshot.AcceptedConnections}, " +
            $"rejected={snapshot.RejectedConnections}, " +
            $"active={snapshot.ActiveConnections}, " +
            $"disconnected={snapshot.DisconnectedConnections}, " +
            $"timedOut={snapshot.TimedOutConnections}, " +
            $"queueOverflows={snapshot.SendQueueOverflows}, " +
            $"acceptErrors={snapshot.SocketAcceptErrors}, " +
            $"receiveErrors={snapshot.SocketReceiveErrors}, " +
            $"sendErrors={snapshot.SocketSendErrors}, " +
            $"receiveOps={snapshot.ReceiveOperations}, " +
            $"sendOps={snapshot.SendOperations}, " +
            $"bytesReceived={snapshot.BytesReceived}, " +
            $"bytesSent={snapshot.BytesSent}, " +
            $"acceptPoolAvailable={snapshot.AcceptPoolAvailable}, " +
            $"availableClientSlots={snapshot.AvailableClientSlots}, " +
            $"timeoutDisconnects={GetDisconnectCount(snapshot, DisconnectReason.Timeout)}, " +
            $"overflowDisconnects={GetDisconnectCount(snapshot, DisconnectReason.SendQueueOverflow)}.";
    }
}
