using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Arrowgene.Networking.Tcp.Server.SAEAServer;

namespace Arrowgene.Networking.Test;

public class SaeaServerTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Accepts_Client_And_Echoes_Payload()
    {
        EchoHandler handler = new EchoHandler();
        SaeaServerOptions options = new SaeaServerOptions
        {
            MaxConnections = 2,
            BufferSize = 64
        };

        await RunWithServerAsync(
            handler,
            options,
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(() => handler.ConnectedCount == 1, DefaultTimeout, "Expected one connected client.");

                byte[] payload = Encoding.ASCII.GetBytes("ping");
                await SendAllAsync(client, payload, DefaultTimeout);
                byte[] echoedPayload = await ReceiveExactlyAsync(client, payload.Length, DefaultTimeout);

                Assert.Equal(payload, echoedPayload);
            });
    }

    [Fact]
    public async Task Receive_Callback_Uses_Isolated_Copy_For_Each_Notification()
    {
        ReceiveIsolationHandler handler = new ReceiveIsolationHandler();
        SaeaServerOptions options = new SaeaServerOptions
        {
            MaxConnections = 1,
            BufferSize = 128
        };

        byte[] firstPayload = Encoding.ASCII.GetBytes("alpha");
        byte[] secondPayload = Encoding.ASCII.GetBytes("bravo");

        await RunWithServerAsync(
            handler,
            options,
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);

                await SendAllAsync(client, firstPayload, DefaultTimeout);
                await WaitUntilAsync(() => handler.ReceiveCount >= 1, DefaultTimeout, "Expected first receive callback.");

                await SendAllAsync(client, secondPayload, DefaultTimeout);
                await WaitUntilAsync(() => handler.ReceiveCount >= 2, DefaultTimeout, "Expected second receive callback.");

                Assert.NotNull(handler.FirstBuffer);
                Assert.NotNull(handler.SecondBuffer);
                Assert.Equal(firstPayload, handler.FirstBuffer);
                Assert.Equal(secondPayload, handler.SecondBuffer);
                Assert.False(ReferenceEquals(handler.FirstBuffer, handler.SecondBuffer), "Each callback should get its own buffer instance.");
            });
    }

    [Fact]
    public async Task Send_Copies_Buffer_Before_Transmission()
    {
        SendIsolationHandler handler = new SendIsolationHandler();
        SaeaServerOptions options = new SaeaServerOptions
        {
            MaxConnections = 1,
            BufferSize = 64
        };

        await RunWithServerAsync(
            handler,
            options,
            async port =>
            {
                using Socket client = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(() => handler.ConnectedCount == 1, DefaultTimeout, "Expected one connected client.");

                byte[] request = Encoding.ASCII.GetBytes("go");
                await SendAllAsync(client, request, DefaultTimeout);
                byte[] response = await ReceiveExactlyAsync(client, 5, DefaultTimeout);

                Assert.Equal("hello", Encoding.ASCII.GetString(response));
            });
    }

    [Fact]
    public async Task Recycles_Session_After_Client_Disconnect()
    {
        TrackingHandler handler = new TrackingHandler();
        SaeaServerOptions options = new SaeaServerOptions
        {
            MaxConnections = 1,
            BufferSize = 64
        };

        await RunWithServerAsync(
            handler,
            options,
            async port =>
            {
                using Socket client1 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(() => handler.ConnectedCount == 1, DefaultTimeout, "Expected first client to connect.");

                client1.Shutdown(SocketShutdown.Both);
                client1.Close();

                await WaitUntilAsync(() => handler.DisconnectedCount == 1, DefaultTimeout, "Expected first client disconnect.");

                using Socket client2 = await ConnectClientAsync(port, DefaultTimeout);
                await WaitUntilAsync(() => handler.ConnectedCount == 2, DefaultTimeout, "Expected recycled session to accept a second client.");
            });
    }

    private static async Task RunWithServerAsync(
        ISaeaConnectionHandler handler,
        SaeaServerOptions options,
        Func<int, Task> assertion)
    {
        int port = GetFreeTcpPort();
        using SaeaServer server = new SaeaServer(IPAddress.Loopback, (ushort)port, handler, options);
        server.Start();

        try
        {
            await assertion(port);
        }
        finally
        {
            server.Stop();
        }
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<Socket> ConnectClientAsync(int port, TimeSpan timeout)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port).WaitAsync(timeout);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task SendAllAsync(Socket socket, byte[] data, TimeSpan timeout)
    {
        int sentBytes = 0;
        while (sentBytes < data.Length)
        {
            int bytesSent = await socket.SendAsync(data.AsMemory(sentBytes), SocketFlags.None).AsTask().WaitAsync(timeout);
            if (bytesSent <= 0)
            {
                throw new IOException("Socket send returned zero bytes.");
            }

            sentBytes += bytesSent;
        }
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int expectedLength, TimeSpan timeout)
    {
        byte[] buffer = new byte[expectedLength];
        int receivedBytes = 0;

        while (receivedBytes < expectedLength)
        {
            int bytesRead = await socket.ReceiveAsync(buffer.AsMemory(receivedBytes), SocketFlags.None).AsTask().WaitAsync(timeout);
            if (bytesRead <= 0)
            {
                throw new IOException("Socket closed before enough data was received.");
            }

            receivedBytes += bytesRead;
        }

        return buffer;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), failureMessage);
    }

    private sealed class EchoHandler : ISaeaConnectionHandler
    {
        private int _connectedCount;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);

        public void OnServerStarted()
        {
        }

        public void OnServerStopped()
        {
        }

        public void OnConnected(ISaeaSession session)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnReceived(ISaeaSession session, byte[] data)
        {
            session.Send(data);
        }

        public void OnDisconnected(ISaeaSession session, SocketError socketError)
        {
        }
    }

    private sealed class ReceiveIsolationHandler : ISaeaConnectionHandler
    {
        private int _receiveCount;

        public int ReceiveCount => Volatile.Read(ref _receiveCount);

        public byte[]? FirstBuffer { get; private set; }

        public byte[]? SecondBuffer { get; private set; }

        public void OnServerStarted()
        {
        }

        public void OnServerStopped()
        {
        }

        public void OnConnected(ISaeaSession session)
        {
        }

        public void OnReceived(ISaeaSession session, byte[] data)
        {
            int receiveCount = Interlocked.Increment(ref _receiveCount);
            if (receiveCount == 1)
            {
                FirstBuffer = data;
                return;
            }

            if (receiveCount == 2)
            {
                SecondBuffer = data;
            }
        }

        public void OnDisconnected(ISaeaSession session, SocketError socketError)
        {
        }
    }

    private sealed class SendIsolationHandler : ISaeaConnectionHandler
    {
        private int _connectedCount;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);

        public void OnServerStarted()
        {
        }

        public void OnServerStopped()
        {
        }

        public void OnConnected(ISaeaSession session)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnReceived(ISaeaSession session, byte[] data)
        {
            byte[] payload = Encoding.ASCII.GetBytes("hello");
            session.Send(payload);
            payload[0] = (byte)'j';
        }

        public void OnDisconnected(ISaeaSession session, SocketError socketError)
        {
        }
    }

    private sealed class TrackingHandler : ISaeaConnectionHandler
    {
        private int _connectedCount;
        private int _disconnectedCount;

        public int ConnectedCount => Volatile.Read(ref _connectedCount);

        public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);

        public void OnServerStarted()
        {
        }

        public void OnServerStopped()
        {
        }

        public void OnConnected(ISaeaSession session)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        public void OnReceived(ISaeaSession session, byte[] data)
        {
        }

        public void OnDisconnected(ISaeaSession session, SocketError socketError)
        {
            Interlocked.Increment(ref _disconnectedCount);
        }
    }
}
