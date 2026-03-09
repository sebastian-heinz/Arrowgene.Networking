using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Arrowgene.Networking.Server;

internal sealed class AsyncEventClientRegistry : IDisposable
{
    private readonly object _sync;
    private readonly int[] _laneLoadByIndex;
    private readonly AsyncEventClient[] _allClients;
    private readonly Stack<AsyncEventClient> _availableClients;
    private readonly List<AsyncEventClientHandle> _activeHandles;
    private readonly int _maxConnections;

    internal AsyncEventClientRegistry(int maxConnections, int orderingLaneCount,
        Func<int, AsyncEventClient> clientFactory)
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

        _maxConnections = maxConnections;
        _sync = new object();
        _laneLoadByIndex = new int[orderingLaneCount];
        _allClients = new AsyncEventClient[_maxConnections];
        _availableClients = new Stack<AsyncEventClient>(_maxConnections);
        _activeHandles = new List<AsyncEventClientHandle>(_maxConnections);

        for (int clientId = _maxConnections - 1; clientId >= 0; clientId--)
        {
            AsyncEventClient client = clientFactory(clientId);
            _allClients[clientId] = client;
            _availableClients.Push(client);
        }
    }

    internal uint GetAliveClientCount()
    {
        uint liveConnections = 0;
        List<AsyncEventClientHandle> handles = new List<AsyncEventClientHandle>(_maxConnections);
        SnapshotActiveHandles(handles);
        foreach (AsyncEventClientHandle handle in handles)
        {
            if (handle.TryGetClient(out AsyncEventClient client))
            {
                if (client.IsAlive)
                {
                    liveConnections++;
                }
            }
        }

        return liveConnections;
    }

    internal bool TryActivateClient(
        AsyncEventServer server,
        Socket acceptedSocket,
        out AsyncEventClientHandle handle
    )
    {
        lock (_sync)
        {
            if (!_availableClients.TryPop(out AsyncEventClient? pooledClient))
            {
                handle = default;
                return false;
            }

            int orderingLane = FindLeastLoadedLane();
            _laneLoadByIndex[orderingLane]++;

            try
            {
                pooledClient.Activate(acceptedSocket, orderingLane);
            }
            catch
            {
                _laneLoadByIndex[orderingLane]--;
                _availableClients.Push(pooledClient);
                throw;
            }

            handle = new AsyncEventClientHandle(server, pooledClient);
            pooledClient.ReceiveEventArgs.UserToken = handle;
            pooledClient.SendEventArgs.UserToken = handle;
            _activeHandles.Add(handle);
            return true;
        }
    }

    internal bool TryDeactivateClient(AsyncEventClientHandle clientHandle)
    {
        lock (_sync)
        {
            if (!clientHandle.TryGetClient(out AsyncEventClient client))
            {
                return false;
            }

            if (!client.CanReturnToPool())
            {
                return false;
            }
            
            int orderingLane = client.UnitOfOrder;
            if ((uint)orderingLane < (uint)_laneLoadByIndex.Length && _laneLoadByIndex[orderingLane] > 0)
            {
                _laneLoadByIndex[orderingLane]--;
            }
            client.ResetForPool();
            _availableClients.Push(client);
            _activeHandles.Remove(clientHandle);
        }
        return true;
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