# AsyncEventServer Code Review — Issues

Review scope: `AsyncEventServer.cs` and all supporting types
(`AsyncEventClient`, `AsyncEventClientHandle`, `AsyncEventClientRegistry`,
`AsyncEventSendQueue`, `AsyncEventAcceptPool`, `AsyncEventBufferSlab`,
`AsyncEventSettings`).

Focus: locking, synchronization, and logic correctness.

---

## Issue 1 — Double Disconnect on Receive Error Paths (Logic Bug)

**Severity:** Medium
**File:** `AsyncEventServer.cs`

`ProcessReceive` calls `Disconnect` on every error path **and** returns
`false`:

- Line 443: socket error → `Disconnect(); return false;`
- Line 456: zero bytes → `Disconnect(); return false;`
- Line 465: null buffer → `Disconnect(); return false;`

Both callers then **also** call `Disconnect` when they see `false`:

- `ReceiveCompleted` (line 424): `Disconnect(clientHandle);`
- `StartReceive` (line 403): `Disconnect(clientHandle);`

This produces a double `Disconnect` for every receive-side error.

The `ProcessReceive` method has a mixed return contract: on the normal
completion path (line 487 `return client.IsAlive;`), returning `false` means
"the client died elsewhere, caller should disconnect." But on error paths,
returning `false` means "I already disconnected." The caller cannot distinguish
the two.

**Impact:**
Not a crash — `Close()` is idempotent (`_isAlive` guard), and
`TryDeactivateClient` returns `false` on the redundant call (either
`CanReturnToPool` fails because `_isInPool` is already `true`, or generation
mismatch if the slot was re-activated).  However, every duplicate `Disconnect`
call acquires multiple locks, performs identity/stats reads, and logs
unnecessarily.

**Note:** The **send path does not have this issue.** `SendCompleted` (line
609-613) and `StartSend` (line 594-596) do **not** call `Disconnect` when
`ProcessSend` returns `false` — they simply stop the send chain. `ProcessSend`
is the sole owner of disconnect on the send side, which is the correct
pattern.

**Suggested fix:**
Remove the `Disconnect` calls inside `ProcessReceive` (lines 443, 456, 465)
and let the callers be the sole owners, consistent with how the send path
works.  Alternatively, keep `Disconnect` inside `ProcessReceive` and change
callers to not disconnect on `false` (matching the send path pattern).

---

## Issue 2 — `OnClientDisconnected` Consumer Callback Receives Wiped State (Logic Bug)

**Severity:** Medium
**File:** `AsyncEventServer.cs` lines 674-697, `AsyncEventClientRegistry.cs`
lines 117-125

In `Disconnect`, the flow is:

```
client.Close();                              // line 674
_clientRegistry.TryDeactivateClient(...)     // line 675
    → client.CanReturnToPool()               //   sets _isInPool = true
    → reads client.UnitOfOrder               //   reads correct value
    → client.ResetForPool()                  //   wipes Identity, stats, UnitOfOrder, etc.
    → pushes client back to available pool
    → removes handle from active list
// ... log disconnect stats using captured locals (correct) ...
_consumer.OnClientDisconnected(clientHandle) // line 697
```

The handle passed to `OnClientDisconnected` still resolves (generation is only
bumped in `Activate`, not `ResetForPool`).  But the consumer will see:

- `clientHandle.Identity` → `"[Unknown Client]"`
- `clientHandle.BytesReceived` → `0`
- `clientHandle.BytesSent` → `0`
- `clientHandle.ConnectedAt` → `DateTime.MinValue`

The server captures the correct stats into locals (lines 669-672) for its own
log message, but these are not forwarded to the consumer.

**Acknowledged:** The TODO on line 697 notes this:
`// TODO pass a new object, that is a client snapshot`

**Impact:**
Any consumer that reads session stats inside `OnClientDisconnected` gets
zeroed-out values.

**Suggested fix:**
Create a snapshot struct/object from the captured locals and pass it to the
consumer, or defer `ResetForPool` until after the callback.

---

## Issue 3 — Shutdown Dispose Race With In-Flight IOCP Accept Callbacks (Concurrency Bug)

