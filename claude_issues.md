# Server.cs Technical Audit

Audit of `SAEAServer/Server.cs` and its supporting types. Reviewed against the AGENTS.md
architecture constraints: single-run lifecycle, intentional buffer copy, pooled resources.

---

## Critical Issues

### C1. Dispose/ServerStop Race — Resource Use-After-Dispose

**Files:** `Server.cs:191-220`, `Server.cs:938-955`

`ServerStop()` releases `_lifecycleLock` before calling `Shutdown()`. If `Dispose()` is called
concurrently, it sets state to `Disposed`, calls its own `Shutdown()`, then immediately disposes
`_acceptPool`, `_clientRegistry`, and `_cancellation` — while the first `Shutdown()` from
`ServerStop` may still be executing and referencing those objects.

```
Thread A (ServerStop):   lock { _state = Stopping } → Shutdown() [running...]
Thread B (Dispose):      lock { _state = Disposed } → Shutdown() → _acceptPool.Dispose() 💥
                         Thread A's Shutdown still using _acceptPool
```

**Impact:** `ObjectDisposedException` or corrupted state during shutdown.

**Fix:** Guard `Shutdown` with a flag or make `Dispose` wait for an in-progress stop:

```csharp
private int _shutdownStarted; // 0 = not started

private void Shutdown()
{
    if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
    {
        return; // already running or completed
    }

    // ... existing shutdown logic ...
}
```

Then in `Dispose()`, ensure shutdown completes before disposing resources:

```csharp
public void Dispose()
{
    lock (_lifecycleLock)
    {
        if (_state == ServerState.Disposed) return;
        _state = ServerState.Disposed;
    }

    Shutdown();
    // Shutdown is now guaranteed to have completed (single entry via Interlocked)
    _acceptPool.Dispose();
    _clientRegistry.Dispose();
    _cancellation.Dispose();
}
```

---

### C2. Thread.Start Failure Leaves Server in Inconsistent State

**File:** `Server.cs:147-186`

`ServerStart` sets `_state = Running`, then starts three threads sequentially. If any
`Thread.Start()` throws (e.g., `OutOfMemoryException`), previously-started threads are running but
the server is in a half-initialized state. There is no rollback.

```csharp
_state = ServerState.Running;
_disconnectCleanupThread.Start(); // succeeds
_timeoutThread.Start();           // succeeds
_acceptThread.Start();            // throws → two orphan threads running, no listen socket
```

**Impact:** Orphaned background threads that never terminate (cancellation token never signaled
because `ServerStop` sees `Running` state but the server is non-functional).

**Fix:** Wrap thread startup in try/catch and trigger `Shutdown()` on failure:

```csharp
_state = ServerState.Running;
try
{
    _disconnectCleanupThread.Start();
    if (_socketTimeout > TimeSpan.Zero)
    {
        _timeoutThread.Start();
    }
    _acceptThread.Start();
}
catch (Exception ex)
{
    Logger.Exception(ex);
    _state = ServerState.Stopping;
    // Release the lock before shutdown
    // (restructure to release lock, then call Shutdown)
    Shutdown();
    return;
}
```

Note: Since this calls `Shutdown()`, it must be done outside the lock to avoid deadlock with
`Shutdown`'s own `_lifecycleLock` acquisition. The method would need restructuring — set state under
lock, release lock, start threads, and on failure call `Shutdown()`.

---

## High Severity Issues

### H1. IOCP Thread Pool Starvation from Consumer Callbacks

**Files:** `Server.cs:367-370` (`AcceptCompleted`), `Server.cs:492-508` (`ReceiveCompleted`),
`Server.cs:684-697` (`SendCompleted`)

When `AcceptAsync`/`ReceiveAsync`/`SendAsync` complete asynchronously, the completion callbacks fire
on .NET ThreadPool IOCP threads. These callbacks directly invoke consumer code:

