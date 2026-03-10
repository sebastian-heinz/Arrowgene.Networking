# Server.cs Technical Audit

Audit of `SAEAServer/Server.cs` and supporting types on `feat/pass-4`.
**Premise:** Restart (`Stop` → `Start`) is a supported and desired property.
Reviewed against AGENTS.md constraints: intentional buffer copy, pooled resources.

---

## Previously Identified — Now Resolved

| ID | Issue | Status |
|----|-------|--------|
| C1 (old) | `Dispose`/`Stop` race — use-after-dispose | **Fixed.** `_shutdownStarted` CAS + `_shutdownCompleted.Wait()` ensures single-entry shutdown and `Dispose` waits before disposing resources. |
| C2 (old) | `Thread.Start` failure leaves orphaned threads | **Fixed.** `Start()` wraps thread startup in try/catch, transitions to `Stopping`, and calls `Shutdown()` on failure. |

---

## Critical Issues

### C1. Surviving Threads Access Disposed `CancellationTokenSource` — Process Crash

**Files:** `Server.cs:864-880` (`CleanupDisconnectedClients`), `Server.cs:882-929`
(`CheckSocketTimeout`), `Server.cs:964-980` (`Shutdown`)

Both background threads capture `_cancellation.Token` at method entry and call
`cancellationToken.WaitHandle.WaitOne(...)` each iteration. In .NET 6+, accessing
`CancellationToken.WaitHandle` after the source `CancellationTokenSource` is disposed throws
`ObjectDisposedException`.

Shutdown sequence:

```
_cancellation.Cancel();                                   // line 957
Service.JoinThread(_acceptThread, 10_000);                // line 964
WaitForShutdownQuiescence();                              // line 965
Service.JoinThread(_timeoutThread, 10_000);               // line 966
Service.JoinThread(_disconnectCleanupThread, 10_000);     // line 967
WaitForShutdownQuiescence();                              // line 968
// ...
_cancellation.Dispose();                                  // line 980
```

If a thread join times out (10s), the thread survives. The CTS is then disposed. The surviving
thread eventually reaches `cancellationToken.WaitHandle.WaitOne(...)` — the `.WaitHandle`
property accessor hits the disposed CTS internals → `ObjectDisposedException`. This is an
**unhandled exception on a background thread**, which terminates the process in .NET 6+.

**Race window:** Even without join timeout, there's a narrow TOCTOU: thread checks
`IsCancellationRequested` (false, not yet cancelled) → enters loop body → `Cancel()` +
`Dispose()` fire → thread reaches `WaitHandle.WaitOne()` → crash. After `Cancel()`, the
WaitHandle is signaled so `WaitOne` would return immediately, but the `.WaitHandle` *property
accessor* itself throws if the CTS is already disposed.

**Impact:** Process crash during shutdown or restart cycle.

**Fix:** Wrap the wait in a try/catch, or don't dispose the CTS during Shutdown (let `Dispose()`
handle it):

```csharp
// Option A: Remove CTS dispose from Shutdown, let Dispose() handle it
private void Shutdown()
{
    // ...
    // Remove: _cancellation.Dispose();  ← line 980
    // The CTS is disposed in Dispose() at line 1008
    // For restart, RecreateRunResources replaces _cancellation;
    // the old CTS is unreachable but GC-safe (finalizer handles it)
    // ...
}
```

```csharp
// Option B: Guard WaitHandle access in thread methods
while (true)
{
    // ... work ...
    try
    {
        cancellationToken.WaitHandle.WaitOne(DelayMs);
    }
    catch (ObjectDisposedException)
    {
        return;
    }
}
```

Option A is cleaner — it avoids the CTS being disposed while threads may still reference it.
For restarts, `RecreateRunResources` overwrites `_cancellation` with a new instance; the old
disposed-or-not CTS becomes unreachable and is collected.

---

### C2. `WaitForShutdownQuiescence` Has No Timeout — `Stop()` Can Hang Indefinitely

**File:** `Server.cs:1083-1099`

