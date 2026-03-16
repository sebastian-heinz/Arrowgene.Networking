using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.Tests;

internal sealed class ThreadedDisconnectTestConsumer : ThreadedBlockingQueueConsumer
{
    private readonly object _sync;
    private readonly List<ClientSnapshot> _disconnects;
    private readonly bool _blockFirstDisconnect;
    private readonly bool _throwOnFirstDisconnect;
    private readonly ManualResetEventSlim _firstDisconnectEntered;
    private readonly ManualResetEventSlim _blockedDisconnectRelease;
    private int _blockedDisconnectConsumed;
    private int _throwConsumed;
    private int _handlerExceptionCount;

    internal ThreadedDisconnectTestConsumer(
        int orderingLaneCount,
        int queueCapacityPerLane = 1024,
        bool blockFirstDisconnect = false,
        bool throwOnFirstDisconnect = false
    )
        : base(orderingLaneCount, queueCapacityPerLane, nameof(ThreadedDisconnectTestConsumer))
    {
        _sync = new object();
        _disconnects = new List<ClientSnapshot>();
        _blockFirstDisconnect = blockFirstDisconnect;
        _throwOnFirstDisconnect = throwOnFirstDisconnect;
        _firstDisconnectEntered = new ManualResetEventSlim(false);
        _blockedDisconnectRelease = new ManualResetEventSlim(!blockFirstDisconnect);
    }

    internal int DisconnectCount
    {
        get
        {
            lock (_sync)
            {
                return _disconnects.Count;
            }
        }
    }

    internal int HandlerExceptionCount => Volatile.Read(ref _handlerExceptionCount);

    internal ClientSnapshot GetDisconnectedSnapshot(int index)
    {
        lock (_sync)
        {
            return _disconnects[index];
        }
    }

    internal IReadOnlyList<ClientSnapshot> GetDisconnectedSnapshots()
    {
        lock (_sync)
        {
            return _disconnects.ToArray();
        }
    }

    protected override void HandleReceived(ClientHandle clientHandle, byte[] data)
    {
    }

    protected override void HandleDisconnected(ClientSnapshot clientSnapshot)
    {
        if (_throwOnFirstDisconnect && Interlocked.CompareExchange(ref _throwConsumed, 1, 0) == 0)
        {
            Interlocked.Increment(ref _handlerExceptionCount);
            throw new InvalidOperationException("Intentional disconnect handler failure.");
        }

        if (_blockFirstDisconnect && Interlocked.CompareExchange(ref _blockedDisconnectConsumed, 1, 0) == 0)
        {
            _firstDisconnectEntered.Set();
            _blockedDisconnectRelease.Wait();
        }

        lock (_sync)
        {
            _disconnects.Add(clientSnapshot);
        }
    }

    protected override void HandleConnected(ClientHandle clientHandle)
    {
    }

    protected override void HandleError(ClientHandle clientHandle, Exception exception, string message)
    {
    }

    internal async Task WaitForDisconnectCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => DisconnectCount >= expected,
            timeout,
            $"Timed out waiting for {expected} disconnects. Current count: {DisconnectCount}."
        ).ConfigureAwait(false);
    }

    internal async Task WaitForFirstDisconnectEnteredAsync(TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => _firstDisconnectEntered.IsSet,
            timeout,
            "Timed out waiting for the first disconnect handler to enter."
        ).ConfigureAwait(false);
    }

    internal void ReleaseBlockedDisconnect()
    {
        _blockedDisconnectRelease.Set();
    }
}