- `ProcessAccept` → `_consumer.OnClientConnected()`
- `ProcessReceive` → `_consumer.OnReceivedData()`

If the consumer performs blocking or long-running work, IOCP threads are held. The .NET ThreadPool
has a limited number of IOCP threads, and starvation here degrades **all** async I/O across the
entire process — not just this server.

**Impact:** Under load with a slow consumer, accept/receive/send throughput collapses for all
connections.

**Recommendation:** This is an architectural tradeoff. Document the contract that `IConsumer`
callbacks **must not block**. Alternatively, for `OnClientConnected` specifically, consider posting
to a dedicated work queue since connection setup is less latency-sensitive than data receive.

---

### H2. Timeout Check Interval Equals Full Timeout Duration

**File:** `Server.cs:886-891`

```csharp
int timeoutMs = Math.Clamp(
    (int)_socketTimeout.TotalMilliseconds,
    MinSocketTimeoutDelayMs,
    MaxSocketTimeoutDelayMs
);
cancellationToken.WaitHandle.WaitOne(timeoutMs);
```

With a 30-second timeout (`ClientSocketTimeoutSeconds = 30`), the check interval is also 30 seconds
(clamped to `MaxSocketTimeoutDelayMs = 30000`). A client that becomes idle at T=1s after a check
won't be detected until T=31s — nearly double the configured timeout.

**Fix:** Use half the timeout or a fixed sub-interval:

```csharp
int timeoutMs = Math.Clamp(
    (int)(_socketTimeout.TotalMilliseconds / 2),
    MinSocketTimeoutDelayMs,
    MaxSocketTimeoutDelayMs
);
```

---

### H3. Synchronous Accept Loop Blocked by Consumer Callback

**File:** `Server.cs:318-365`, `Server.cs:420-432`

When `AcceptAsync` completes synchronously (`willRaiseEvent == false`), `ProcessAccept` runs inline
on the accept thread. This calls `_consumer.OnClientConnected()` and `StartReceive()` —
both on the accept thread. A slow consumer blocks the accept loop, preventing new connections.

The async path has the same problem (H1) but on IOCP threads. On the sync path, it's worse because
there is only **one** accept thread.

**Recommendation:** Same as H1 — enforce that `OnClientConnected` is non-blocking, or offload
connection setup to a separate queue.

---

## Optimization Suggestions

### O1. Per-Receive `byte[]` Allocation — GC Pressure

**File:** `Server.cs:551`

```csharp
byte[] data = GC.AllocateUninitializedArray<byte>(bytesTransferred);
Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
```

Every receive allocates a new array. Under high throughput (e.g., 10K connections each receiving
10 messages/sec = 100K allocations/sec), this creates significant Gen0 GC pressure.

The buffer copy is intentional per architecture, but the allocation source can be optimized:

```csharp
// Option: Use ArrayPool for the copy buffer, require consumer to return it
byte[] data = ArrayPool<byte>.Shared.Rent(bytesTransferred);
Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
// Consumer would need a way to return the buffer (changes IConsumer contract)
```

If changing `IConsumer` is undesirable, consider `Memory<byte>` / `ReadOnlyMemory<byte>` backed by
pooled arrays with a rental pattern. This is a tradeoff between API simplicity and GC pressure.

---

### O2. Per-Send `byte[]` Copy in SendQueue.Enqueue

**File:** `SendQueue.cs:46-47`

```csharp
byte[] copy = GC.AllocateUninitializedArray<byte>(data.Length);
Buffer.BlockCopy(data, 0, copy, 0, data.Length);
```

Same pattern as O1. The copy is architecturally intentional, but pooling the copy buffer via
`ArrayPool<byte>` would reduce allocation pressure. Requires tracking rentals for return after send
completion.

---

### O3. ClientHandle Boxing via UserToken

**Files:** `ClientRegistry.cs:86-87`, `Server.cs:494`, `Server.cs:686`

`ClientHandle` is a `readonly struct`, but `SocketAsyncEventArgs.UserToken` is `object?`. Every
client activation boxes the handle:

