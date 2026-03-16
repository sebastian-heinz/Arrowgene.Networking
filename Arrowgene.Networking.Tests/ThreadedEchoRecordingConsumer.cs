using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.Tests;

internal sealed class ThreadedEchoRecordingConsumer : ThreadedBlockingQueueConsumer
{
    private readonly object _sync;
    private readonly List<ConnectedClientRecord> _connectedClients;
    private readonly List<DisconnectedClientRecord> _disconnectedClients;
    private readonly List<Exception> _errors;
    private readonly bool _echoReceivedData;
    private readonly int _receiveDelayMs;
    private long _totalReceivedBytes;
    private int _activeHandlers;
    private int _maxConcurrentHandlers;

    internal ThreadedEchoRecordingConsumer(
        int orderingLaneCount,
        bool echoReceivedData = false,
        int queueCapacityPerLane = 1024,
        int receiveDelayMs = 0
    )
        : base(orderingLaneCount, queueCapacityPerLane, nameof(ThreadedEchoRecordingConsumer))
    {
        _sync = new object();
        _connectedClients = new List<ConnectedClientRecord>();
        _disconnectedClients = new List<DisconnectedClientRecord>();
        _errors = new List<Exception>();
        _echoReceivedData = echoReceivedData;
        _receiveDelayMs = receiveDelayMs;
    }

    internal int ConnectedCount
    {
        get
        {
            lock (_sync)
            {
                return _connectedClients.Count;
            }
        }
    }

    internal int DisconnectedCount
    {
        get
        {
            lock (_sync)
            {
                return _disconnectedClients.Count;
            }
        }
    }

    internal long TotalReceivedBytes => Interlocked.Read(ref _totalReceivedBytes);

    internal int MaxConcurrentHandlers => Volatile.Read(ref _maxConcurrentHandlers);

    internal IReadOnlyList<ConnectedClientRecord> ConnectedClients
    {
        get
        {
            lock (_sync)
            {
                return _connectedClients.ToArray();
            }
        }
    }

    internal IReadOnlyList<DisconnectedClientRecord> DisconnectedClients
    {
        get
        {
            lock (_sync)
            {
                return _disconnectedClients.ToArray();
            }
        }
    }

    internal IReadOnlyList<Exception> Errors
    {
        get
        {
            lock (_sync)
            {
                return _errors.ToArray();
            }
        }
    }

    protected override void HandleReceived(ClientHandle clientHandle, byte[] data)
    {
        int activeHandlers = Interlocked.Increment(ref _activeHandlers);
        UpdateMaxConcurrentHandlers(activeHandlers);

        try
        {
            if (_receiveDelayMs > 0)
            {
                Thread.Sleep(_receiveDelayMs);
            }

            Interlocked.Add(ref _totalReceivedBytes, data.Length);

            if (_echoReceivedData)
            {
                clientHandle.Send(data);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeHandlers);
        }
    }

    protected override void HandleDisconnected(ClientSnapshot clientSnapshot)
    {
        lock (_sync)
        {
            _disconnectedClients.Add(new DisconnectedClientRecord(clientSnapshot));
        }
    }

    protected override void HandleConnected(ClientHandle clientHandle)
    {
        ClientKey key = new ClientKey(clientHandle.Port, clientHandle.Generation);
        ConnectedClientRecord record = new ConnectedClientRecord(clientHandle, key, clientHandle.UnitOfOrder);

        lock (_sync)
        {
            _connectedClients.Add(record);
        }
    }

    protected override void HandleError(ClientHandle clientHandle, Exception exception, string message)
    {
        lock (_sync)
        {
            _errors.Add(exception);
        }
    }

    internal ConnectedClientRecord GetConnectedClient(int index)
    {
        lock (_sync)
        {
            return _connectedClients[index];
        }
    }

    internal DisconnectedClientRecord GetDisconnectedClient(int index)
    {
        lock (_sync)
        {
            return _disconnectedClients[index];
        }
    }

    internal async Task WaitForConnectedCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => ConnectedCount >= expected,
            timeout,
            $"Timed out waiting for {expected} connected clients. Current count: {ConnectedCount}."
        ).ConfigureAwait(false);
    }

    internal async Task WaitForDisconnectedCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => DisconnectedCount >= expected,
            timeout,
            $"Timed out waiting for {expected} disconnected clients. Current count: {DisconnectedCount}."
        ).ConfigureAwait(false);
    }

    private void UpdateMaxConcurrentHandlers(int activeHandlers)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxConcurrentHandlers);
            if (activeHandlers <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(
                   ref _maxConcurrentHandlers,
                   activeHandlers,
                   observed
               ) != observed);
    }
}
