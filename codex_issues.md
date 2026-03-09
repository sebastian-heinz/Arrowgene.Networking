# AsyncEventServer Technical Audit

Scope: the implementation under review is [`Arrowgene.Networking/SAEAServer/Server.cs`](Arrowgene.Networking/SAEAServer/Server.cs), which appears to be the current replacement for the requested `AsyncEventServer.cs`.

## Critical Issues

### 1. Shutdown can stall on `_lifecycleLock` and block its own worker thread

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:134-161`
- `Arrowgene.Networking/SAEAServer/Server.cs:166-189`
- `Arrowgene.Networking/SAEAServer/Server.cs:192-209`
- `Arrowgene.Networking/SAEAServer/Server.cs:212-263`
- `Arrowgene.Networking/SAEAServer/Server.cs:784-810`

Problem:
- `ServerStart()` starts `_acceptThread` and `_timeoutThread` while still holding `_lifecycleLock`.
- `Run()` immediately tries to reacquire the same lock before it can do any work.
- `ServerStop()` also holds `_lifecycleLock` while calling `Shutdown()`, and `Shutdown()` performs the expensive parts of shutdown while that lock is still held by the outer caller.
- `Run()` keeps `_lifecycleLock` around the entire `TrySocketListen()` retry loop, including the 1 second retry wait.

Failure mode:
- If `ServerStop()` runs before `_acceptThread` reaches `Run()`, shutdown will call `JoinThread(_acceptThread, 10000)` while the accept thread is blocked waiting for `_lifecycleLock`. That produces an avoidable 10 second shutdown delay and a false join-timeout error.
- If listener startup is retrying, stop/dispose cannot acquire `_lifecycleLock` to cancel the retry loop until all retries finish, so stop latency is bounded by retry count instead of intent.
- Because `Shutdown()` also disconnects clients while the outer stop/dispose call still owns `_lifecycleLock`, user callbacks triggered by disconnect run while the global lifecycle lock is held. That amplifies the stall window and creates deadlock potential if user code waits on another thread that tries to enter a lifecycle API.

Impact:
- Stop/dispose can hang for the full join timeout.
- Startup failure or bind retry paths become unresponsive to stop.
- Lifecycle lock contention is much higher than necessary for a single-run server.

### 2. Dispose races in-flight SAEA completions and can throw `ObjectDisposedException`

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:315-319`
- `Arrowgene.Networking/SAEAServer/Server.cs:784-829`
- `Arrowgene.Networking/SAEAServer/AcceptPool.cs:83-109`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:104-160`
- `Arrowgene.Networking/SAEAServer/Client.cs:191-205`
- `Arrowgene.Networking/SAEAServer/Client.cs:320-324`

Problem:
- Shutdown closes the listener and joins the two dedicated threads, but it never waits for outstanding accept/send/receive completions running on I/O completion threads.
- `Dispose()` immediately follows with `_acceptPool.Dispose()` and `_clientRegistry.Dispose()`.
- `ProcessAccept()` always returns its `SocketAsyncEventArgs` to the pool first. A late accept completion after `_acceptPool.Dispose()` will execute `_acceptPool.Return()` and hit `_capacityGate.Release()` on a disposed semaphore.
- `Disconnect()` only returns a client to the registry when `PendingOperations == 0`. During shutdown, clients with in-flight send/receive completions stay active. `Dispose()` still disposes every client's `SocketAsyncEventArgs`, so late `ReceiveCompleted` / `SendCompleted` callbacks can run against disposed args or disposed client state.

Failure mode:
- Unhandled `ObjectDisposedException` from `AcceptPool.Return()` after dispose.
- Unhandled exceptions from late `ReceiveCompleted` / `SendCompleted` callbacks touching disposed `SocketAsyncEventArgs`.
- Lost or duplicated disconnect cleanup when shutdown races pending I/O.

Impact:
- This is the most likely crash path in a busy shutdown.
- It also makes the accept pool and client registry unsafe to dispose under load, even though there is no steady-state pool leak while the server is running.

### 3. Receive processing releases the client slot before user delivery completes

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:453-513`
- `Arrowgene.Networking/SAEAServer/Server.cs:685-731`
- `Arrowgene.Networking/SAEAServer/Client.cs:191-205`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:104-132`

Problem:
- `ProcessReceive()` decrements `PendingOperations` at line 461 before it copies the buffer and before it calls `_consumer.OnReceivedData(...)`.
- Once the pending count hits zero, a concurrent `Disconnect()` can succeed, return the client to the pool, and make the slot immediately reusable.
- The receive callback then continues executing with a handle that may already be disconnected or recycled for a new connection.

Failure mode:
- Data from the old connection can be delivered after the connection was closed.
- If the slot is recycled quickly, the handle becomes stale during `_consumer.OnReceivedData(...)`. With the current `ThreadedBlockingQueueConsumer`, that can throw from `socket.UnitOfOrder` before the event is even queued.
- Because the callback already copied the bytes, the race is a real correctness bug, not just a noisy log.

Impact:
- Cross-generation delivery and stale-handle exceptions under concurrent disconnect/timeout/close.
- Ordering guarantees become unreliable exactly in the high-contention cases where they matter most.

## Optimization Suggestions

### Accept path

- `StartAccept()` is efficient when accepts stay asynchronous, but synchronous accepts are processed inline on the accept thread. That means `ProcessAccept()`, `_consumer.OnClientConnected(...)`, and `StartReceive()` all run before the accept loop can re-arm more accepts. Under bursty connect traffic, that creates head-of-line blocking in the accept path.
- Refactor goal: re-arm accept capacity first, then hand connection setup to a separate processing path, or at minimum keep the accept-thread work bounded and non-user-blocking.

### Timeout scanning

- `CheckSocketTimeout()` does a full snapshot and full sweep of all active handles every interval (`Server.cs:734-781`). For very large connection counts, that becomes an O(n) polling tax on a dedicated thread.
- The current reuse of a pre-sized `List<ClientHandle>` keeps GC low, but the algorithm still scales poorly with 50k+ connections.
- Better options are a sharded sweep, a coarse timing wheel, or checking only a fraction of the registry per tick.

### Active handle bookkeeping and GC pressure

References:
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:49-66`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:129`
- `Arrowgene.Networking/SAEAServer/Server.cs:708-716`

- `Disconnect()` calls `GetAliveClientCount()`, which allocates a new `List<ClientHandle>` and scans all active clients on every disconnect.
- `_activeHandles.Remove(clientHandle)` is O(n), so mass disconnects combine O(n) removal with another O(n) live-count pass.
- Maintain an atomic `_aliveCount`, and switch active-client tracking to an O(1) removal strategy if disconnect throughput matters.

### Completion robustness

- `AcceptCompleted`, `ReceiveCompleted`, and `SendCompleted` have no outer exception guard. Any bug in pool return, queue accounting, or callback handling can escape an I/O completion callback and terminate the process.
- Even after the disposal races are fixed, keeping a narrow top-level `try/catch` in the completion entrypoints is worthwhile for survivability and diagnostics.

## Code Refactor

### 1. Replace lock-based lifecycle transitions with an atomic state machine

```csharp
private enum ServerState
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Disposed = 5
}

