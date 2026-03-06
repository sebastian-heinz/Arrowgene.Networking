using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SaeaSessionPool : IDisposable
{
    private readonly object _poolLock;
    private readonly Stack<SaeaSession> _availableSessions;
    private readonly SaeaSession[] _sessions;
    private readonly int[] _generations;

    internal SaeaSessionPool(int capacity, Func<int, SaeaIoChannel> channelFactory, SaeaServer server)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(server);

        _poolLock = new object();
        _availableSessions = new Stack<SaeaSession>(capacity);
        _sessions = new SaeaSession[capacity];
        _generations = new int[capacity];

        for (int sessionId = 0; sessionId < capacity; sessionId++)
        {
            SaeaIoChannel channel = channelFactory(sessionId);
            SaeaSession session = new SaeaSession(sessionId, server, channel);
            _sessions[sessionId] = session;
            _availableSessions.Push(session);
        }
    }

    internal int Capacity => _sessions.Length;

    internal int AvailableCount
    {
        get
        {
            lock (_poolLock)
            {
                return _availableSessions.Count;
            }
        }
    }

    internal SaeaSession[] Sessions => _sessions;

    internal bool TryAcquire(out SaeaSession? session)
    {
        lock (_poolLock)
        {
            if (_availableSessions.Count == 0)
            {
                session = null;
                return false;
            }

            session = _availableSessions.Pop();
            return true;
        }
    }

    internal int NextGeneration(int sessionId)
    {
        return Interlocked.Increment(ref _generations[sessionId]);
    }

    internal void IncrementAllGenerations()
    {
        for (int sessionId = 0; sessionId < _generations.Length; sessionId++)
        {
            Interlocked.Increment(ref _generations[sessionId]);
        }
    }

    internal bool TryRecycle(SaeaSession session)
    {
        if (!session.Channel.IsReadyForRecycle)
        {
            return false;
        }

        if (!session.TryMarkPooled())
        {
            return false;
        }

        session.Channel.Reset();
        lock (_poolLock)
        {
            _availableSessions.Push(session);
        }

        return true;
    }

    internal bool WaitForDrain(int timeoutMs)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            bool allSessionsDrained = true;
            foreach (SaeaSession session in _sessions)
            {
                if (!session.Channel.IsReadyForRecycle)
                {
                    allSessionsDrained = false;
                    break;
                }
            }

            if (allSessionsDrained && AvailableCount == Capacity)
            {
                return true;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                return false;
            }

            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        foreach (SaeaSession session in _sessions)
        {
            session.Channel.Dispose();
        }
    }
}
