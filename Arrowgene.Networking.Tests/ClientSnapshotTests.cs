using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Verifies immutable snapshot behavior.
/// </summary>
public sealed class ClientSnapshotTests
{
    /// <summary>
    /// Verifies the packed unique identifier is captured from the constructor inputs.
    /// </summary>
    [Fact]
    public void Constructor_CapturesUniqueId()
    {
        const ushort clientId = 321;
        const uint generation = 654u;

        ClientSnapshot snapshot = new ClientSnapshot(
            clientId,
            generation,
            "[127.0.0.1:1234]",
            IPAddress.Loopback,
            1234,
            true,
            DateTime.UtcNow,
            10,
            20,
            30,
            40,
            0,
            0,
            1
        );

        Assert.Equal(UniqueIdManager.Pack(clientId, generation), snapshot.UniqueId);
    }

    /// <summary>
    /// Verifies snapshot properties cannot be reassigned after construction.
    /// </summary>
    [Fact]
    public void PublicProperties_DoNotExposeSetters()
    {
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.ClientId));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Generation));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Identity));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.RemoteIpAddress));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Port));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.IsAlive));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.ConnectedAt));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.LastReadMs));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.LastWriteMs));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.BytesReceived));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.BytesSent));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.PendingOperations));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.SendQueuedBytes));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.UnitOfOrder));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.UniqueId));
    }

    /// <summary>
    /// Verifies snapshots include the current queued outbound bytes and return to zero after the queue drains.
    /// </summary>
    [Fact]
    public void Snapshot_SendQueuedBytesReflectsQueueState()
    {
        Client client = CreateActivatedClient(
            out ClientHandle clientHandle,
            out Socket peerSocket,
            out TcpListener listener
        );

        try
        {
            byte[] payload = new byte[128];

            bool enqueued = client.QueueSend(clientHandle.Generation, payload, out bool startSend, out bool queueOverflow);
            Assert.True(enqueued);
            Assert.True(startSend);
            Assert.False(queueOverflow);

            ClientSnapshot queuedSnapshot = client.Snapshot();
            Assert.Equal(payload.Length, queuedSnapshot.SendQueuedBytes);

            bool prepared = client.TryPrepareSendChunk(clientHandle.Generation, payload.Length, out int chunkSize);
            Assert.True(prepared);
            Assert.Equal(payload.Length, chunkSize);

            bool continueSending = client.CompleteSend(chunkSize);
            Assert.False(continueSending);

            ClientSnapshot drainedSnapshot = client.Snapshot();
            Assert.Equal(0, drainedSnapshot.SendQueuedBytes);
        }
        finally
        {
            client.Dispose();
            peerSocket.Dispose();
            listener.Stop();
        }
    }

    private static Client CreateActivatedClient(
        out ClientHandle clientHandle,
        out Socket peerSocket,
        out TcpListener listener)
    {
        TcpServer tcpServer = (TcpServer)RuntimeHelpers.GetUninitializedObject(typeof(TcpServer));
        SocketAsyncEventArgs receiveEventArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();
        sendEventArgs.SetBuffer(new byte[256], 0, 256);

        Client client = new Client(tcpServer, 1, receiveEventArgs, sendEventArgs, 1024);

        listener = new TcpListener(IPAddress.Loopback, PortAllocator.GetFreeTcpPort());
        listener.Start();

        peerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        peerSocket.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

        Socket serverSocket = listener.AcceptSocket();
        client.Activate(serverSocket, 0, out clientHandle);

        return client;
    }

    private static void AssertPropertyIsReadOnly(string propertyName)
    {
        PropertyInfo property = typeof(ClientSnapshot).GetProperty(propertyName)!;
        Assert.Null(property.SetMethod);
    }
}
