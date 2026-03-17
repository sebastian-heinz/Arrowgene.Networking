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
    private readonly ushort _maxConnections;

    internal ClientRegistry(ushort maxConnections, int orderingLaneCount,
        Func<ushort, Client> clientFactory)
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

        for (int clientId = maxConnections - 1; clientId >= 0; clientId--)
        {
            Client client = clientFactory((ushort)clientId);
            _allClients[clientId] = client;
            _availableClients.Push(client);
        }
    }

    internal uint GetAliveClientCount()
    {
        lock (_sync)
        {
            return (uint)_activeHandles.Count;
        }
    }

    internal int GetAvailableClientSlotCount()
    {
        lock (_sync)
        {
            return _availableClients.Count;
        }
    }

    internal bool TryActivateClient(
        TcpServer tcpServer,
        Socket acceptedSocket,
        out ClientHandle clientHandle
    )
    {
        lock (_sync)
        {
            if (!_availableClients.TryPop(out Client? pooledClient))
            {
                clientHandle = default;
                return false;
            }

            int orderingLane = FindLeastLoadedLane();
            _laneLoadByIndex[orderingLane]++;

            try
            {
                pooledClient.Activate(acceptedSocket, orderingLane, out clientHandle);
            }
            catch
            {
                _laneLoadByIndex[orderingLane]--;
                _availableClients.Push(pooledClient);
                throw;
            }

            pooledClient.ReceiveEventArgs.UserToken = clientHandle;
            pooledClient.SendEventArgs.UserToken = clientHandle;
            _activeHandles.Add(clientHandle);
            return true;
        }
    }

    internal bool TryDeactivateClient(ClientHandle clientHandle, out ClientSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!clientHandle.TryGetClient(out Client client))
            {
                snapshot = default;
                return false;
            }

            if (!client.CanReturnToPool())
            {
                snapshot = default;
                return false;
            }

            snapshot = client.Snapshot();
            int orderingLane = client.UnitOfOrder;
            if ((uint)orderingLane < (uint)_laneLoadByIndex.Length && _laneLoadByIndex[orderingLane] > 0)
            {
                _laneLoadByIndex[orderingLane]--;
            }

            client.ResetForPool();
            _availableClients.Push(client);
            RemoveActiveHandleFastNoLock(clientHandle);
        }

        return true;
    }

    private void RemoveActiveHandleFastNoLock(ClientHandle clientHandle)
    {
        int idx = _activeHandles.IndexOf(clientHandle);
        if (idx >= 0)
        {
            int last = _activeHandles.Count - 1;
            if (idx != last)
            {
                _activeHandles[idx] = _activeHandles[last];
            }

            _activeHandles.RemoveAt(last);
        }
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

    internal void SnapshotLaneLoads(long[] destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < _laneLoadByIndex.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination),
                "Destination must be at least as large as the configured ordering lane count.");
        }

        lock (_sync)
        {
            for (int index = 0; index < _laneLoadByIndex.Length; index++)
            {
                destination[index] = _laneLoadByIndex[index];
            }
        }
    }

    internal long GetTotalSendQueuedBytes()
    {
        long total = 0;

        for (int index = 0; index < _allClients.Length; index++)
        {
            Client client = _allClients[index];
            if (client.IsAlive)
            {
                total += client.GetSendQueuedBytes();
            }
        }

        return total;
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