```csharp
pooledClient.ReceiveEventArgs.UserToken = handle; // boxes ClientHandle
```

And every callback unboxes:

```csharp
if (eventArgs.UserToken is not ClientHandle clientHandle) // unboxes
```

With high connection churn, this contributes to GC pressure. Consider storing a class wrapper or
using the client ID as the token and looking up the handle from a concurrent structure.

---

### O4. String Interpolation in Logging Hot Paths

**File:** `Server.cs:991-997` and all call sites

```csharp
Logger.Write(level, $"{prefix}{function} - {message}", null);
```

Every `Log()` call performs string interpolation regardless of whether the log level is active. In
high-throughput paths (`ProcessReceive`, `ProcessSend`), the error-path logging is fine, but the
pattern should be verified to not run in success paths. Currently, success paths don't log (good),
but the `Disconnect` path (line 784-789) builds a multi-line interpolated string on every
disconnect.

**Recommendation:** Consider checking log level before formatting in `Disconnect`:

```csharp
if (Logger.IsLevel(LogLevel.Info))
{
    Log(LogLevel.Info, nameof(Disconnect), $"Disconnected...");
}
```

---

## Minor / Informational

### M1. `ClientHandle` Retains Object References After Staleness

**File:** `ClientHandle.cs:13-14`

A stale `ClientHandle` (generation mismatch) still holds references to both `Server` and `Client`,
preventing GC of those objects if the consumer caches handles. Since this is a `readonly struct`,
the references can't be cleared. This is acceptable for the single-run constraint but worth
documenting in the API — consumers should not store `ClientHandle` values long-term after
disconnection.

### M2. `BytesSend` Typo / Duplicate Property

**File:** `ClientHandle.cs:95`

```csharp
public ulong BytesSent => Client.BytesSent;
public ulong BytesSend => Client.BytesSent;  // duplicate with typo
```

`BytesSend` appears to be a typo of `BytesSent`. Both return the same value. Consider removing
`BytesSend` to avoid API confusion.

### M3. `_disconnectCleanupQueue` Re-enqueue Spin

**File:** `Server.cs:832-838`

If `TryFinalizeDisconnect` keeps failing (pending operations never reach 0 due to a stuck SAEA
operation), the cleanup thread re-enqueues the handle every 500ms indefinitely. This won't cause a
crash but wastes CPU. Consider adding a retry limit or escalation (force-close after N retries).

### M4. `CheckSocketTimeout` List Reuse Pattern

**File:** `Server.cs:848`

```csharp
List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);
```

The list is pre-allocated with `MaxConnections` capacity. `SnapshotActiveHandles` calls `Clear()`
then `AddRange()`, which is efficient since capacity is preserved. No issue — noting for
completeness.

---

## Summary Table

| ID | Severity | Category | Summary |
|----|----------|----------|---------|
| C1 | Critical | Thread Safety | `Dispose`/`ServerStop` race — use-after-dispose of shared resources |
| C2 | Critical | Logic | `Thread.Start` failure leaves orphaned threads, no rollback |
| H1 | High | Scalability | IOCP thread starvation from consumer callbacks on async path |
| H2 | High | Logic | Timeout check interval equals full timeout, up to 2x detection delay |
| H3 | High | Scalability | Accept thread blocked by synchronous consumer callback |
| O1 | Medium | GC Pressure | Per-receive `byte[]` allocation under high throughput |
| O2 | Medium | GC Pressure | Per-send `byte[]` copy allocation |
| O3 | Low | GC Pressure | `ClientHandle` struct boxing via `UserToken` |
| O4 | Low | GC Pressure | String interpolation in disconnect logging |
| M1 | Info | Resource | Stale `ClientHandle` retains object references |
| M2 | Info | API | `BytesSend` duplicate/typo property |
| M3 | Info | Resilience | Cleanup re-enqueue has no retry limit |
