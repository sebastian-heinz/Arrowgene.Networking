using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Integration coverage for the SAEA server lifecycle, transfer, and pooling behavior.
/// </summary>
public sealed class ServerIntegrationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MediumTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Verifies the server never activates more clients than the configured connection cap.
    /// </summary>
    [Fact]
    public async Task ConnectionCap_PreventsMoreThanMaxConnectionsFromActivating()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.OrderingLaneCount = 2;
                settings.ConcurrentAccepts = 2;
            }
        );

        List<TcpClient> clients = new List<TcpClient>();

        try
        {
            for (int index = 0; index < 4; index++)
            {
                clients.Add(await host.ConnectClientAsync());
            }

            await consumer.WaitForConnectedCountAsync(2, MediumTimeout);
            await Task.Delay(300);

            Assert.Equal(2, consumer.ConnectedCount);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(2, MediumTimeout);
    }

    /// <summary>
    /// Verifies ordering lanes are assigned deterministically by least load with lowest-index tie breaking.
    /// </summary>
    [Fact]
    public async Task OrderingLanes_FollowLeastLoadedSequenceAndReuseLowestTie()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 6;
                settings.OrderingLaneCount = 3;
                settings.ConcurrentAccepts = 3;
            }
        );

        List<TcpClient> clients = new List<TcpClient>();
        int[] expectedInitialLanes = new[] { 0, 1, 2, 0, 1 };

        try
        {
            for (int index = 0; index < expectedInitialLanes.Length; index++)
            {
                TcpClient client = await host.ConnectClientAsync();
                clients.Add(client);

                await consumer.WaitForConnectedCountAsync(index + 1, MediumTimeout);
                ConnectedClientRecord connectedClient = consumer.GetConnectedClient(index);
                Assert.Equal(expectedInitialLanes[index], connectedClient.UnitOfOrder);
            }

            host.DisposeClient(clients[0]);
            clients.RemoveAt(0);

            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            TcpClient replacement = await host.ConnectClientAsync();
            clients.Add(replacement);

            await consumer.WaitForConnectedCountAsync(6, MediumTimeout);
            ConnectedClientRecord replacementClient = consumer.GetConnectedClient(5);
            Assert.Equal(0, replacementClient.UnitOfOrder);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(6, MediumTimeout);
    }

    /// <summary>
    /// Verifies outbound sends copy the caller-provided buffer before asynchronous transmission.
    /// </summary>
    [Fact]
    public async Task SendCopiesCallerBufferBeforeAsyncWrite()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 256;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);
            ConnectedClientRecord connectedClient = consumer.GetConnectedClient(0);

            byte[] expected = CreatePayload(512, 7);
            byte[] buffer = expected.ToArray();

            connectedClient.Handle.Send(buffer);
            Array.Fill(buffer, (byte)0xFF);

            byte[] received = await host.ReadExactAsync(client, expected.Length, MediumTimeout);

            Assert.Equal(expected, received);
            Assert.NotEqual(buffer, received);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }

        await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);
    }

    /// <summary>
    /// Verifies a large payload is echoed back intact even when the server must split it into multiple buffer-sized chunks.
    /// </summary>
    [Fact]
    public async Task BigDataTransfer_EchoesEntirePayloadAcrossMultipleChunks()
    {
        RecordingConsumer consumer = new RecordingConsumer(echoReceivedData: true);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.BufferSize = 512;
                settings.MaxQueuedSendBytes = 4 * 1024 * 1024;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);

            byte[] payload = CreatePayload(1024 * 1024, 11);
            byte[] echoed = await host.RoundTripAsync(client, payload, LongTimeout);

            Assert.Equal(payload, echoed);
            await consumer.WaitForTotalReceivedBytesAsync(payload.Length, MediumTimeout);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }

        await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);
    }

    /// <summary>
    /// Verifies the server can accept and round-trip a larger simultaneous client set.
    /// </summary>
    [Fact]
    public async Task ManyClients_CanConnectAndExchangeDataSimultaneously()
    {
        RecordingConsumer consumer = new RecordingConsumer(echoReceivedData: true);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 32;
                settings.OrderingLaneCount = 4;
                settings.ConcurrentAccepts = 8;
                settings.BufferSize = 512;
            }
        );

        TcpClient[] clients = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => host.ConnectClientAsync())
        );

        try
        {
            await consumer.WaitForConnectedCountAsync(clients.Length, MediumTimeout);

            Task[] roundTrips = Enumerable.Range(0, clients.Length)
                .Select(index => AssertRoundTripAsync(
                    host,
                    clients[index],
                    CreatePayload(128 + index, index + 1)
                ))
                .ToArray();

            await Task.WhenAll(roundTrips);

            Assert.Equal(32, consumer.ConnectedCount);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(clients.Length, MediumTimeout);
    }

    /// <summary>
    /// Verifies receive callbacks can run concurrently under multi-client parallel writes and still account for every byte.
    /// </summary>
    [Fact]
    public async Task ConcurrentReceives_AccountForEveryByteUnderParallelLoad()
    {
        RecordingConsumer consumer = new RecordingConsumer(receiveDelayMs: 5);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 12;
                settings.OrderingLaneCount = 4;
                settings.ConcurrentAccepts = 6;
                settings.BufferSize = 256;
            }
        );

        TcpClient[] clients = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => host.ConnectClientAsync())
        );

        byte[][] payloads = Enumerable.Range(0, clients.Length)
            .Select(index => CreatePayload(64 * 1024, 100 + index))
            .ToArray();

        long expectedTotalBytes = payloads.Sum(payload => (long)payload.Length);

        try
        {
            await consumer.WaitForConnectedCountAsync(clients.Length, MediumTimeout);

            Task[] writers = Enumerable.Range(0, clients.Length)
                .Select(index => host.WriteAsync(clients[index], payloads[index], LongTimeout))
                .ToArray();

            await Task.WhenAll(writers);
            await consumer.WaitForTotalReceivedBytesAsync(expectedTotalBytes, LongTimeout);

            Assert.True(consumer.MaxConcurrentReceiveCallbacks > 1);
            Assert.Equal(expectedTotalBytes, consumer.TotalReceivedBytes);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(clients.Length, MediumTimeout);
    }

    /// <summary>
    /// Verifies stop blocks until an in-flight receive callback has completed.
    /// </summary>
    [Fact]
    public async Task Stop_WaitsForInFlightReceiveCallbackToFinish()
    {
        RecordingConsumer consumer = new RecordingConsumer(blockFirstReceive: true);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 256;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);
            await host.WriteAsync(client, CreatePayload(128, 600), MediumTimeout);
            await consumer.WaitForBlockedReceiveAsync(ShortTimeout);

            Task stopTask = Task.Run(() => host.Server.Stop());

            await Task.Delay(200);
            Assert.False(stopTask.IsCompleted);

            consumer.ReleaseBlockedReceive();

            await stopTask.WaitAsync(MediumTimeout);
            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies repeated connect-send-disconnect waves recycle pooled client slots without losing callbacks.
    /// </summary>
    [Fact]
    public async Task HighClientChurn_RecyclesConnectionsAcrossWaves()
    {
        RecordingConsumer consumer = new RecordingConsumer();

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 6;
                settings.OrderingLaneCount = 3;
                settings.ConcurrentAccepts = 3;
                settings.BufferSize = 256;
            }
        );

        const int clientsPerWave = 6;
        const int waveCount = 15;
        const int payloadSize = 64;

        for (int wave = 0; wave < waveCount; wave++)
        {
            TcpClient[] waveClients = await Task.WhenAll(
                Enumerable.Range(0, clientsPerWave)
                    .Select(_ => host.ConnectClientAsync())
            );

            try
            {
                int expectedConnections = (wave + 1) * clientsPerWave;
                int expectedBytes = (wave + 1) * clientsPerWave * payloadSize;

                await consumer.WaitForConnectedCountAsync(expectedConnections, MediumTimeout);

                Task[] writes = Enumerable.Range(0, waveClients.Length)
                    .Select(index => host.WriteAsync(
                        waveClients[index],
                        CreatePayload(payloadSize, wave * clientsPerWave + index),
                        MediumTimeout
                    ))
                    .ToArray();

                await Task.WhenAll(writes);
                await consumer.WaitForTotalReceivedBytesAsync(expectedBytes, MediumTimeout);
            }
            finally
            {
                DisposeClients(host, waveClients);
            }

            int expectedDisconnections = (wave + 1) * clientsPerWave;
            await consumer.WaitForDisconnectedCountAsync(expectedDisconnections, MediumTimeout);
        }

        Assert.Equal(clientsPerWave * waveCount, consumer.ConnectedCount);
        Assert.Equal(clientsPerWave * waveCount, consumer.DisconnectedCount);
        Assert.Empty(consumer.Errors);
    }

    /// <summary>
    /// Verifies dispose can join an already-running stop without tearing down pooled resources early.
    /// </summary>
    [Fact]
    public async Task Dispose_CanJoinConcurrentStopDuringBusyReceive()
    {
        RecordingConsumer consumer = new RecordingConsumer(blockFirstReceive: true);

        using ServerTestHost host = new ServerTestHost(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 256;
            }
        );

        TcpClient client = await host.ConnectClientAsync();

        try
        {
            await consumer.WaitForConnectedCountAsync(1, ShortTimeout);
            await host.WriteAsync(client, CreatePayload(128, 601), MediumTimeout);
            await consumer.WaitForBlockedReceiveAsync(ShortTimeout);

            Task stopTask = Task.Run(() => host.Server.Stop());
            await Task.Delay(50);
            Task disposeTask = Task.Run(() => host.Server.Dispose());

            await Task.Delay(200);
            Assert.False(stopTask.IsCompleted);
            Assert.False(disposeTask.IsCompleted);

            consumer.ReleaseBlockedReceive();

            await stopTask.WaitAsync(MediumTimeout);
            await disposeTask.WaitAsync(MediumTimeout);
            await consumer.WaitForDisconnectedCountAsync(1, MediumTimeout);

            Assert.True(stopTask.IsCompletedSuccessfully);
            Assert.True(disposeTask.IsCompletedSuccessfully);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            host.DisposeClient(client);
        }
    }

    /// <summary>
    /// Verifies the server sustains repeated parallel round-trips across multiple clients under mixed payload sizes.
    /// </summary>
    [Fact]
    public async Task StressLoad_SustainsRepeatedParallelRoundTrips()
    {
        RecordingConsumer consumer = new RecordingConsumer(echoReceivedData: true, receiveDelayMs: 1);

        using ServerTestHost host = new ServerTestHost(
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
            Enumerable.Range(0, 8)
                .Select(_ => host.ConnectClientAsync())
        );

        try
        {
            await consumer.WaitForConnectedCountAsync(clients.Length, MediumTimeout);

            Task[] workloads = Enumerable.Range(0, clients.Length)
                .Select(clientIndex => RunStressClientAsync(host, clients[clientIndex], clientIndex))
                .ToArray();

            await Task.WhenAll(workloads);

            Assert.True(consumer.TotalReceivedBytes > 0);
            Assert.True(consumer.MaxConcurrentReceiveCallbacks > 1);
            Assert.Empty(consumer.Errors);
        }
        finally
        {
            DisposeClients(host, clients);
        }

        await consumer.WaitForDisconnectedCountAsync(clients.Length, MediumTimeout);
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)((index * 31) + (seed * 17)));
        }

        return payload;
    }

    private static void DisposeClients(ServerTestHost host, IEnumerable<TcpClient> clients)
    {
        foreach (TcpClient client in clients)
        {
            host.DisposeClient(client);
        }
    }

    private static async Task AssertRoundTripAsync(
        ServerTestHost host,
        TcpClient client,
        byte[] payload
    )
    {
        byte[] echoed = await host.RoundTripAsync(client, payload, MediumTimeout);
        Assert.Equal(payload, echoed);
    }

    private static async Task RunStressClientAsync(
        ServerTestHost host,
        TcpClient client,
        int clientIndex
    )
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            int payloadSize = 64 + ((clientIndex + 3) * (iteration + 5) * 37 % 2048);
            byte[] payload = CreatePayload(payloadSize, (clientIndex * 100) + iteration);
            await AssertRoundTripAsync(host, client, payload);
        }
    }
}
