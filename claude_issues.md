# SAEAServer Audit — Issues & Fixes

Audit scope: `Server.cs`, `Client.cs`, `ClientHandle.cs`, `ClientRegistry.cs`,
`AcceptPool.cs`, `SendQueue.cs`, `BufferSlab.cs`

---

## Fixed Issues

### FIX-1: Race Condition — Reading Mutable Client Fields After Close
**Severity:** Medium-High
**File:** `Server.cs` — `Disconnect()`
**Problem:**
`Disconnect()` called `client.Close()` (setting `_isAlive = false`), then read
`client.Identity`, `client.ConnectedAt`, `client.BytesReceived`, `client.BytesSent`
*after* the client was logically dead. Between `Close()` and these reads, another
thread could call `TryDeactivateClient` → `ResetForPool` → `Activate`, recycling
the slot for a new connection. The logged/reported values would belong to the
**new** connection, not the one that disconnected.
**Fix:**
Removed the mutable field reads. All disconnect telemetry now comes from the
`ClientSnapshot` returned by `TryDeactivateClient`, which captures state atomically
under the registry lock before the slot is returned to the pool.

---

### FIX-2: Stale Handle Valid After Pool Return
**Severity:** Medium
**File:** `Client.cs` — `ResetForPool()`
**Problem:**
`_generation` was only incremented in `Activate()`. After `TryDeactivateClient`
called `ResetForPool` and pushed the client back into the available pool, handles
captured before disconnect still passed `TryGetClient` because the generation
had not changed. A consumer holding an old `ClientHandle` could call `Send()` on
a slot that is now available (or re-activated for a different connection) and
succeed, causing cross-connection data leaks.
**Fix:**
Added `_generation = unchecked(_generation + 1)` as the first operation inside
`ResetForPool()` under `_sync`. All pre-existing handles are now immediately
invalidated the moment the slot returns to the pool.

---

### FIX-3: Double Disconnect from ProcessReceive Error Paths
**Severity:** Medium
**File:** `Server.cs` — `ProcessReceive()`
**Problem:**
On socket error, zero-bytes, or null-buffer, `ProcessReceive` called
`Disconnect(clientHandle)` and then returned `false`. Both callers
(`StartReceive` and `ReceiveCompleted`) also call `Disconnect` when the return
value is `false`. This resulted in two `Disconnect` calls for the same handle
on every receive-side error, doubling the disconnect processing and log noise.
**Fix:**
Removed the three explicit `Disconnect(clientHandle)` calls inside
`ProcessReceive` error branches. The method now only returns `false`;
the callers are responsible for the disconnect.

---

### FIX-4: GetAliveClientCount O(n) Allocation on Every Disconnect
**Severity:** Medium
**File:** `ClientRegistry.cs` — `GetAliveClientCount()`
**Problem:**
Allocated a `List<ClientHandle>` of capacity `MaxConnections`, snapshot all
active handles into it, then iterated to count alive clients — every single
disconnect. At 10K connections under churn, this creates significant GC
pressure and O(n) work on the hot path.
**Fix:**
Replaced with `return (uint)_activeHandles.Count` under `_sync`. The
`_activeHandles` list is already precisely maintained (add on activate, remove
on deactivate), so its count is the authoritative alive count with O(1) cost.

---

### FIX-5: NullReferenceException on Default ClientHandle
**Severity:** Medium
**File:** `ClientHandle.cs` — `TryGetClient()`, `Client` property
**Problem:**
`ClientHandle` is a `readonly struct`. A `default(ClientHandle)` has
`_client == null`. Both `TryGetClient` and the `Client` property getter
accessed `_client.Generation` without a null check, causing a
`NullReferenceException` on any default-constructed handle.
**Fix:**
Added `Client? c = _client; if (c is null || ...)` guard to both accessors.
Also added `[DoesNotReturn]` attribute to `ThrowStaleException` so the
compiler recognizes the non-null flow after the guard, eliminating
the CS8603 nullable warning.

---

## Unfixed Issues (Acknowledged, Low Severity)

### OPEN-1: Double Shutdown on ServerStop + Dispose
**Severity:** Low
**File:** `Server.cs` — `Shutdown()`
**Problem:**
Calling `ServerStop()` then `Dispose()` executes `Shutdown()` twice. All
operations inside are idempotent (close null socket, cancel already-cancelled
token, join already-dead threads), so no functional bug occurs, but it does
redundant work.
**Recommendation:**
Add a `_isShutdown` flag checked at the top of `Shutdown()`.

---

### OPEN-2: SendEventArgs Read Outside Lock After Potential Reset
**Severity:** Low
**File:** `Client.cs` — `CompleteSend()`
**Problem:**
`CompleteSend` delegates to `SendQueue.CompleteSend` (which has its own lock),
but the caller `ProcessSend` reads `sendEventArgs.SocketError` and
`sendEventArgs.BytesTransferred` without holding `client._sync`. If a
concurrent `ResetForPool` fires between I/O completion and these reads, the
SAEA state could be inconsistent.
**Mitigation:**
FIX-2 (generation increment in `ResetForPool`) makes this nearly impossible
to reach — the `TryGetClient` check at the top of `ProcessSend` will fail
once the slot is recycled. Residual risk is negligible.

---

### OPEN-3: Timeout Check Precision Is 1x Interval
**Severity:** Low
**File:** `Server.cs` — `CheckSocketTimeout()`
**Problem:**
The check interval equals the timeout value (clamped 1–30s). A client that
goes idle just after a check runs won't be detected until the next cycle,
meaning actual idle time before disconnect can be up to 2x the configured
timeout.
**Recommendation:**
Use `timeout / 2` as the check interval for tighter precision, or document
the behavior as expected.

---

### OPEN-4: Accept Loop Never Yields on Synchronous Burst
**Severity:** Low
**File:** `Server.cs` — `StartAccept()`
**Problem:**
When `AcceptAsync` completes synchronously (returns `false`), the loop
processes inline and immediately re-issues. Under a SYN flood this thread
never yields. Bounded by the `AcceptPool` semaphore, so it won't spin
unbounded, but the accept thread can starve.
**Recommendation:**
Acceptable for current design. Could add a yield-after-N-synchronous counter
if this becomes a measurable problem under load testing.

---

### OPEN-5: O(n) List.Remove in ClientRegistry
**Severity:** Low-Medium
**File:** `ClientRegistry.cs` — `TryDeactivateClient()`
**Problem:**
`_activeHandles.Remove(clientHandle)` is O(n) linear scan. At high connection
counts (10K+) with frequent churn, this becomes a bottleneck under the
`_sync` lock.
**Recommendation:**
Replace with a `Dictionary<int, int>` mapping `ClientId` to list index for
O(1) swap-remove, or use `HashSet<ClientHandle>` if ordering is not required.