private int _state = (int)ServerState.Created;

public void ServerStart()
{
    if (Interlocked.CompareExchange(
            ref _state,
            (int)ServerState.Starting,
            (int)ServerState.Created) != (int)ServerState.Created)
    {
        return;
    }

    try
    {
        if (_socketTimeout > TimeSpan.Zero)
        {
            _timeoutThread.Start();
        }

        _acceptThread.Start();
        Volatile.Write(ref _state, (int)ServerState.Running);
    }
    catch
    {
        Volatile.Write(ref _state, (int)ServerState.Stopped);
        throw;
    }
}

public void ServerStop()
{
    int previous = Interlocked.Exchange(ref _state, (int)ServerState.Stopping);
    if (previous is (int)ServerState.Stopped or (int)ServerState.Disposed)
    {
        return;
    }

    ShutdownCore();
    Volatile.Write(ref _state, (int)ServerState.Stopped);
}
```

Why:
- State changes become single-step and non-blocking.
- Threads are not joined while a lifecycle lock is held.
- `Run()` no longer needs to keep a global lock across bind retries.

### 2. Drain outstanding accepts before disposing the accept pool

```csharp
private int _acceptsInFlight;
private readonly ManualResetEventSlim _acceptsDrained = new(initialState: true);

private bool TryIssueAccept(Socket listenSocket, SocketAsyncEventArgs args)
{
    _acceptsDrained.Reset();
    Interlocked.Increment(ref _acceptsInFlight);

    try
    {
        if (!listenSocket.AcceptAsync(args))
        {
            ProcessAccept(args);
        }

        return true;
    }
    catch
    {
        if (Interlocked.Decrement(ref _acceptsInFlight) == 0)
        {
            _acceptsDrained.Set();
        }

        throw;
    }
}

private void ProcessAccept(SocketAsyncEventArgs args)
{
    try
    {
        // Existing accept processing.
    }
    finally
    {
        _acceptPool.Return(args);

        if (Interlocked.Decrement(ref _acceptsInFlight) == 0)
        {
            _acceptsDrained.Set();
        }
    }
}

private void ShutdownCore()
{
    Service.CloseSocket(_listenSocket);
    _listenSocket = null;
    _cancellation.Cancel();

    _acceptsDrained.Wait(ThreadTimeoutMs);
}
```

Why:
- Pool disposal becomes safe because shutdown waits for accept completions to drain.
- The same pattern should be mirrored for per-client send/receive operations before disposing client `SocketAsyncEventArgs`.

### 3. Keep the receive operation leased until user delivery finishes

```csharp
private bool ProcessReceive(ClientHandle handle)
{
    if (!handle.TryGetClient(out Client client))
    {
        return false;
    }

    try
    {
        SocketAsyncEventArgs receiveArgs = client.ReceiveEventArgs;

        if (receiveArgs.SocketError != SocketError.Success)
        {
            return false;
        }

        int count = receiveArgs.BytesTransferred;
        if (count <= 0 || receiveArgs.Buffer is not byte[] buffer)
        {
            return false;
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(count);
        Buffer.BlockCopy(buffer, receiveArgs.Offset, payload, 0, count);
        client.RecordReceive(count);

        _consumer.OnReceivedData(handle, payload);
        return client.IsAlive;
    }
    finally
    {
        client.DecrementPendingOperations();
    }
}
```

Why:
- `Disconnect()` cannot recycle the slot while the callback is still in progress.
- The receive callback either finishes against the original client generation or the final disconnect happens after the callback unwinds.

## Bottom Line

- I did not find a normal steady-state leak in `AcceptPool` or `ClientRegistry` while the server remains running.
- The real failures are lifecycle coordination bugs: shutdown lock contention, disposal before I/O completions drain, and releasing the client slot before `OnReceivedData` finishes.
- Those three issues are enough to produce hangs, stale-handle faults, and shutdown-time crashes under real load.
