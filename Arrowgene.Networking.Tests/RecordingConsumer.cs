using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;

namespace Arrowgene.Networking.Tests;

internal sealed class RecordingConsumer : IConsumer
{
    private readonly object _sync;
    private readonly List<ConnectedClientRecord> _connectedClients;
    private readonly List<DisconnectedClientRecord> _disconnectedClients;
    private readonly List<Exception> _errors;
    private readonly Dictionary<ClientKey, long> _receivedBytesByClient;
    private readonly bool _echoReceivedData;
    private readonly int _receiveDelayMs;
    private readonly bool _blockFirstReceive;
    private readonly ManualResetEventSlim _blockedReceiveEntered;
    private readonly ManualResetEventSlim _blockedReceiveRelease;
    private long _totalReceivedBytes;
    private int _activeReceiveCallbacks;
    private int _maxConcurrentReceiveCallbacks;
    private int _blockedReceiveConsumed;

    internal RecordingConsumer(
        bool echoReceivedData = false,
        int receiveDelayMs = 0,
        bool blockFirstReceive = false
    )
    {
        _sync = new object();
        _connectedClients = new List<ConnectedClientRecord>();
        _disconnectedClients = new List<DisconnectedClientRecord>();
        _errors = new List<Exception>();
        _receivedBytesByClient = new Dictionary<ClientKey, long>();
        _echoReceivedData = echoReceivedData;
        _receiveDelayMs = receiveDelayMs;
        _blockFirstReceive = blockFirstReceive;
        _blockedReceiveEntered = new ManualResetEventSlim(false);
        _blockedReceiveRelease = new ManualResetEventSlim(!blockFirstReceive);
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

    internal int MaxConcurrentReceiveCallbacks => Volatile.Read(ref _maxConcurrentReceiveCallbacks);

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

    public void OnReceivedData(ClientHandle clientHandle, byte[] data)
    {
        int activeCallbacks = Interlocked.Increment(ref _activeReceiveCallbacks);
        UpdateMaxConcurrentCallbacks(activeCallbacks);

        try
        {
            if (_receiveDelayMs > 0)
            {
                Thread.Sleep(_receiveDelayMs);
            }

            ClientKey key = new ClientKey(clientHandle.Port, clientHandle.Generation);
            lock (_sync)
            {
                _receivedBytesByClient.TryGetValue(key, out long currentBytes);
                _receivedBytesByClient[key] = currentBytes + data.Length;
            }

            Interlocked.Add(ref _totalReceivedBytes, data.Length);

            if (_blockFirstReceive && Interlocked.CompareExchange(ref _blockedReceiveConsumed, 1, 0) == 0)
            {
                _blockedReceiveEntered.Set();
                _blockedReceiveRelease.Wait();
            }

            if (_echoReceivedData)
            {
                clientHandle.Send(data);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeReceiveCallbacks);
        }
    }

    public void OnClientDisconnected(ClientSnapshot clientSnapshot)
    {
        lock (_sync)
        {
            _disconnectedClients.Add(new DisconnectedClientRecord(clientSnapshot));
        }
    }

    public void OnClientConnected(ClientHandle clientHandle)
    {
        ClientKey key = new ClientKey(clientHandle.Port, clientHandle.Generation);
        ConnectedClientRecord record = new ConnectedClientRecord(clientHandle, key, clientHandle.UnitOfOrder);

        lock (_sync)
        {
            _connectedClients.Add(record);
        }
    }

    public void OnError(ClientHandle clientHandle, Exception exception, string message)
    {
        lock (_sync)
        {
            _errors.Add(exception);
        }
    }

    internal long GetReceivedBytes(ClientKey key)
    {
        lock (_sync)
        {
            return _receivedBytesByClient.GetValueOrDefault(key);
        }
    }

    internal ConnectedClientRecord GetConnectedClient(int index)
    {
        lock (_sync)
        {
            return _connectedClients[index];
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

    internal async Task WaitForTotalReceivedBytesAsync(long expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => TotalReceivedBytes >= expected,
            timeout,
            $"Timed out waiting for {expected} received bytes. Current total: {TotalReceivedBytes}."
        ).ConfigureAwait(false);
    }

    internal async Task WaitForBlockedReceiveAsync(TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => _blockedReceiveEntered.IsSet,
            timeout,
            "Timed out waiting for the blocked receive callback to start."
        ).ConfigureAwait(false);
    }

    internal void ReleaseBlockedReceive()
    {
        _blockedReceiveRelease.Set();
    }

    private void UpdateMaxConcurrentCallbacks(int activeCallbacks)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxConcurrentReceiveCallbacks);
            if (activeCallbacks <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(
                   ref _maxConcurrentReceiveCallbacks,
                   activeCallbacks,
                   observed
               ) != observed);
    }
}
