# AsyncEventServer Code Review — Issues

Review of `AsyncEventServer.cs` and its supporting types, focused on locking,
synchronization, and logic correctness.

---

## Issue 1 — Double Disconnect in ProcessReceive / ProcessSend (Logic Bug)

**Severity:** Medium
**Files:** `AsyncEventServer.cs`

`ProcessReceive` calls `Disconnect` on every error path (lines 442, 455, 465)
**and** returns `false`. Its callers — `ReceiveCompleted` (line 423) and the
`StartReceive` loop (line 401) — **also** call `Disconnect` when `false` is
returned.  This results in `Disconnect` being invoked twice for the same error
on the same thread.

The same pattern exists in `ProcessSend` (lines 631, 639, 647) paired with
`SendCompleted` (line 616) and the `StartSend` loop (line 595).

**Impact:**
`Close()` is idempotent and `TryDeactivateClient` returns `false` the second
time, so this does not cause a crash or double-free.  However, every duplicated
call does redundant lock acquisitions, logging, and consumer-error-handler
work.  More importantly it makes the protocol of `ProcessReceive`/`ProcessSend`
ambiguous — the caller cannot tell whether a `false` return means "I already
disconnected" or "you should disconnect".

**Suggested fix:**
Choose one owner of the disconnect.  Either:
- Remove the `Disconnect` calls **inside** `ProcessReceive`/`ProcessSend` and
  let the caller do it, or
- Keep them inside and have the methods return a different sentinel (e.g. a
  tri-state) so the caller knows not to disconnect again.

---

## Issue 2 — Lane Load Decremented for Wrong Lane (Logic Bug)

**Severity:** High
**Files:** `AsyncEventClientRegistry.cs` lines 113-127

In `TryDeactivateClient`, `ResetForPool()` (line 118) sets `UnitOfOrder = 0`
**before** line 121 reads `client.UnitOfOrder` to decrement the lane counter.
The result is that the lane counter is **always decremented for lane 0**
regardless of which lane the client was actually on.

```csharp
client.ResetForPool();                       // sets UnitOfOrder = 0
_availableClients.Push(client);
_activeHandles.Remove(clientHandle);
int orderingLane = client.UnitOfOrder;       // always 0
```

Over time, lane 0's counter drifts negative (or underflows to a very large
value if unsigned), while the real lane's counter only ever increases.
`FindLeastLoadedLane` will then always pick lane 0, defeating the
load-balancing logic entirely.

**Suggested fix:**
Capture `UnitOfOrder` **before** calling `ResetForPool()`.

---

## Issue 3 — `OrderingLaneCount` Validation Allows Zero (Validation Bug)

**Severity:** Medium
**Files:** `AsyncEventSettings.cs` line 135

`Validate()` accepts `OrderingLaneCount == 0`:

```csharp
if (OrderingLaneCount < 0)   // allows 0
```

But `AsyncEventClientRegistry` constructor (line 25) throws
`ArgumentOutOfRangeException` when `orderingLaneCount <= 0`, and
`FindLeastLoadedLane` would index into an empty array.

**Suggested fix:**
Change the validation to `<= 0`, consistent with the registry.

---

## Issue 4 — `ListenSocketRetries` Validation Has Two Errors (Validation Bug)

**Severity:** Low
**Files:** `AsyncEventSettings.cs` lines 153-156

```csharp
if (ListenSocketRetries <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(MaxQueuedSendBytes),  // wrong name
        "ListenSocketRetries must be zero or greater.");               // contradicts <= 0 check
}
```

1. The parameter name passed to the exception is `MaxQueuedSendBytes` instead
   of `ListenSocketRetries`.
2. The message says "must be zero or greater" but the condition rejects zero.
   Either the condition should be `< 0` (to allow zero retries) or the message
   should say "must be greater than zero".

---

## Issue 5 — `ProcessAccept` Calls Consumer Callback Before Starting Receive (Design Concern)

**Severity:** Low
**Files:** `AsyncEventServer.cs` lines 336-350

