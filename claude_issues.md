# Server.cs Technical Audit

Scope reviewed:
- `SAEAServer/Server.cs`
- `SAEAServer/AcceptPool.cs`
- `SAEAServer/ClientRegistry.cs`
- `SAEAServer/Client.cs`
- `SAEAServer/ClientHandle.cs`
- `SAEAServer/SendQueue.cs`
- `SAEAServer/BufferSlab.cs`
- `SAEAServer/ClientSnapshot.cs`
- `Service.cs`

---

## Critical Issues

### CRIT-1: `Disconnect` can permanently leak a client slot when `_pendingOperations > 0`

**Evidence**
- `Disconnect` calls `client.Close()` (sets `_isAlive = false`, closes socket), then calls `_clientRegistry.TryDeactivateClient` (`Server.cs:734-735`).
- `TryDeactivateClient` calls `client.CanReturnToPool()` (`ClientRegistry.cs:103`).
- `CanReturnToPool` returns `false` when `_pendingOperations > 0` (`Client.cs:197`).
- When `CanReturnToPool` returns `false`, `TryDeactivateClient` returns `false` -- the client is **never returned to the pool** and **never removed from `_activeHandles`**.

**Why this is critical**
- After `Close()`, the socket is shut down and `_isAlive` is `false`. Pending async completions will fire, call `DecrementPendingOperations`, discover `!IsAlive`, and call `Disconnect` again. But the second `Disconnect` call reaches `TryGetClient` which checks generation -- if `ResetForPool` never runs (because `CanReturnToPool` was never re-checked), the handle remains valid.
- The second `Disconnect` calls `Close()` again (no-op since `_isAlive` is already `false`), then `TryDeactivateClient` again. This time `_pendingOperations` may be 0, so it succeeds -- **but only if the race works out**.
- In the **normal non-racy case**, this works because the last I/O completion triggers a second `Disconnect` after pending ops drain. However, if `Disconnect` is called from the timeout thread _while_ a receive or send completion is also in-flight, there is a window where:
  1. Timeout thread: `Disconnect` -> `Close()` succeeds -> `TryDeactivateClient` -> `CanReturnToPool` returns `false` (pending ops = 1)
  2. I/O completion thread: `ProcessReceive` -> `DecrementPendingOperations` -> sees `!IsAlive` -> calls `Disconnect`
  3. Second `Disconnect`: `TryGetClient` -> succeeds (generation unchanged) -> `Close()` no-op -> `TryDeactivateClient` -> `CanReturnToPool` returns `true` -> slot returned

  This works _if_ step 2 happens. But if the I/O completion already ran `DecrementPendingOperations` **before** step 1 (so pending ops were already 0 at step 1), then `TryDeactivateClient` succeeds at step 1 and step 2's `Disconnect` sees a stale handle. This path is fine.

  The **actual leak** scenario: if `Disconnect` is called while pending ops > 0 and then `Close()` causes the OS to silently drop the completion (no callback fires -- e.g., the `SocketAsyncEventArgs` was never actually posted because `TryBeginSocketOperation` succeeded but `ReceiveAsync` threw and the catch block decremented but another path incremented), the second `Disconnect` never comes and the slot is permanently leaked.

**Severity**: Medium-High. Under normal operation the two-phase disconnect works, but edge cases around exception paths in `StartReceive`/`StartSend` where `DecrementPendingOperations` runs but no further `Disconnect` triggers can strand a slot.

**Recommendation**: Add a deferred return mechanism: after `Close()`, if `CanReturnToPool` fails, schedule a callback or use a sweep in `CheckSocketTimeout` to reclaim slots where `!IsAlive && !IsInPool && PendingOperations == 0`.

---

### CRIT-2: `AcceptPool.Return` after `AcceptPool.Dispose` throws unhandled `ObjectDisposedException`

