using System.Collections.Generic;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SaeaSessionRegistry
{
    private readonly object _registryLock;
    private readonly SaeaSessionHandle[] _handles;
    private readonly bool[] _active;
    private int _activeCount;

    internal SaeaSessionRegistry(int capacity)
    {
        _registryLock = new object();
        _handles = new SaeaSessionHandle[capacity];
        _active = new bool[capacity];
        _activeCount = 0;
    }

    internal int ActiveCount
    {
        get
        {
            lock (_registryLock)
            {
                return _activeCount;
            }
        }
    }

    internal void Register(SaeaSessionHandle handle)
    {
        lock (_registryLock)
        {
            int sessionId = handle.SessionId;
            _handles[sessionId] = handle;
            if (_active[sessionId])
            {
                return;
            }

            _active[sessionId] = true;
            _activeCount++;
        }
    }

    internal bool Unregister(SaeaSessionHandle handle)
    {
        lock (_registryLock)
        {
            int sessionId = handle.SessionId;
            if (!_active[sessionId])
            {
                return false;
            }

            if (_handles[sessionId] != handle)
            {
                return false;
            }

            _handles[sessionId] = default;
            _active[sessionId] = false;
            _activeCount--;
            return true;
        }
    }

    internal void Snapshot(List<SaeaSessionHandle> buffer)
    {
        lock (_registryLock)
        {
            buffer.Clear();
            for (int index = 0; index < _active.Length; index++)
            {
                if (_active[index])
                {
                    buffer.Add(_handles[index]);
                }
            }
        }
    }

    internal void Clear()
    {
        lock (_registryLock)
        {
            for (int index = 0; index < _active.Length; index++)
            {
                _handles[index] = default;
                _active[index] = false;
            }

            _activeCount = 0;
        }
    }
}
