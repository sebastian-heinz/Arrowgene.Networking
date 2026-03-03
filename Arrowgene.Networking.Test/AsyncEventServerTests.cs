using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Arrowgene.Networking.Tcp;
using Arrowgene.Networking.Tcp.Consumer;
using Arrowgene.Networking.Tcp.Server.AsyncEvent;

namespace Arrowgene.Networking.Test;

public class AsyncEventServerTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Accepts_New_Client_Connection_Successfully()
    {
        var consumer = new TrackingConsumer();
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 4;
                settings.BufferSize = 128;
            },
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected one connected client."
                );

                Assert.Equal(1, consumer.ConnectedCount);
                Assert.Equal(0, consumer.DisconnectedCount);
            });
    }

    [Fact]
    public async Task Rejects_Client_When_Max_Connection_Limit_Is_Reached()
    {
        var consumer = new TrackingConsumer(echoReceivedData: true);
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 64;
            },
            async port =>
            {
                using Socket client1 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected first client to connect."
                );

                byte[] ping = Encoding.ASCII.GetBytes("ok");
                await SendAllAsync(client1, ping);
                byte[] echoedPing = await ReceiveExactlyAsync(client1, ping.Length, DefaultTimeout);
                Assert.Equal(ping, echoedPing);

                using Socket client2 = await ConnectClientAsync(port, DefaultTimeout);
                bool secondClientClosed = await WaitForRemoteCloseAsync(client2, TimeSpan.FromSeconds(3));

                Assert.True(secondClientClosed, "Second client should be closed when max connections is reached.");
                Assert.Equal(1, consumer.ConnectedCount);
            });
    }

    [Fact]
    public async Task Recycles_Client_Resources_After_Graceful_Disconnect()
    {
        var consumer = new TrackingConsumer();
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 64;
            },
            async port =>
            {
                using Socket client1 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected first client to connect."
                );

                client1.Shutdown(SocketShutdown.Both);
                client1.Close();

                await WaitUntilAsync(
                    () => consumer.DisconnectedCount >= 1,
                    DefaultTimeout,
                    "Expected graceful disconnect callback."
                );

                using Socket client2 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 2,
                    DefaultTimeout,
                    "Expected server to recycle client slot after disconnect."
                );

                Assert.True(consumer.ConnectedCount >= 2);
            });
    }

    [Fact]
    public async Task Recycles_Client_Resources_After_Abortive_Disconnect()
    {
        var consumer = new TrackingConsumer();
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 64;
            },
            async port =>
            {
                using Socket client1 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected first client to connect."
                );

                client1.LingerState = new LingerOption(enable: true, seconds: 0);
                client1.Close();

                await WaitUntilAsync(
                    () => consumer.DisconnectedCount >= 1,
                    DefaultTimeout,
                    "Expected disconnect callback after abortive close."
                );

                using Socket client2 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 2,
                    DefaultTimeout,
                    "Expected server to accept a new client after abortive disconnect."
                );

                Assert.True(consumer.ConnectedCount >= 2);
            });
    }

    [Fact]
    public async Task Processes_Incomplete_Message_Spanning_Multiple_Receives()
    {
        var consumer = new LengthPrefixedEchoConsumer(maxPayloadBytes: 256);
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 16;
            },
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected client to connect."
                );

                byte[] payload = Encoding.UTF8.GetBytes("fragmented-message");
                byte[] frame = BuildFrame(payload);
                int splitIndex = frame.Length - 2;

                await SendAllAsync(client, frame[..splitIndex]);
                await Task.Delay(100);
                Assert.Equal(0, consumer.DispatchedCount);

                await SendAllAsync(client, frame[splitIndex..]);
                await WaitUntilAsync(
                    () => consumer.DispatchedCount == 1,
                    DefaultTimeout,
                    "Expected fragmented payload to be reconstructed."
                );

                byte[] echoedPayload = await ReceiveFramePayloadAsync(client, DefaultTimeout);
                Assert.Equal(payload, echoedPayload);
            });
    }

    [Fact]
    public async Task Handles_Corrupted_Data_And_Continues_With_New_Clients()
    {
        var consumer = new LengthPrefixedEchoConsumer(maxPayloadBytes: 64);
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.BufferSize = 32;
            },
            async port =>
            {
                using Socket invalidClient = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected invalid client to connect."
                );

                byte[] invalidHeader = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(invalidHeader, 4096);
                await SendAllAsync(invalidClient, invalidHeader);

                await WaitUntilAsync(
                    () => consumer.InvalidPayloadCount == 1,
                    DefaultTimeout,
                    "Expected invalid payload to be detected."
                );

                bool invalidClientClosed = await WaitForRemoteCloseAsync(invalidClient, DefaultTimeout);
                Assert.True(invalidClientClosed, "Invalid client connection should be closed.");

                using Socket validClient = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 2,
                    DefaultTimeout,
                    "Expected server to continue accepting new clients after corrupted data."
                );

                byte[] validPayload = Encoding.ASCII.GetBytes("ok");
                await SendAllAsync(validClient, BuildFrame(validPayload));
                byte[] echoedPayload = await ReceiveFramePayloadAsync(validClient, DefaultTimeout);

                Assert.Equal(validPayload, echoedPayload);
            });
    }

    [Fact]
    public async Task Buffers_And_Sends_Back_Large_Payload()
    {
        var consumer = new LengthPrefixedEchoConsumer(maxPayloadBytes: 2048);
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 32;
            },
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected client to connect."
                );

                byte[] payload = CreatePayload(512);
                await SendAllAsync(client, BuildFrame(payload));

                byte[] echoedPayload = await ReceiveFramePayloadAsync(client, DefaultTimeout);
                Assert.Equal(payload, echoedPayload);
            });
    }

    [Fact]
    public async Task Handles_Sudden_Client_Drops_Without_Corrupting_State()
    {
        var consumer = new TrackingConsumer(echoReceivedData: true);
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 8;
                settings.BufferSize = 64;
            },
            async port =>
            {
                var droppedClients = new List<Socket>();
                for (int i = 0; i < 5; i++)
                {
                    droppedClients.Add(await ConnectClientAsync(port, DefaultTimeout));
                }

                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 5,
                    DefaultTimeout,
                    "Expected all initial clients to connect."
                );

                foreach (Socket socket in droppedClients)
                {
                    socket.LingerState = new LingerOption(enable: true, seconds: 0);
                    socket.Close();
                }

                await WaitUntilAsync(
                    () => consumer.DisconnectedCount >= 5,
                    DefaultTimeout,
                    "Expected all dropped clients to disconnect."
                );

                using Socket recoveryClient = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 6,
                    DefaultTimeout,
                    "Expected server to remain operational after sudden drops."
                );

                byte[] ping = Encoding.ASCII.GetBytes("alive");
                await SendAllAsync(recoveryClient, ping);
                byte[] echo = await ReceiveExactlyAsync(recoveryClient, ping.Length, DefaultTimeout);
                Assert.Equal(ping, echo);
            });
    }

    [Fact]
    public async Task Disconnects_Idle_Client_According_To_Configured_SocketTimeout()
    {
        var consumer = new TrackingConsumer();
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 1;
                settings.BufferSize = 64;
                settings.SocketTimeoutSeconds = 1;
            },
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected client to connect."
                );

                await WaitUntilAsync(
                    () => consumer.DisconnectedCount >= 1,
                    TimeSpan.FromSeconds(6),
                    "Expected idle client to be disconnected by socket timeout."
                );

                bool closed = await WaitForRemoteCloseAsync(client, TimeSpan.FromSeconds(2));
                Assert.True(closed, "Client socket should be closed by server timeout handling.");
            });
    }

    [Fact]
    public async Task Swallows_Consumer_OnReceivedData_Exception_And_Continues_Processing()
    {
        var consumer = new ThrowOnceEchoConsumer();
        await RunWithServerAsync(
            consumer,
            settings =>
            {
                settings.MaxConnections = 2;
                settings.BufferSize = 64;
            },
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount == 1,
                    DefaultTimeout,
                    "Expected first client to connect."
                );

                byte[] first = Encoding.ASCII.GetBytes("x");
                await SendAllAsync(client, first);
                await WaitUntilAsync(
                    () => consumer.ReceiveCallCount >= 1,
                    DefaultTimeout,
                    "Expected first receive callback to occur."
                );

                byte[] second = Encoding.ASCII.GetBytes("after-error");
                await SendAllAsync(client, second);
                byte[] echoedSecond = await ReceiveExactlyAsync(client, second.Length, DefaultTimeout);
                Assert.Equal(second, echoedSecond);

                using Socket secondClient = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= 2,
                    DefaultTimeout,
                    "Expected server to keep accepting clients after consumer exception."
                );
            });
    }

    [Fact]
    public async Task Can_Start_And_Stop_Server_Multiple_Times_Without_Corrupting_State()
    {
        const int cycles = 5;
        int port = GetFreePort();
        var consumer = new TrackingConsumer(echoReceivedData: true);
        var settings = new AsyncEventSettings
        {
            BufferSize = 64,
            MaxConnections = 8,
            Retries = 0,
            MaxUnitOfOrder = 1,
            SocketTimeoutSeconds = -1
        };
        settings.SocketSettings.Backlog = 64;

        using var server = new AsyncEventServer(IPAddress.Loopback, checked((ushort)port), consumer, settings);
        try
        {
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                server.Start();

                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(
                    () => consumer.ConnectedCount >= cycle + 1,
                    DefaultTimeout,
                    $"Expected client to connect in cycle {cycle}."
                );

                byte[] payload = Encoding.ASCII.GetBytes($"cycle-{cycle}");
                await SendAllAsync(client, payload);
                byte[] echo = await ReceiveExactlyAsync(client, payload.Length, DefaultTimeout);
                Assert.Equal(payload, echo);

                client.Shutdown(SocketShutdown.Both);
                client.Close();

                await WaitUntilAsync(
                    () => consumer.DisconnectedCount >= cycle + 1,
                    DefaultTimeout,
                    $"Expected disconnect callback in cycle {cycle}."
                );

                server.Stop();

                await AssertCannotConnectAsync(port, TimeSpan.FromMilliseconds(300));
            }

            Assert.Equal(cycles, consumer.StartCount);
            Assert.Equal(cycles, consumer.StopCount);
            Assert.Equal(cycles, consumer.ConnectedCount);
            Assert.Equal(cycles, consumer.DisconnectedCount);
            Assert.True(consumer.ReceivedCount >= cycles);
        }
        finally
        {
            try
            {
                server.Stop();
            }
            catch
            {
                // ignored during test teardown
            }
        }
    }

    private static async Task RunWithServerAsync(
        IConsumer consumer,
        Action<AsyncEventSettings>? configureSettings,
        Func<int, Task> testBody)
    {
        int port = GetFreePort();
        var settings = new AsyncEventSettings
        {
            BufferSize = 64,
            MaxConnections = 16,
            Retries = 0,
            MaxUnitOfOrder = 1,
            SocketTimeoutSeconds = -1
        };
        settings.SocketSettings.Backlog = 64;
        configureSettings?.Invoke(settings);

        AsyncEventServer? server = null;
        try
        {
            server = new AsyncEventServer(IPAddress.Loopback, checked((ushort)port), consumer, settings);
            server.Start();
            await testBody(port);
        }
        finally
        {
            if (server != null)
            {
                try
                {
                    server.Stop();
                }
                catch
                {
                    // ignored during test teardown
                }

                server.Dispose();
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<Socket> ConnectClientAsync(int port, TimeSpan timeout)
    {
        Exception? lastException = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                return socket;
            }
            catch (Exception ex)
            {
                lastException = ex;
                socket.Dispose();
                await Task.Delay(20);
            }
        }

        throw new TimeoutException($"Failed to connect to 127.0.0.1:{port} within {timeout}.", lastException);
    }

    private static async Task AssertCannotConnectAsync(int port, TimeSpan timeout)
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => ConnectClientAsync(port, timeout)
        );
    }

    private static async Task SendAllAsync(Socket socket, byte[] data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            sent += await socket.SendAsync(data.AsMemory(sent), SocketFlags.None);
        }
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int length, TimeSpan timeout)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] buffer = new byte[length];
        int received = 0;
        using var cts = new CancellationTokenSource(timeout);
        while (received < length)
        {
            int bytes = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, cts.Token);
            if (bytes == 0)
            {
                throw new IOException("Remote endpoint closed the connection.");
            }

            received += bytes;
        }

        return buffer;
    }

    private static async Task<bool> WaitForRemoteCloseAsync(Socket socket, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (socket.Poll(50_000, SelectMode.SelectRead) && socket.Available == 0)
                {
                    return true;
                }
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    private static async Task<byte[]> ReceiveFramePayloadAsync(Socket socket, TimeSpan timeout)
    {
        byte[] header = await ReceiveExactlyAsync(socket, 4, timeout);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(payloadLength, 0, 1024 * 1024);
        return await ReceiveExactlyAsync(socket, payloadLength, timeout);
    }

    private static byte[] CreatePayload(int size)
    {
        byte[] payload = new byte[size];
        for (int i = 0; i < size; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), message);
    }

    private sealed class TrackingConsumer(bool echoReceivedData = false) : IConsumer
    {
        private readonly bool _echoReceivedData = echoReceivedData;
        private int _connectedCount;
        private int _disconnectedCount;
        private int _receivedCount;
        private int _startCount;
        private int _stopCount;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);
        public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);
        public int ReceivedCount => Volatile.Read(ref _receivedCount);
        public int StartCount => Volatile.Read(ref _startCount);
        public int StopCount => Volatile.Read(ref _stopCount);

        public void OnStart()
        {
            Interlocked.Increment(ref _startCount);
        }

        public void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            Interlocked.Increment(ref _receivedCount);
            if (_echoReceivedData)
            {
                socket.Send(data);
            }
        }

        public void OnClientDisconnected(ITcpSocket socket)
        {
            Interlocked.Increment(ref _disconnectedCount);
        }

        public void OnClientConnected(ITcpSocket socket)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnStop()
        {
            Interlocked.Increment(ref _stopCount);
        }
    }

    private sealed class ThrowOnceEchoConsumer : IConsumer
    {
        private int _connectedCount;
        private int _receiveCallCount;
        private int _thrown;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);
        public int ReceiveCallCount => Volatile.Read(ref _receiveCallCount);

        public void OnStart()
        {
        }

        public void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            Interlocked.Increment(ref _receiveCallCount);
            if (Interlocked.CompareExchange(ref _thrown, 1, 0) == 0)
            {
                throw new InvalidOperationException("Simulated consumer failure.");
            }

            socket.Send(data);
        }

        public void OnClientDisconnected(ITcpSocket socket)
        {
        }

        public void OnClientConnected(ITcpSocket socket)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnStop()
        {
        }
    }

    private sealed class LengthPrefixedEchoConsumer(int maxPayloadBytes) : IConsumer
    {
        private readonly int _maxPayloadBytes = maxPayloadBytes;
        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
        private int _connectedCount;
        private int _disconnectedCount;
        private int _invalidPayloadCount;
        private int _dispatchedCount;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);
        public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);
        public int InvalidPayloadCount => Volatile.Read(ref _invalidPayloadCount);
        public int DispatchedCount => Volatile.Read(ref _dispatchedCount);

        public void OnStart()
        {
        }

        public void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            SessionState state = _sessions.GetOrAdd(socket.Identity, _ => new SessionState());
            lock (state.Sync)
            {
                state.Buffer.AddRange(data);
                while (true)
                {
                    if (state.Buffer.Count < 4)
                    {
                        return;
                    }

                    int payloadLength =
                        state.Buffer[0] |
                        (state.Buffer[1] << 8) |
                        (state.Buffer[2] << 16) |
                        (state.Buffer[3] << 24);
                    if (payloadLength < 0 || payloadLength > _maxPayloadBytes)
                    {
                        Interlocked.Increment(ref _invalidPayloadCount);
                        state.Buffer.Clear();
                        socket.Close();
                        return;
                    }

                    int frameLength = 4 + payloadLength;
                    if (state.Buffer.Count < frameLength)
                    {
                        return;
                    }

                    byte[] payload = state.Buffer.GetRange(4, payloadLength).ToArray();
                    state.Buffer.RemoveRange(0, frameLength);
                    Interlocked.Increment(ref _dispatchedCount);
                    socket.Send(BuildFrame(payload));
                }
            }
        }

        public void OnClientDisconnected(ITcpSocket socket)
        {
            Interlocked.Increment(ref _disconnectedCount);
            _sessions.TryRemove(socket.Identity, out _);
        }

        public void OnClientConnected(ITcpSocket socket)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnStop()
        {
        }

        private sealed class SessionState
        {
            public object Sync { get; } = new object();
            public List<byte> Buffer { get; } = new();
        }
    }
}
