using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;
using Arrowgene.Networking.SAEAServer.Metric;

namespace Arrowgene.Networking.Tests;

internal sealed class ServerTestHost : IDisposable
{
    private readonly object _sync;
    private readonly List<TcpClient> _trackedClients;
    private bool _disposed;

    internal ServerTestHost(
        IConsumer consumer,
        Action<TcpServerSettings>? configureSettings = null
    )
    {
        Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));

        TcpServerSettings settings = new TcpServerSettings
        {
            Identity = "Tests",
            MaxConnections = 16,
            BufferSize = 1024,
            OrderingLaneCount = 4,
            ConcurrentAccepts = 4,
            MaxQueuedSendBytes = 8 * 1024 * 1024,
            ListenSocketRetries = 0,
            ClientSocketTimeoutSeconds = -1,
        };

        settings.ListenSocketSettings.Backlog = 128;
        configureSettings?.Invoke(settings);

        _sync = new object();
        _trackedClients = new List<TcpClient>();
        Port = PortAllocator.GetFreeTcpPort();
        TcpServer = new TcpServer(IPAddress.Loopback, Port, Consumer, settings);
        MetricsCollector = new TcpServerMetricsCollector(TcpServer);
        TcpServer.Start();
        MetricsCollector.Start("TestMetrics");
    }

    internal IConsumer Consumer { get; }

    internal TcpServer TcpServer { get; }

    internal TcpServerMetricsCollector MetricsCollector { get; }

    internal ushort Port { get; }

    internal async Task<TcpClient> ConnectClientAsync(TimeSpan? timeout = null)
    {
        ThrowIfDisposed();

        TimeSpan connectTimeout = timeout ?? TimeSpan.FromSeconds(5);
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (true)
        {
            TcpClient client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true
            };

            try
            {
                using CancellationTokenSource cancellation = new CancellationTokenSource(connectTimeout);
                await client.ConnectAsync(IPAddress.Loopback, Port, cancellation.Token).ConfigureAwait(false);

                lock (_sync)
                {
                    _trackedClients.Add(client);
                }

                return client;
            }
            catch (SocketException) when (stopwatch.Elapsed < connectTimeout)
            {
                client.Dispose();
                await Task.Delay(25).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopwatch.Elapsed < connectTimeout)
            {
                client.Dispose();
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
    }

    internal async Task WriteAsync(
        TcpClient client,
        byte[] payload,
        TimeSpan? timeout = null
    )
    {
        using CancellationTokenSource cancellation = CreateCancellation(timeout);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(payload, cancellation.Token).ConfigureAwait(false);
    }

    internal async Task<byte[]> ReadExactAsync(
        TcpClient client,
        int length,
        TimeSpan? timeout = null
    )
    {
        using CancellationTokenSource cancellation = CreateCancellation(timeout);
        byte[] buffer = new byte[length];
        NetworkStream stream = client.GetStream();
        await stream.ReadExactlyAsync(buffer, cancellation.Token).ConfigureAwait(false);
        return buffer;
    }

    internal async Task<byte[]> RoundTripAsync(
        TcpClient client,
        byte[] payload,
        TimeSpan? timeout = null
    )
    {
        using CancellationTokenSource cancellation = CreateCancellation(timeout);
        byte[] response = new byte[payload.Length];
        NetworkStream stream = client.GetStream();

        Task writeTask = stream.WriteAsync(payload, cancellation.Token).AsTask();
        Task readTask = stream.ReadExactlyAsync(response, cancellation.Token).AsTask();

        await Task.WhenAll(writeTask, readTask).ConfigureAwait(false);
        return response;
    }

    internal void DisposeClient(TcpClient client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            client.Dispose();
        }
        catch
        {
        }

        lock (_sync)
        {
            _trackedClients.Remove(client);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        List<TcpClient> clients;
        lock (_sync)
        {
            clients = new List<TcpClient>(_trackedClients);
            _trackedClients.Clear();
        }

        foreach (TcpClient client in clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }

        Thread.Sleep(150);

        try
        {
            TcpServer.Stop();
        }
        catch
        {
        }

        Thread.Sleep(100);
        MetricsCollector.Dispose();
        TcpServer.Dispose();
        _disposed = true;
    }

    private static CancellationTokenSource CreateCancellation(TimeSpan? timeout)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        return new CancellationTokenSource(effectiveTimeout);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ServerTestHost));
        }
    }
}