```csharp
private void WaitForShutdownQuiescence()
{
    while (true)   // ← no exit condition other than IsShutdownQuiescent
    {
        DisconnectActiveClients();
        ProcessDisconnectCleanupQueue(handles, "DeferredCleanup");
        if (IsShutdownQuiescent(handles)) return;
        _asyncCallbacksDrained.Wait(DisconnectCleanupDelayMs);
    }
}
```

If a consumer callback blocks (e.g., `OnReceivedData` hangs), `_inFlightAsyncCallbacks` stays
\> 0 indefinitely. `IsShutdownQuiescent` returns `false` forever. `Stop()` never returns, making
restart impossible.

Similarly, if a client has a pending SAEA operation that never completes (kernel-level socket
stuck), `CanReturnToPool()` returns `false` because `_pendingOperations > 0`, and the client
slot can never be deactivated.

**Impact:** `Stop()` hangs, blocking the calling thread. Restart is impossible. On a server with
a management API, this freezes the control plane.

**Fix:** Add a deadline with escalation:

```csharp
private void WaitForShutdownQuiescence()
{
    List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);
    long deadline = Environment.TickCount64 + _settings.ShutdownTimeoutMs; // e.g., 30_000

    while (true)
    {
        DisconnectActiveClients();
        ProcessDisconnectCleanupQueue(handles, "DeferredCleanup");

        if (IsShutdownQuiescent(handles))
        {
            return;
        }

        if (Environment.TickCount64 >= deadline)
        {
            Log(LogLevel.Error, nameof(WaitForShutdownQuiescence),
                $"Shutdown quiescence timeout. Active:{handles.Count} " +
                $"InFlight:{Volatile.Read(ref _inFlightAsyncCallbacks)}");

            // Force-return remaining clients to pool despite pending ops
            ForceReturnLeakedClients(handles);
            return;
        }

        _asyncCallbacksDrained.Wait(DisconnectCleanupDelayMs);
    }
}
```

Without this, the restart property is aspirational — a single stuck consumer callback makes
it unreachable.

---

### C3. Client Pool Slot Leak Across Restarts (Consequence of C2 Fix)

**Files:** `Server.cs:1083-1099`, `ClientRegistry.cs:93-121`

If `WaitForShutdownQuiescence` is given a timeout (to fix C2), clients that couldn't be
deactivated remain in `_activeHandles` and are never returned to `_availableClients`.
`RecreateRunResources` does not touch `_clientRegistry`. On restart, the pool has fewer
available slots. Over multiple restart cycles, this compounds until `TryActivateClient` always
fails — the server accepts connections but immediately rejects them all.

```
Restart 1: 1000/1000 slots available
Restart 2:  998/1000 (2 leaked from stuck callbacks)
Restart 3:  995/1000
...
Restart N:    0/1000 → server is functionally dead
```

**Impact:** Progressive capacity degradation across restart cycles.

**Fix:** After the quiescence timeout, force-reset leaked clients:

```csharp
private void ForceReturnLeakedClients(List<ClientHandle> handles)
{
    _clientRegistry.SnapshotActiveHandles(handles);
    foreach (ClientHandle handle in handles)
    {
        if (handle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(ForceReturnLeakedClients),
                $"Force-recycling leaked client slot.", client.Identity);
            // Reset pending ops to 0 so CanReturnToPool succeeds
            client.ForceResetPendingOperations();
            _clientRegistry.TryDeactivateClient(handle, out _);
        }
    }
}
```

This requires adding a `ForceResetPendingOperations()` method to `Client` that zeroes the
counter under lock. It's a last-resort escalation — data integrity for the stuck connection
is already lost at this point.

---

## High Severity Issues

### H1. IOCP Thread Pool Starvation from Consumer Callbacks

**Files:** `Server.cs:393-404`, `Server.cs:526-550`, `Server.cs:731-752`

Async completions fire on ThreadPool IOCP threads and directly invoke consumer code
(`OnClientConnected`, `OnReceivedData`). A blocking consumer starves the IOCP pool, degrading
all async I/O across the entire process.

