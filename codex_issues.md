# AsyncEventServer Technical Audit

Scope reviewed:
- `Arrowgene.Networking/SAEAServer/Server.cs`
- `Arrowgene.Networking/SAEAServer/AcceptPool.cs`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs`
- `Arrowgene.Networking/SAEAServer/Client.cs`

## Critical Issues

### CRIT-1: Lifecycle lock is held across full shutdown path, creating stop/dispose stalls and join timeouts
**Evidence**
- `ServerStop` holds `_lifecycleLock` and calls `Shutdown` inside the lock (`Server.cs:168-187`).
- `Dispose` does the same (`Server.cs:806-815`).
- `Shutdown` joins `_acceptThread` and `_timeoutThread` (`Server.cs:800-801`).
- `_acceptThread` entry (`Run`) also needs `_lifecycleLock` (`Server.cs:194`).

**Why this is critical**
- If stop/dispose races with startup, `_acceptThread` can be blocked waiting for `_lifecycleLock` while shutdown is waiting for `_acceptThread` to exit.
- Result: repeated 10s join timeout (`ThreadTimeoutMs`) and externally visible hangs on stop/dispose.

---

### CRIT-2: Accept completion can call into disposed `AcceptPool` (unhandled `ObjectDisposedException`)
**Evidence**
- Accept completions return args through `_acceptPool.Return(...)` (`Server.cs:319`).
- `AcceptPool.Return` calls `_capacityGate.Release()` with no disposal guard (`AcceptPool.cs:93`).
- `Dispose` tears down `_acceptPool` after shutdown (`Server.cs:817`), while pending accepts can still complete on IO threads.

**Why this is critical**
- A late completion after pool disposal can throw `ObjectDisposedException` from `SemaphoreSlim.Release()`.
- This exception is not caught in completion path and can terminate the process.

---

### CRIT-3: Resources are disposed even when worker shutdown did not complete
**Evidence**
- `Shutdown` uses bounded joins (`Server.cs:800-801`) and does not fail/abort dispose path on timeout.
- `Dispose` immediately disposes `_clientRegistry` and `_cancellation` afterward (`Server.cs:818-819`).
- `CheckSocketTimeout` repeatedly touches `cancellationToken.WaitHandle` (`Server.cs:771`).

**Why this is critical**
- If join times out, still-running worker/callback code can access disposed objects (`CancellationTokenSource`, pooled SAEA/client data), causing sporadic `ObjectDisposedException` and undefined teardown behavior.

## Optimization Suggestions

1. Timeout scanning is `O(active clients)` every cycle (`Server.cs:733-764`) and capped at 30s poll (`Server.cs:766-771`) even for large socket timeouts.  
Use a deadline min-heap or timing wheel keyed by `LastReadMs/LastWriteMs` to avoid full scans at high connection counts.

2. `ProcessReceive` allocates a new `byte[]` for every packet (`Server.cs:494-495`).  
The copy-for-isolation requirement is valid, but this is high GC pressure. If API can evolve, pass owned buffers (`IMemoryOwner<byte>`) with explicit lifetime; if API cannot change, pre-size packet framing to reduce small allocation frequency.

3. `StartAccept` retries immediately on `SocketException` (`Server.cs:296-300`) with full exception logging each iteration.  
Add bounded backoff + rate-limited logging to prevent CPU/log storms during transient listener faults.

4. `Disconnect` logs multiline info and executes consumer callback for each client (`Server.cs:699-722`) during shutdown path.  
Batch disconnect telemetry or reduce per-client sync logging in stop flows to shorten tail latency.

## Code Refactor (Synchronization / Lifecycle)

### 1) Move shutdown work outside `_lifecycleLock`
```csharp
public void ServerStop()
{
    bool shouldShutdown;

    lock (_lifecycleLock)
    {
        if (_isDisposed || _isStopped)
        {
            return;
        }

        _isRunning = false;
        _isStopped = true;
        shouldShutdown = true;
    }

    if (shouldShutdown)
    {
        ShutdownCore();
    }
}
```

### 2) Drain in-flight accepts before disposing `AcceptPool`
```csharp
private int _inflightAccepts;
private readonly ManualResetEventSlim _acceptsDrained = new(initialState: true);

private bool TryPostAccept(Socket listenSocket, SocketAsyncEventArgs args)
{
    _acceptsDrained.Reset();
    Interlocked.Increment(ref _inflightAccepts);

    try
    {
        bool pending = listenSocket.AcceptAsync(args);
        if (!pending)
        {
            ProcessAccept(args);
        }
        return true;
    }
    catch
    {
        OnAcceptCompletedFinally(args);
        throw;
    }
}

private void OnAcceptCompletedFinally(SocketAsyncEventArgs args)
{
    _acceptPool.Return(args);
    if (Interlocked.Decrement(ref _inflightAccepts) == 0)
    {
        _acceptsDrained.Set();
    }
}
```

### 3) Dispose managed resources only after confirmed worker stop
```csharp
private bool StopWorkers()
{
    bool acceptStopped = Service.TryJoinThread(_acceptThread, ThreadTimeoutMs);
    bool timeoutStopped = Service.TryJoinThread(_timeoutThread, ThreadTimeoutMs);
    return acceptStopped && timeoutStopped;
}

public void Dispose()
{
    if (Interlocked.Exchange(ref _disposeState, 1) != 0)
    {
        return;
    }

    ServerStop();
    bool workersStopped = StopWorkers();

    if (!workersStopped)
    {
        Logger.Error("Server dispose skipped pooled-resource disposal because workers did not stop cleanly.");
        return;
    }

    _acceptPool.Dispose();
    _clientRegistry.Dispose();
    _cancellation.Dispose();
}
```
