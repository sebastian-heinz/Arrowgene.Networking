using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;
using Xunit;

namespace Arrowgene.Networking.Tests;

public sealed class ThreadedBlockingQueueConsumerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MediumTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void DisconnectLaneOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(1);
        IConsumer queuedConsumer = consumer;
        ClientSnapshot snapshot = CreateSnapshot(unitOfOrder: 1, clientId: 1);

        consumer.Start();

        Assert.Throws<ArgumentOutOfRangeException>(() => queuedConsumer.OnClientDisconnected(snapshot));
    }

    [Fact]
    public async Task BoundedLaneQueue_BackpressuresProducerUntilWorkerDrains()
    {
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(
            orderingLaneCount: 1,
            queueCapacityPerLane: 1,
            blockFirstDisconnect: true
        );
        IConsumer queuedConsumer = consumer;

        consumer.Start();

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));
        await consumer.WaitForFirstDisconnectEnteredAsync(ShortTimeout);

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 2));

        Task thirdEnqueue = Task.Run(
            () => queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 3)),
            TestContext.Current.CancellationToken
        );

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(thirdEnqueue.IsCompleted);

        consumer.ReleaseBlockedDisconnect();

        await thirdEnqueue.WaitAsync(MediumTimeout, TestContext.Current.CancellationToken);
        await consumer.WaitForDisconnectCountAsync(3, MediumTimeout);
    }

    [Fact]
    public async Task HandlerException_DoesNotKillLaneThread()
    {
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(
            orderingLaneCount: 1,
            throwOnFirstDisconnect: true
        );
        IConsumer queuedConsumer = consumer;

        consumer.Start();

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 2));

        await consumer.WaitForDisconnectCountAsync(1, MediumTimeout);

        Assert.Equal(1, consumer.HandlerExceptionCount);
        Assert.Equal(2, consumer.GetDisconnectedSnapshot(0).ClientId);
    }

    [Fact]
    public void StopAndDispose_AreIdempotent_AndIgnoreLateEvents()
    {
        ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(1);
        IConsumer queuedConsumer = consumer;

        consumer.Start();
        consumer.Stop();
        consumer.Stop();
        consumer.Dispose();
        consumer.Dispose();

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));

        Assert.Equal(0, consumer.DisconnectCount);
    }

    [Fact]
    public async Task LaneOrdering_PreservesPerLaneDisconnectSequence()
    {
        using ThreadedDisconnectTestConsumer consumer = new ThreadedDisconnectTestConsumer(orderingLaneCount: 2);
        IConsumer queuedConsumer = consumer;

        consumer.Start();

        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 1));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 1, clientId: 100));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 2));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 1, clientId: 101));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 0, clientId: 3));
        queuedConsumer.OnClientDisconnected(CreateSnapshot(unitOfOrder: 1, clientId: 102));

        await consumer.WaitForDisconnectCountAsync(6, MediumTimeout);

        IReadOnlyList<ClientSnapshot> disconnects = consumer.GetDisconnectedSnapshots();
        List<int> laneZeroClientIds = new List<int>();
        List<int> laneOneClientIds = new List<int>();

        foreach (ClientSnapshot disconnect in disconnects)
        {
            if (disconnect.UnitOfOrder == 0)
            {
                laneZeroClientIds.Add(disconnect.ClientId);
            }
            else if (disconnect.UnitOfOrder == 1)
            {
                laneOneClientIds.Add(disconnect.ClientId);
            }
        }

        Assert.Equal(new[] { 1, 2, 3 }, laneZeroClientIds.ToArray());
        Assert.Equal(new[] { 100, 101, 102 }, laneOneClientIds.ToArray());
    }

    [Fact]
    public async Task Disconnection_PreservesAssignedLane()
    {
        ThreadedEchoRecordingConsumer consumer = new ThreadedEchoRecordingConsumer(orderingLaneCount: 2);

        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.OrderingLaneCount = 2;
                settings.ConcurrentAccepts = 2;
            }
        );

        TcpClient firstClient = await host.ConnectClientAsync();
        TcpClient secondClient = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(2, MediumTimeout);

            ConnectedClientRecord firstRecord = consumer.GetConnectedClient(0);
            host.DisposeClient(firstClient);

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            Assert.Equal(firstRecord.UnitOfOrder, consumer.GetDisconnectedClient(0).Snapshot.UnitOfOrder);
        }
        finally
        {
            host.DisposeClient(firstClient);
            host.DisposeClient(secondClient);
        }
    }

    [Fact]
    public async Task StressLoad_CanEchoAcrossMultipleLanes()
    {
        ThreadedEchoRecordingConsumer consumer = new ThreadedEchoRecordingConsumer(
            orderingLaneCount: 4,
            echoReceivedData: true,
            receiveDelayMs: 1
        );

        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 8;
                settings.OrderingLaneCount = 4;
                settings.ConcurrentAccepts = 4;
                settings.BufferSize = 256;
                settings.MaxQueuedSendBytes = 4 * 1024 * 1024;
            }
        );

        TcpClient[] clients = await Task.WhenAll(
            new[]
            {
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync(),
                host.ConnectClientAsync()
            }
        );

        try
        {
            await consumer.WaitForConnectedCountAsync(clients.Length, MediumTimeout);

            List<Task> workloads = new List<Task>();
            for (int clientIndex = 0; clientIndex < clients.Length; clientIndex++)
            {
                workloads.Add(RunStressClientAsync(host, clients[clientIndex], clientIndex));
            }

            await Task.WhenAll(workloads);

            Assert.True(consumer.TotalReceivedBytes > 0);
            Assert.True(consumer.MaxConcurrentHandlers > 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(clients.Length, MediumTimeout);
    }

    [Fact]
    public async Task LoadWaves_CanConnectRoundTripAndDisconnectAcrossBursts()
    {
        ThreadedEchoRecordingConsumer consumer = new ThreadedEchoRecordingConsumer(
            orderingLaneCount: 4,
            echoReceivedData: true,
            receiveDelayMs: 1
        );

        using ThreadedConsumerTestHost host = new ThreadedConsumerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 16;
                settings.OrderingLaneCount = 4;
                settings.ConcurrentAccepts = 4;
                settings.BufferSize = 256;
                settings.MaxQueuedSendBytes = 4 * 1024 * 1024;
            }
        );

        int expectedConnected = 0;
        int expectedDisconnected = 0;
        long expectedReceivedBytes = 0;

        for (int wave = 0; wave < 3; wave++)
        {
            Task<TcpClient>[] connectTasks = new Task<TcpClient>[12];
            for (int clientIndex = 0; clientIndex < connectTasks.Length; clientIndex++)
            {
                connectTasks[clientIndex] = host.ConnectClientAsync();
            }

            TcpClient[] clients = await Task.WhenAll(connectTasks);

            try
            {
                expectedConnected += clients.Length;
                await consumer.WaitForConnectedCountAsync(expectedConnected, MediumTimeout);

                Task<long>[] workloads = new Task<long>[clients.Length];
                for (int clientIndex = 0; clientIndex < clients.Length; clientIndex++)
                {
                    workloads[clientIndex] = RunLoadWaveClientAsync(host, clients[clientIndex], wave, clientIndex);
                }

                long[] waveReceivedBytes = await Task.WhenAll(workloads);
                for (int index = 0; index < waveReceivedBytes.Length; index++)
                {
                    expectedReceivedBytes += waveReceivedBytes[index];
                }
            }
            finally
            {
                DisposeClients(host, clients);
            }

            expectedDisconnected += clients.Length;
            await consumer.WaitForDisconnectedCountAsync(expectedDisconnected, MediumTimeout);
        }

        Assert.Equal(expectedReceivedBytes, consumer.TotalReceivedBytes);
        Assert.True(consumer.MaxConcurrentHandlers > 1);
        Assert.Empty(consumer.Errors);
    }

    private static ClientSnapshot CreateSnapshot(int unitOfOrder, ushort clientId)
    {
        return new ClientSnapshot(
            clientId,
            1,
            $"Client-{clientId}",
            IPAddress.Loopback,
            checked((ushort)(2000 + clientId)),
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

    private static void DisposeClients(ThreadedConsumerTestHost host, IEnumerable<TcpClient> clients)
    {
        foreach (TcpClient client in clients)
        {
            host.DisposeClient(client);
        }
    }

    private static async Task RunStressClientAsync(
        ThreadedConsumerTestHost host,
        TcpClient client,
        int clientIndex
    )
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            int payloadSize = 64 + ((clientIndex + 5) * (iteration + 3) * 29 % 1024);
            byte[] payload = CreatePayload(payloadSize, (clientIndex * 50) + iteration);
            byte[] echoed = await host.RoundTripAsync(client, payload, LongTimeout);
            Assert.Equal(payload, echoed);
        }
    }

    private static async Task<long> RunLoadWaveClientAsync(
        ThreadedConsumerTestHost host,
        TcpClient client,
        int wave,
        int clientIndex
    )
    {
        long totalBytes = 0;

        for (int iteration = 0; iteration < 5; iteration++)
        {
            int payloadSize = 48 + ((wave + 3) * (clientIndex + 5) * (iteration + 7) * 23 % 640);
            byte[] payload = CreatePayload(payloadSize, (wave * 1000) + (clientIndex * 100) + iteration);
            byte[] echoed = await host.RoundTripAsync(client, payload, LongTimeout);
            Assert.Equal(payload, echoed);
            totalBytes += payload.Length;
        }

        return totalBytes;
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)((index * 17) + (seed * 13)));
        }

        return payload;
    }
}