**Evidence**
- `ProcessAccept` calls `_acceptPool.Return(acceptEventArgs)` (`Server.cs:364`).
- `AcceptPool.Return` calls `_capacityGate.Release()` (`AcceptPool.cs:93`).
- `Dispose` -> `Shutdown` closes the listen socket, cancels the CTS, disconnects clients, joins threads. Then `Dispose` calls `_acceptPool.Dispose()` (`Server.cs:875`), which disposes `_capacityGate` (`AcceptPool.cs:109`).
- A pending `AcceptAsync` completion can fire on an IOCP thread **after** `_capacityGate` is disposed, causing `ObjectDisposedException` in `SemaphoreSlim.Release`.

**Why this is critical**
- The exception is unhandled on an IOCP thread, which will crash the process via `UnobservedTaskException` or `AppDomain.UnhandledException`.

**Recommendation**: Guard `Return` against post-dispose calls:
```csharp
internal void Return(SocketAsyncEventArgs eventArgs)
{
    eventArgs.AcceptSocket = null;
    eventArgs.UserToken = null;

    lock (_sync)
    {
        _availableEventArgs.Push(eventArgs);
    }

    try
    {
        _capacityGate.Release();
    }
    catch (ObjectDisposedException)
    {
        // Pool already disposed; late completion after shutdown.
    }
}
```

---

### CRIT-3: `SendQueue.CompleteSend` is called without `Client._sync` lock -- race with `QueueSend`

**Evidence**
- `Client.CompleteSend` (`Client.cs:261-264`) delegates directly to `_sendQueue.CompleteSend(transferredCount)` **without** acquiring `_sync`.
- `Client.QueueSend` (`Client.cs:246-258`) calls `_sendQueue.Enqueue` **under** `_sync`.
- `Client.TryPrepareSendChunk` (`Client.cs:224-243`) calls `_sendQueue.CopyNextChunk` **under** `_sync`.
- `SendQueue.CompleteSend` acquires its own `_sync` (`SendQueue.cs:89`), so the `SendQueue` internal state is consistent. However, `Client.CompleteSend` is called from `ProcessSend` (`Server.cs:714`), and the return value drives whether `StartSend` continues looping.

**Analysis**: This is actually safe because `SendQueue` has its own lock. The two lock domains (`Client._sync` and `SendQueue._sync`) do not create a deadlock because `Client` never holds its `_sync` while calling `CompleteSend`. However, `CompleteSend` not being under `Client._sync` means there is a window between `CompleteSend` returning `false` (no more data) and a concurrent `QueueSend` setting `startSend = true` for the newly enqueued data. Let's trace:

1. Thread A: `CompleteSend` acquires `SendQueue._sync`, sees no pending messages, sets `_sendInProgress = false`, returns `false`.
2. Thread B: `QueueSend` acquires `Client._sync` then `SendQueue._sync`, sees `_sendInProgress == false`, sets it to `true`, sets `startSend = true`, returns.
3. Thread B: `Send` sees `startSend == true`, calls `StartSend`.
4. Thread A: `ProcessSend` returns `false`, send loop ends.

This is correct -- Thread B will kick the send pump. No data is lost.

**Verdict**: Not a bug. The `SendQueue._sync` lock is sufficient. No action needed.

---

### CRIT-4: `Shutdown` called from `ServerStop` while `_lifecycleLock` is NOT held -- but `Run` thread may call `Shutdown` concurrently

**Evidence**
- `ServerStop` sets `_state = ServerState.Stopping` under lock, then calls `Shutdown()` outside the lock (`Server.cs:201-206`).
- `Run` thread, if `TrySocketListen` fails, also sets `_state = ServerState.Stopping` under lock, then calls `Shutdown()` (`Server.cs:218-233`).
- Both paths can execute `Shutdown` concurrently if the timing aligns (user calls `ServerStop` right as `TrySocketListen` fails).