`_consumer.OnClientConnected(clientHandle)` is called before
`StartReceive(clientHandle)`.  If the consumer immediately sends data to the
client inside `OnClientConnected`, this works because `Send` is independent of
the receive path.  However, if the consumer stores the handle and another
thread races to send before `StartReceive` has posted the first receive, there
is no data-loss risk but the ordering is worth noting:
- The client's `ReceiveEventArgs.UserToken` is already set (line 97-98 in
  `TryActivateClient`), so even if the consumer triggers a send that completes
  inline and disconnects, the receive side handles the stale check gracefully.

This is not a bug under the current code but is fragile to future changes.

---

## Issue 6 — `Shutdown` Called Inside Already-Held `_lifecycleLock` (Redundant Reentrant Lock)

**Severity:** Info
**Files:** `AsyncEventServer.cs`

`ServerStop` holds `_lifecycleLock` (line 142) and calls `Shutdown()` (line
160), which immediately tries to acquire `_lifecycleLock` again (line 765).
C# `Monitor` is reentrant, so this does not deadlock, but the inner lock
acquisition is redundant.  The same pattern occurs from `Dispose`.

`Shutdown` is a `private` method only ever called while `_lifecycleLock` is
already held; the inner lock is dead code.

---

## Issue 7 — `_listenSocket` Read Without Synchronization in `StartAccept`

**Severity:** Low
**Files:** `AsyncEventServer.cs` line 252

```csharp
Socket? listenSocket = _listenSocket;
```

This read occurs on the accept thread outside any lock.  `_listenSocket` is
written under `_lifecycleLock` in `TrySocketListen` (line 207) and set to
`null` in `Shutdown` (line 772).  Because the accept thread is started after
`_lifecycleLock` is released in `Run`, the .NET memory model guarantees
visibility of the initial write (lock release is a full fence).

However, during shutdown, `_listenSocket` is set to `null` without
synchronization relative to `StartAccept`.  The code is still safe because
`AcceptAsync` on a disposed socket throws `ObjectDisposedException`, which is
caught.  But making `_listenSocket` `volatile` (or reading it under lock)
would make the intent clearer and avoid relying on the exception path for
correct shutdown.

---

## Issue 8 — `OnClientDisconnected` Receives a Stale Handle (Design Concern)

**Severity:** Low
**Files:** `AsyncEventServer.cs` lines 698-710

After `TryDeactivateClient` succeeds, the client has been returned to the pool
and `ResetForPool` has been called.  The `clientHandle` passed to
`_consumer.OnClientDisconnected` still has the old generation, so
`TryGetClient` **will succeed** (generation was not bumped by `ResetForPool`),
but the client's state has been wiped.  The existing TODO on line 700
acknowledges this:

```csharp
_consumer.OnClientDisconnected(clientHandle); // TODO pass a new object, that is a client snapshot
```

If a consumer accesses `clientHandle.Identity` or `clientHandle.BytesReceived`
inside this callback, it will see the reset values (`"[Unknown Client]"`, `0`),
not the values from the just-disconnected session.  The server already captures
these values into locals (lines 672-675) for logging but does not forward them
to the consumer.

---

## Summary

| # | Issue | Severity | Type |
|---|---|---|---|
| 1 | Double `Disconnect` in ProcessReceive / ProcessSend | Medium | Logic Bug |
| 2 | Lane load decremented for wrong lane (always lane 0) | High | Logic Bug |
| 3 | `OrderingLaneCount` validation allows zero | Medium | Validation Bug |
| 4 | `ListenSocketRetries` validation — wrong param name & contradictory message | Low | Validation Bug |
| 5 | Consumer callback before receive is posted | Low | Design Concern |
| 6 | Redundant reentrant lock in `Shutdown` | Info | Code Smell |
| 7 | `_listenSocket` read without synchronization | Low | Concurrency |
| 8 | `OnClientDisconnected` receives handle with reset state | Low | Design Concern |
