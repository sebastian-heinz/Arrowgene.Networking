using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal sealed class ClientRegistry : IDisposable
{
    private readonly object _sync;
    private readonly int[] _laneLoadByIndex;
    private readonly Client[] _allClients;
    private readonly Stack<Client> _availableClients;
    private readonly List<ClientHandle> _activeHandles;
    private readonly int _maxConnections;

    internal ClientRegistry(int maxConnections, int orderingLaneCount,
        Func<int, Client> clientFactory)
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
        _allClients = new Client[_maxConnections];
        _availableClients = new Stack<Client>(_maxConnections);
        _activeHandles = new List<ClientHandle>(_maxConnections);

        for (int clientId = _maxConnections - 1; clientId >= 0; clientId--)
        {
            Client client = clientFactory(clientId);
            _allClients[clientId] = client;
            _availableClients.Push(client);
        }
    }

    internal uint GetAliveClientCount()
    {
        uint liveConnections = 0;
        List<ClientHandle> handles = new List<ClientHandle>(_maxConnections);
        SnapshotActiveHandles(handles);
        foreach (ClientHandle handle in handles)
        {
            if (handle.TryGetClient(out Client client))
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
        Server server,
        Socket acceptedSocket,
        out ClientHandle handle
    )
    {
        lock (_sync)
        {
            if (!_availableClients.TryPop(out Client? pooledClient))
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

            handle = new ClientHandle(server, pooledClient);
            pooledClient.ReceiveEventArgs.UserToken = handle;
            pooledClient.SendEventArgs.UserToken = handle;
            _activeHandles.Add(handle);
            return true;
        }
    }

    internal bool TryDeactivateClient(ClientHandle clientHandle)
    {
        lock (_sync)
        {
            if (!clientHandle.TryGetClient(out Client client))
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

    internal void SnapshotActiveHandles(List<ClientHandle> destination)
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