**Impact**:
- `Shutdown` closes the listen socket under lock (idempotent due to null-check), cancels the CTS (idempotent in .NET), snapshots active handles and disconnects them (concurrent disconnect calls are guarded by `Client.Close` idempotency and `TryDeactivateClient` generation checks), and joins threads (safe to join from multiple threads -- second join sees thread not alive).
- `_state` is set to `ServerState.Stopped` under lock with a guard (`Server.cs:855`), so double-set is harmless.

**Verdict**: The concurrent `Shutdown` calls are effectively safe due to idempotent operations throughout. Low risk. Could add an `Interlocked.CompareExchange` flag to run `Shutdown` exactly once for cleanliness, but not strictly necessary.

---

## Moderate Issues

### MOD-1: `_activeHandles.Remove(clientHandle)` is O(n) per disconnect

**Evidence**: `ClientRegistry.TryDeactivateClient` (`ClientRegistry.cs:118`) calls `_activeHandles.Remove(clientHandle)`, which is `List<T>.Remove` -- an O(n) scan.

**Impact**: Under high churn (many connections connecting/disconnecting), this becomes a bottleneck since it runs under `_sync` lock, blocking all activations and deactivations. With `MaxConnections` at the default 100 this is negligible, but at 10K+ connections it becomes measurable.

**Recommendation**: Replace `List<ClientHandle>` with a `Dictionary<int, ClientHandle>` keyed by `ClientId`, or use a swap-with-last removal pattern on the list. Alternatively, since `Client` objects have a stable `ClientId`, use a `BitArray` or parallel array for O(1) add/remove and O(n) snapshot.

---

### MOD-2: `CheckSocketTimeout` allocates a new `List<ClientHandle>` at startup and reuses it, but `SnapshotActiveHandles` calls `AddRange` which may resize

**Evidence**: The list is pre-allocated with capacity `MaxConnections` (`Server.cs:773`), and `SnapshotActiveHandles` calls `Clear()` then `AddRange()` (`ClientRegistry.cs:133-134`). Since active handles never exceed `MaxConnections`, the list never resizes after initial allocation.

**Verdict**: No issue -- correct pre-allocation.

---

### MOD-3: `ClientHandle` generation check is non-atomic with respect to `Client` state

**Evidence**: `ClientHandle.TryGetClient` (`ClientHandle.cs:49-60`) reads `_client.Generation` without a lock. `Client.Generation` acquires `_sync` (`Client.cs:64`). But between `TryGetClient` returning `true` and the caller using the `Client`, another thread could call `ResetForPool` which bumps the generation.

**Impact**: This is a fundamental TOCTOU issue, but it is **by design** -- the generation check is a best-effort staleness detector, and all `Client` methods that mutate state have their own lock. The worst case is a stale handle passes the generation check, then the operation sees `!_isAlive` and returns gracefully. This is acceptable.

---

## Optimization Suggestions

### OPT-1: Per-receive `byte[]` allocation creates GC pressure

**Location**: `Server.cs:539`
```csharp
byte[] data = new byte[bytesTransferred];
Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
```

Per the project constraints (intentional copy for isolation), this allocation is required. However:
- Use `GC.AllocateUninitializedArray<byte>(bytesTransferred)` instead of `new byte[bytesTransferred]` to skip zeroing memory that will be immediately overwritten. This is already done in `SendQueue.Enqueue` but not here.

```csharp
byte[] data = GC.AllocateUninitializedArray<byte>(bytesTransferred);
Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
```

---

### OPT-2: `StartAccept` tight-loops on `SocketException` without backoff

**Location**: `Server.cs:341-346`
```csharp
catch (SocketException exception)
{
    Logger.Exception(exception);
    _acceptPool.Return(acquiredEventArgs);
    continue;  // immediate retry
}
```

A transient socket error (e.g., EMFILE/too many open files) will spin the accept thread at 100% CPU, flooding logs.

