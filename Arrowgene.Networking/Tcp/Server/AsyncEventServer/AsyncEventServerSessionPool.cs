using System;
using System.Collections.Generic;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

internal sealed class AsyncEventServerSessionPool : IDisposable
{
    private readonly object _sync;
    private readonly AsyncEventServerSessionState[] _sessions;
    private readonly Stack<AsyncEventServerSessionState> _available;
    private readonly List<AsyncEventServerSessionState> _active;
    private readonly int[] _laneLoads;
    private readonly int[] _generations;

    internal AsyncEventServerSessionPool(
        int capacity,
        int orderingLaneCount,
        Func<int, AsyncEventServerSessionState> factory)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (orderingLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderingLaneCount));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        _sync = new object();
        _sessions = new AsyncEventServerSessionState[capacity];
        _available = new Stack<AsyncEventServerSessionState>(capacity);
        _active = new List<AsyncEventServerSessionState>(capacity);
        _laneLoads = new int[orderingLaneCount];
        _generations = new int[capacity];

        for (int index = capacity - 1; index >= 0; index--)
        {
            AsyncEventServerSessionState session = factory(index);
            _sessions[index] = session;
            _available.Push(session);
        }
    }

    internal int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active.Count;
            }
        }
    }

    internal bool IsDrained
    {
        get
        {
            lock (_sync)
            {
                return _available.Count == _sessions.Length && _active.Count == 0;
            }
        }
    }

    internal bool TryAcquire(out AsyncEventServerSessionState? session, out int generation, out int orderingLane)
    {
        lock (_sync)
        {
            if (_available.Count == 0)
            {
                session = null;
                generation = 0;
                orderingLane = 0;
                return false;
            }

            session = _available.Pop();
            generation = unchecked(++_generations[session.SessionId]);
            orderingLane = FindLeastLoadedLane();
            _laneLoads[orderingLane]++;
            return true;
        }
    }

    internal void RegisterActive(AsyncEventServerSessionState session, int orderingLane)
    {
        lock (_sync)
        {
            session.ActiveIndex = _active.Count;
            _active.Add(session);
        }
    }

    internal void UnregisterActive(AsyncEventServerSessionState session)
    {
        lock (_sync)
        {
            int activeIndex = session.ActiveIndex;
            if (activeIndex < 0)
            {
                return;
            }

            int lastIndex = _active.Count - 1;
            AsyncEventServerSessionState lastSession = _active[lastIndex];
            _active[activeIndex] = lastSession;
            lastSession.ActiveIndex = activeIndex;
            _active.RemoveAt(lastIndex);

            int orderingLane = session.OrderingLane;
            if ((uint)orderingLane < (uint)_laneLoads.Length && _laneLoads[orderingLane] > 0)
            {
                _laneLoads[orderingLane]--;
            }

            session.ActiveIndex = -1;
        }
    }

    internal void SnapshotActiveSessions(List<AsyncEventServerSessionState> buffer)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        lock (_sync)
        {
            buffer.Clear();
            buffer.AddRange(_active);
        }
    }

    internal bool TryRecycle(AsyncEventServerSessionState session)
    {
        if (session.ActiveIndex >= 0 || !session.IsReadyForRecycle)
        {
            return false;
        }

        lock (_sync)
        {
            if (session.ActiveIndex >= 0 || !session.TryMarkPooled())
            {
                return false;
            }

            session.ResetForPool();
            _available.Push(session);
            return true;
        }
    }

    internal bool WaitForDrain(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (IsDrained)
            {
                bool allIdle = true;
                for (int index = 0; index < _sessions.Length; index++)
                {
                    AsyncEventServerSessionState session = _sessions[index];
                    if (session.PendingOperations != 0 || session.IsHandleValid(session.Generation))
                    {
                        allIdle = false;
                        break;
                    }
                }

                if (allIdle)
                {
                    return true;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        for (int index = 0; index < _sessions.Length; index++)
        {
            _sessions[index].Dispose();
        }
    }

    private int FindLeastLoadedLane()
    {
        int bestLane = 0;
        int bestLoad = _laneLoads[0];
        for (int index = 1; index < _laneLoads.Length; index++)
        {
            int load = _laneLoads[index];
            if (load < bestLoad)
            {
                bestLoad = load;
                bestLane = index;
            }
        }

        return bestLane;
    }
}
