using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
            out ClientRegistry clientRegistry,
            out AcceptPool acceptPool
        );

        try
        {
            collector.Start("MetricsCollectorTests");
            collector.Stop();

            TcpServerMetricsSnapshot firstSnapshot = collector.GetSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot secondSnapshot = collector.GetSnapshot();
            collector.CaptureSnapshot();
            TcpServerMetricsSnapshot thirdSnapshot = collector.GetSnapshot();

            Assert.Equal(1, firstSnapshot.SnapshotSequenceNumber);
            Assert.Equal(2, secondSnapshot.SnapshotSequenceNumber);
            Assert.Equal(3, thirdSnapshot.SnapshotSequenceNumber);

            Assert.Equal(firstSnapshot.ServerStartedAtUtc, secondSnapshot.ServerStartedAtUtc);
            Assert.Equal(firstSnapshot.ServerStartedAtUtc, thirdSnapshot.ServerStartedAtUtc);

            Assert.True(firstSnapshot.TimestampUtc >= firstSnapshot.ServerStartedAtUtc);
            Assert.True(secondSnapshot.TimestampUtc >= secondSnapshot.ServerStartedAtUtc);
            Assert.True(thirdSnapshot.TimestampUtc >= thirdSnapshot.ServerStartedAtUtc);
        }
        finally
        {
            collector.Dispose();
            acceptPool.Dispose();
            clientRegistry.Dispose();
        }
    }

    private static TcpServerMetricsCollector CreateCollector(
        out ClientRegistry clientRegistry,
        out AcceptPool acceptPool)
    {
        TcpServerMetricsState metricsState = new TcpServerMetricsState();
        clientRegistry = new ClientRegistry(1, 1, CreateClient);
        acceptPool = new AcceptPool(1, IgnoreAcceptCompletion);
        return new TcpServerMetricsCollector(metricsState, clientRegistry, acceptPool, null, 1);
    }

    private static Client CreateClient(ushort clientId)
    {
        TcpServer tcpServer = (TcpServer)RuntimeHelpers.GetUninitializedObject(typeof(TcpServer));
        SocketAsyncEventArgs receiveEventArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs sendEventArgs = new SocketAsyncEventArgs();
        return new Client(tcpServer, clientId, receiveEventArgs, sendEventArgs, 1024);
    }

    private static void IgnoreAcceptCompletion(object? sender, SocketAsyncEventArgs eventArgs)
    {
    }
}