**Recommendation**: Add a bounded delay on consecutive errors:
```csharp
catch (SocketException exception)
{
    Logger.Exception(exception);
    _acceptPool.Return(acquiredEventArgs);
    cancellationToken.WaitHandle.WaitOne(100); // brief backoff
    continue;
}
```

---

### OPT-3: Timeout scan interval doesn't scale with connection count

**Location**: `Server.cs:811-816`

The scan runs every `clamp(socketTimeout, 1s, 30s)` regardless of how many clients are active. For servers with very long timeouts (e.g., 3600s) and thousands of clients, scanning all clients every 30 seconds is wasteful. Conversely, for very short timeouts (e.g., 5s), scanning every 5s is reasonable.

**Recommendation**: This is acceptable for the current design. A timing wheel would be more efficient at scale but adds significant complexity. No change recommended unless connection counts exceed ~50K.

---

### OPT-4: `SocketSettings` is not shown but `ConfigureSocket` is called on accepted sockets in the hot path

**Location**: `Server.cs:399`

If `ConfigureSocket` makes syscalls (setsockopt), this is unavoidable. Just noting it as a measured cost per accept.

---

## Code Refactor Suggestions

### REF-1: Guard `AcceptPool.Return` against post-dispose crashes

```csharp
// AcceptPool.cs - Return method
internal void Return(SocketAsyncEventArgs eventArgs)
{
    eventArgs.AcceptSocket = null;
    eventArgs.UserToken = null;

    lock (_sync)
    {
        _availableEventArgs.Push(eventArgs);
    }

    try
    {
        _capacityGate.Release();
    }
    catch (ObjectDisposedException)
    {
        // Late completion after shutdown -- safe to ignore.
    }
}
```

---

### REF-2: Add stale-slot reclamation to `CheckSocketTimeout`

```csharp
// Server.cs - inside CheckSocketTimeout, after the timeout-disconnect loop:
// Reclaim slots that were closed but couldn't return to pool due to pending ops.
foreach (ClientHandle clientHandle in handles)
{
    if (!clientHandle.TryGetClient(out Client client))
    {
        continue;
    }

    if (!client.IsAlive && client.PendingOperations == 0)
    {
        _clientRegistry.TryDeactivateClient(clientHandle, out _);
    }
}
```

---

### REF-3: Use `GC.AllocateUninitializedArray` for receive buffer copy

```csharp
// Server.cs:539 - ProcessReceive
byte[] data = GC.AllocateUninitializedArray<byte>(bytesTransferred);
Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
```

---

### REF-4: Single-flight `Shutdown` via atomic flag (optional cleanliness)

```csharp
// Server.cs - add field:
private int _shutdownExecuted;

// Server.cs - Shutdown method, first line:
private void Shutdown()
{
    if (Interlocked.Exchange(ref _shutdownExecuted, 1) != 0)
    {
        return; // Another thread is already executing shutdown.
    }
    // ... rest of Shutdown
}
```

This eliminates any concern about concurrent `Shutdown` execution from `ServerStop` + failed `Run`.

---

## Summary

| ID | Severity | Category | Description |
|----|----------|----------|-------------|
| CRIT-1 | Medium-High | Resource Leak | Client slot leak when `Disconnect` races with pending I/O ops and completion never fires |
| CRIT-2 | High | Crash | `AcceptPool.Return` after dispose causes unhandled `ObjectDisposedException` on IOCP thread |
| CRIT-3 | N/A (False alarm) | Thread Safety | `CompleteSend` without `Client._sync` -- analyzed as safe |
| CRIT-4 | Low | Thread Safety | Concurrent `Shutdown` calls -- analyzed as safe due to idempotent operations |
| MOD-1 | Medium | Scalability | O(n) `_activeHandles.Remove` under lock |
| OPT-1 | Low | GC Pressure | Use `AllocateUninitializedArray` for receive copies |
| OPT-2 | Medium | CPU/Logs | Accept loop tight-spins on `SocketException` |
| REF-4 | Low | Cleanliness | Atomic single-flight `Shutdown` guard |