**Severity:** Low
**File:** `AsyncEventServer.cs` lines 760-806, `AsyncEventAcceptPool.cs`

During `Dispose()`, the sequence is:

1. `Shutdown()` closes the listen socket (line 768) — this causes pending
   `AcceptAsync` operations to complete with errors on IOCP thread pool threads
2. `Shutdown()` joins the accept thread (line 785) — the accept thread exits
3. `Dispose()` calls `_acceptPool.Dispose()` (line 802) — disposes
   `_capacityGate` (SemaphoreSlim)

The IOCP completion callbacks for pending accepts fire on thread pool threads,
not the accept thread.  If a callback fires **after** step 3:

- `AcceptCompleted` → `ProcessAccept` → `_acceptPool.Return(eventArgs)`
- `Return` calls `_capacityGate.Release()` → `ObjectDisposedException`

This is unhandled and would crash the process.

**Impact:**
Extremely narrow race window.  In practice, IOCP callbacks fire almost
immediately after the listen socket is closed, and the thread joins in step 2
provide an implicit delay.  Thread pool starvation could widen the window.

**Suggested fix:**
Add a disposed/shutdown check at the top of `ProcessAccept` or `Return`, or
cancel and wait for all outstanding accept operations before disposing the
pool.

---

## Issue 4 — Redundant Reentrant Lock Acquisition in `Shutdown` (Code Smell)

**Severity:** Info
**File:** `AsyncEventServer.cs` lines 760-787

`Shutdown()` acquires `_lifecycleLock` at line 762.  It is only ever called
from:

- `ServerStop()` — which already holds `_lifecycleLock` (line 142)
- `Dispose()` — which already holds `_lifecycleLock` (line 791)
- `Run()` — which already holds `_lifecycleLock` (line 168)

C# `Monitor` is reentrant, so this doesn't deadlock, but the inner lock
is always a no-op.  More importantly, lines 768-786 (close listen socket,
cancel CTS, disconnect clients, join threads) appear to be outside a lock
block but are actually protected by the caller's lock scope — this is
structurally misleading.

**Suggested fix:**
Remove the inner lock from `Shutdown` and add a comment that it must be called
under `_lifecycleLock`, or pass the lock responsibility more explicitly.

---

## Summary

| # | Issue | Severity | Type |
|---|---|---|---|
| 1 | Double `Disconnect` on receive error paths | Medium | Logic Bug |
| 2 | `OnClientDisconnected` callback sees wiped client state | Medium | Logic Bug |
| 3 | Dispose race with in-flight IOCP accept callbacks | Low | Concurrency Bug |
| 4 | Redundant reentrant lock in `Shutdown` | Info | Code Smell |

### Items Verified As Correct

- **Lane load tracking:** `TryDeactivateClient` reads `UnitOfOrder` (line 118)
  **before** `ResetForPool` (line 123) — ordering is correct.
- **Send path disconnect ownership:** `SendCompleted` and `StartSend` do not
  call `Disconnect` when `ProcessSend` returns `false` — `ProcessSend` is the
  sole owner. Clean design.
- **Send queue serialization:** `_sendInProgress` flag correctly ensures only
  one send chain is active per client.  `Enqueue`, `CopyNextChunk`, and
  `CompleteSend` all acquire the queue's `_sync` lock.  No lost-wakeup or
  double-chain races.
- **Self-join guard:** `Service.JoinThread` checks `Thread.CurrentThread !=
  thread` (line 25), preventing deadlock when `Run()` calls `Shutdown()`.
- **Generation-based handle staleness:** `TryGetClient` generation check is
  non-atomic with subsequent use, but safe because `CanReturnToPool` requires
  `pendingOperations == 0` before recycling, and `Activate` bumps generation
  atomically under lock.
- **Buffer slab isolation:** Receive and send buffers occupy non-overlapping
  regions (`clientId * 2 * bufferSize` and `(clientId * 2 + 1) * bufferSize`).
- **Settings validation:** `OrderingLaneCount` correctly uses `<= 0` (line
  135), consistent with the registry constructor.  `ListenSocketRetries` uses
  `< 0` (line 152) with correct parameter name.