**Recommendation:** Document that `IConsumer` callbacks **must not block**. Consider posting
`OnClientConnected` to a dedicated queue.

---

### H2. Timeout Check Interval Equals Full Timeout Duration

**File:** `Server.cs:923-928`

```csharp
int timeoutMs = Math.Clamp(
    (int)_socketTimeout.TotalMilliseconds,
    MinSocketTimeoutDelayMs,
    MaxSocketTimeoutDelayMs
);
```

With a 30s timeout, the check runs every 30s. Worst-case detection delay is ~2x the configured
timeout.

**Fix:** Use half the timeout:

```csharp
int timeoutMs = Math.Clamp(
    (int)(_socketTimeout.TotalMilliseconds / 2),
    MinSocketTimeoutDelayMs,
    MaxSocketTimeoutDelayMs
);
```

---

### H3. Synchronous Accept Path Blocks Accept Thread

**Files:** `Server.cs:344-391`, `Server.cs:406-469`

When `AcceptAsync` completes synchronously, `ProcessAccept` runs inline on the single accept
thread, including `_consumer.OnClientConnected()` and `StartReceive()`. A slow consumer blocks
all new accepts.

**Recommendation:** Enforce non-blocking consumer contract or offload synchronous-path accepts.

---

### H4. `ProcessSend` Error Path Skips `CompleteSend` — Stale SendQueue State

**File:** `Server.cs:754-796`

On error (socket error / zero bytes), `ProcessSend` calls `Disconnect` but never calls
`_sendQueue.CompleteSend()`. The queue's `_sendInProgress` flag and `_currentMessage` remain
set. This is cleaned up by `ResetForPool()` when the client is deactivated, but during the
deferred cleanup window a racing `Send()` call could see `_sendInProgress == true` and silently
not start the send pump.

**Severity:** Medium — no data corruption, but potential for a send to be silently dropped
during the disconnect grace period.

---

### H5. `EnterAsyncCallback` / `ExitAsyncCallback` — MRES Lost-Signal Race

**File:** `Server.cs:1056-1070`

```csharp
private void EnterAsyncCallback()
{
    if (Interlocked.Increment(ref _inFlightAsyncCallbacks) == 1)
        _asyncCallbacksDrained.Reset();
}

private void ExitAsyncCallback()
{
    if (Interlocked.Decrement(ref _inFlightAsyncCallbacks) == 0)
        _asyncCallbacksDrained.Set();
}
```

The counter update and MRES signal are not atomic:

```
Thread A (exit):  Decrement → 0    (about to Set)
Thread B (enter): Increment → 1  → Reset()
Thread A:                           Set()   ← spurious: B is in-flight
```

Mitigated by `IsShutdownQuiescent` re-checking the counter. But the correctness of shutdown
depends on that re-check. If `IsShutdownQuiescent` is ever refactored to trust the MRES alone,
shutdown can escape with in-flight callbacks.

**Fix:** Replace MRES with a spin-wait on the counter, or gate both operations under a lock.

---

### H6. `RecreateRunResources` Does Not Clear `_disconnectCleanupQueue`

**File:** `Server.cs:1158-1168`

`RecreateRunResources` resets threads, CTS, counters, and events — but not the
`_disconnectCleanupQueue`. `Shutdown`'s `WaitForShutdownQuiescence` should drain it, but if a
quiescence timeout is added (C2 fix), stale handles could carry into the next lifecycle. The new
cleanup thread would process them, and generation checks would reject them, but it's wasted work
and a correctness hazard if the generation wraps (uint overflow after ~4B activations per slot).

**Fix:** Add to `RecreateRunResources`:

```csharp
lock (_disconnectCleanupSync)
{
    _disconnectCleanupQueue.Clear();
}
```

---

## Optimization Suggestions

### O1. Per-Receive `byte[]` Allocation — GC Pressure

**File:** `Server.cs:593-594`

Every receive allocates a `byte[]`. At scale (10K connections × 10 msg/sec), this generates
~100K Gen0 allocations/sec. The copy is intentional (buffer isolation). Consider
`ArrayPool<byte>.Shared.Rent()` with a return protocol, or accept the cost for API simplicity.

