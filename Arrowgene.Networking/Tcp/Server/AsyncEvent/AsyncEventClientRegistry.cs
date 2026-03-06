using System;
using System.Collections.Generic;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

internal sealed class AsyncEventClientRegistry : IDisposable
{
    private readonly object _sync;
    private readonly int[] _generationByClientId;
    private readonly int[] _laneLoadByIndex;
    private readonly AsyncEventClient[] _allClients;
    private readonly Stack<AsyncEventClient> _availableClients;
    private readonly List<AsyncEventClientHandle> _activeHandles;

    internal AsyncEventClientRegistry(int maxConnections, int orderingLaneCount, Func<int, AsyncEventClient> clientFactory)
    {
        if (maxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        }

        if (orderingLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderingLaneCount));
        }

        if (clientFactory is null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        _sync = new object();
        _generationByClientId = new int[maxConnections];
        _laneLoadByIndex = new int[orderingLaneCount];
        _allClients = new AsyncEventClient[maxConnections];
        _availableClients = new Stack<AsyncEventClient>(maxConnections);
        _activeHandles = new List<AsyncEventClientHandle>(maxConnections);

        for (int clientId = maxConnections - 1; clientId >= 0; clientId--)
        {
            AsyncEventClient client = clientFactory(clientId);
            _allClients[clientId] = client;
            _availableClients.Push(client);
        }
    }

    internal IReadOnlyList<AsyncEventClient> AllClients => _allClients;

    internal int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _activeHandles.Count;
            }
        }
    }

    internal int AvailableCount
    {
        get
        {
            lock (_sync)
            {
                return _availableClients.Count;
            }
        }
    }

    internal void PrepareForStart()
    {
        lock (_sync)
        {
            if (_availableClients.Count != _allClients.Length)
            {
                throw new InvalidOperationException(
                    $"The client pool is not fully drained. Count:{_availableClients.Count} Expected:{_allClients.Length}.");
            }

            if (_activeHandles.Count != 0)
            {
                throw new InvalidOperationException("Found active client handles from a previous server run.");
            }

            Array.Clear(_laneLoadByIndex, 0, _laneLoadByIndex.Length);
            for (int index = 0; index < _generationByClientId.Length; index++)
            {
                unchecked
                {
                    _generationByClientId[index]++;
                }
            }
        }
    }

    internal bool TryActivateClient(
        System.Net.Sockets.Socket acceptedSocket,
        int runGeneration,
        out AsyncEventClient? client,
        out AsyncEventClientHandle handle,
        out int activeConnections)
    {
        lock (_sync)
        {
            if (!_availableClients.TryPop(out AsyncEventClient? pooledClient))
            {
                client = null;
                handle = default;
                activeConnections = _activeHandles.Count;
                return false;
            }

            int orderingLane = FindLeastLoadedLane();
            _laneLoadByIndex[orderingLane]++;
            int generation = unchecked(++_generationByClientId[pooledClient.ClientId]);

            try
            {
                pooledClient.Activate(acceptedSocket, orderingLane, runGeneration, generation);
            }
            catch
            {
                _laneLoadByIndex[orderingLane]--;
                _availableClients.Push(pooledClient);
                throw;
            }

            handle = pooledClient.CreateHandle();
            _activeHandles.Add(handle);
            activeConnections = _activeHandles.Count;
            client = pooledClient;
            return true;
        }
    }

    internal bool TryRemoveActiveClient(AsyncEventClient client, out int activeConnections)
    {
        lock (_sync)
        {
            AsyncEventClientHandle handle = client.CreateHandle();
            bool removed = _activeHandles.Remove(handle);
            if (removed)
            {
                int orderingLane = client.UnitOfOrder;
                if ((uint)orderingLane < (uint)_laneLoadByIndex.Length && _laneLoadByIndex[orderingLane] > 0)
                {
                    _laneLoadByIndex[orderingLane]--;
                }
            }

            activeConnections = _activeHandles.Count;
            return removed;
        }
    }

    internal void SnapshotActiveHandles(List<AsyncEventClientHandle> destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        lock (_sync)
        {
            destination.Clear();
            destination.AddRange(_activeHandles);
        }
    }

    internal bool TryRecycle(AsyncEventClient client)
    {
        if (client.IsAlive || client.PendingOperations > 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (!client.TryReturnToPool())
            {
                return false;
            }

            client.ResetForPool();
            _availableClients.Push(client);
            return true;
        }
    }

    internal bool WaitForPoolDrain(int timeoutMs)
    {
        int waitedMs = 0;
        while (waitedMs < timeoutMs)
        {
            lock (_sync)
            {
                if (_availableClients.Count == _allClients.Length && _activeHandles.Count == 0)
                {
                    return true;
                }
            }

            Thread.Sleep(10);
            waitedMs += 10;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _activeHandles.Clear();
            _availableClients.Clear();
        }

        for (int index = 0; index < _allClients.Length; index++)
        {
            _allClients[index].Dispose();
        }
    }

    private int FindLeastLoadedLane()
    {
        int bestLane = 0;
        int bestLoad = _laneLoadByIndex[0];

        for (int index = 1; index < _laneLoadByIndex.Length; index++)
        {
            if (_laneLoadByIndex[index] < bestLoad)
            {
                bestLoad = _laneLoadByIndex[index];
                bestLane = index;
            }
        }

        return bestLane;
    }
}
