using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

internal sealed class AsyncEventAcceptPool : IDisposable
{
    private readonly object _sync;
    private readonly Stack<SocketAsyncEventArgs> _availableEventArgs;
    private readonly SocketAsyncEventArgs[] _allEventArgs;
    private readonly SemaphoreSlim _capacityGate;
    private readonly EventHandler<SocketAsyncEventArgs> _completedHandler;

    internal AsyncEventAcceptPool(int acceptConcurrency, EventHandler<SocketAsyncEventArgs> completedHandler)
    {
        if (acceptConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptConcurrency));
        }

        _completedHandler = completedHandler ?? throw new ArgumentNullException(nameof(completedHandler));
        _sync = new object();
        _availableEventArgs = new Stack<SocketAsyncEventArgs>(acceptConcurrency);
        _allEventArgs = new SocketAsyncEventArgs[acceptConcurrency];
        _capacityGate = new SemaphoreSlim(acceptConcurrency, acceptConcurrency);

        for (int index = 0; index < acceptConcurrency; index++)
        {
            SocketAsyncEventArgs eventArgs = new SocketAsyncEventArgs();
            eventArgs.Completed += _completedHandler;
            _allEventArgs[index] = eventArgs;
            _availableEventArgs.Push(eventArgs);
        }
    }

    internal int Capacity => _allEventArgs.Length;

    internal int CurrentCount
    {
        get
        {
            lock (_sync)
            {
                return _availableEventArgs.Count;
            }
        }
    }

    internal int CurrentSemaphoreCount => _capacityGate.CurrentCount;

    internal bool TryAcquire(CancellationToken cancellationToken, out SocketAsyncEventArgs? eventArgs)
    {
        eventArgs = null;

        try
        {
            _capacityGate.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        lock (_sync)
        {
            if (_availableEventArgs.TryPop(out SocketAsyncEventArgs? available))
            {
                eventArgs = available;
                return true;
            }
        }

        _capacityGate.Release();
        throw new InvalidOperationException("The accept semaphore is out of sync with the pooled accept event args.");
    }

    internal void PrepareForStart(int runGeneration)
    {
        lock (_sync)
        {
            foreach (SocketAsyncEventArgs eventArgs in _allEventArgs)
            {
                eventArgs.AcceptSocket = null;
                eventArgs.UserToken = runGeneration;
            }
        }
    }

    internal void Return(SocketAsyncEventArgs eventArgs, int runGeneration)
    {
        eventArgs.AcceptSocket = null;
        eventArgs.UserToken = runGeneration;

        lock (_sync)
        {
            _availableEventArgs.Push(eventArgs);
        }

        _capacityGate.Release();
    }

    internal bool WaitForDrain(int timeoutMs)
    {
        int waitedMs = 0;
        while (waitedMs < timeoutMs)
        {
            lock (_sync)
            {
                if (_availableEventArgs.Count == _allEventArgs.Length)
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
            _availableEventArgs.Clear();
        }

        for (int index = 0; index < _allEventArgs.Length; index++)
        {
            _allEventArgs[index].Completed -= _completedHandler;
            _allEventArgs[index].Dispose();
        }

        _capacityGate.Dispose();
    }
}