---

### O2. Per-Send `byte[]` Copy in `SendQueue.Enqueue`

**File:** `SendQueue.cs:46-47`

Same allocation pattern as O1. Pooling would reduce GC pressure but requires tracking rentals
through `CompleteSend`.

---

### O3. `ClientHandle` Boxing via `SocketAsyncEventArgs.UserToken`

**Files:** `ClientRegistry.cs:86-87`, `Server.cs:531`, `Server.cs:736`

`ClientHandle` is a struct but `UserToken` is `object?` — boxing on every activation, unboxing
on every callback. Store the `Client` (class) directly as `UserToken` and reconstruct the
`ClientHandle` from it:

```csharp
pooledClient.ReceiveEventArgs.UserToken = pooledClient; // no boxing

// In callback:
if (eventArgs.UserToken is not Client client) { ... }
ClientHandle clientHandle = new ClientHandle(this, client);
```

---

### O4. String Interpolation in Disconnect Logging

**File:** `Server.cs:836-845`

Multi-line interpolated string built on every disconnect. Guard with log-level check under high
churn.

---

## Minor / Informational

### M1. `ClientHandle` Retains References After Staleness

**File:** `ClientHandle.cs:13-14`

Stale handles hold `Server` + `Client` references, preventing GC if consumers cache them.
Document that handles should not be stored after disconnection.

### M2. `BytesSend` Duplicate Property

**File:** `ClientHandle.cs:111`

`BytesSend` is a typo of `BytesSent`. Remove to avoid API confusion.

### M3. Disconnect Cleanup Re-enqueue Has No Retry Limit

**File:** `Server.cs:1130-1155`

Handles that can't be finalized are re-enqueued every 500ms indefinitely. Add a retry limit.

### M4. `_cancellation.Dispose()` Called Twice

**Files:** `Server.cs:980`, `Server.cs:1008`

`Shutdown()` and `Dispose()` both dispose the CTS. Idempotent but redundant. If C1 fix is
applied (remove dispose from Shutdown), this is resolved.

### M5. AGENTS.md Does Not Reflect Restart Support

**File:** `AGENTS.md:22`

States *"lifecycle should be start once and stop once"* but the implementation supports restart.
Update documentation to match the design.

---

## Summary Table

| ID | Severity | Category | Summary |
|----|----------|----------|---------|
| C1 | Critical | Thread Safety | Surviving threads access disposed CTS WaitHandle → process crash |
| C2 | Critical | Liveness | `WaitForShutdownQuiescence` has no timeout → `Stop()` hangs, restart impossible |
| C3 | Critical | Resource Leak | Client pool slots leak across restart cycles (compounds with C2 fix) |
| H1 | High | Scalability | IOCP thread starvation from blocking consumer callbacks |
| H2 | High | Logic | Timeout check interval = full timeout → 2x detection delay |
| H3 | High | Scalability | Accept thread blocked by synchronous consumer callback |
| H4 | High | Logic | `ProcessSend` error path leaves `SendQueue` state dirty |
| H5 | High | Thread Safety | MRES lost-signal race in async callback tracking (mitigated by counter re-check) |
| H6 | High | Restart | `RecreateRunResources` doesn't clear `_disconnectCleanupQueue` |
| O1 | Medium | GC Pressure | Per-receive `byte[]` allocation |
| O2 | Medium | GC Pressure | Per-send `byte[]` copy allocation |
| O3 | Low | GC Pressure | `ClientHandle` struct boxing via `UserToken` |
| O4 | Low | GC Pressure | String interpolation in disconnect logging |
| M1 | Info | Resource | Stale `ClientHandle` retains object references |
| M2 | Info | API | `BytesSend` duplicate/typo property |
| M3 | Info | Resilience | Cleanup re-enqueue has no retry limit |
| M4 | Info | Cleanup | Double CTS dispose (resolved if C1 fix applied) |
| M5 | Info | Documentation | AGENTS.md doesn't reflect restart support |
