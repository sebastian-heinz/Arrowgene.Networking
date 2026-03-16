# Arrowgene.Networking - Code Review Issues

## Bugs

### 1. Race condition in `EnterAsyncCallback` / `ExitAsyncCallback`

**Status:** Open
**File:** `TcpServer.cs:1094-1106`

The `ManualResetEventSlim` drain gate has a race window between `EnterAsyncCallback` (Increment then Reset) and `ExitAsyncCallback` (Decrement then Set). Consider the following interleaving:

1. Thread A: `Interlocked.Increment` -> count = 1
2. Thread B: `Interlocked.Decrement` -> count = 0, calls `Set()`
3. Thread A: calls `Reset()` — overwrites the `Set()` even though count is 0

This leaves `_asyncCallbacksDrained` in the reset state with zero in-flight callbacks. `WaitForShutdownQuiescence` self-corrects via its timeout-retry loop, but shutdown can stall for up to `DisconnectCleanupDelayMs` (500ms) per occurrence.

**Fix:** Use `Interlocked` compare-and-swap or a `CountdownEvent` pattern to make enter/exit atomic with respect to the event state.

---

### 2. `CancellationTokenSource` double-dispose in `Dispose`

**Status:** Resolved
**File:** `TcpServer.cs:1042-1058`

`Dispose()` previously called `Shutdown()` (which disposes `_cancellation`) and then disposed `_cancellation` a second time. The redundant `_cancellation.Dispose()` call has been removed from `Dispose()`. `Shutdown()` remains the single owner of CTS disposal.

---

### 3. `OrderingLaneCount` mismatch between server and `ThreadedBlockingQueueConsumer` causes runtime crash

**Status:** Resolved
**Files:** `ISupportsOrderingLaneCount.cs` (new), `TcpServer.cs:118-126`, `ThreadedBlockingQueueConsumer.cs:11,69`

A new `ISupportsOrderingLaneCount` interface was added to the `Consumer` namespace. Any consumer that requires a minimum lane count can implement it. `ThreadedBlockingQueueConsumer` implements the interface via its `OrderingLaneCount` property. The `TcpServer` constructor checks for the interface and throws `ArgumentException` at construction time if the consumer's lane count is insufficient. This is decoupled from any concrete consumer type.

---

### 4. `_pendingOperations` can go negative

**Status:** Resolved
**File:** `Client.cs:349-360`

`DecrementPendingOperations` now uses a compare-and-swap loop that refuses to decrement below zero. If the count is already <= 0, the method returns without modification, preventing the counter from going negative and protecting against premature pool return.

---

## Design Issues

### 5. `BlockingQueueConsumer` does not implement `IDisposable`

**Status:** Resolved
**File:** `BlockingQueueConsumer.cs`

`BlockingQueueConsumer` now implements `IDisposable` and disposes the underlying `BlockingCollection<IClientEvent>` in its `Dispose()` method.

---

### 6. Inconsistent stale-handle behavior on `ClientHandle`

**Status:** Open
**File:** `ClientHandle.cs`

Property accessors (`Identity`, `RemoteIpAddress`, `Port`, etc.) throw `ObjectDisposedException` via the `Client` getter when the handle is stale (line 37-43). But `Send()` and `Disconnect()` silently no-op on stale handles (lines 137-161). This inconsistency means the same handle can throw on read but silently drop writes, making it harder for consumers to reason about handle validity.

**Recommendation:** Either make all operations consistently throw on stale handles, or make all operations consistently no-op (with documentation stating which behavior to expect).

---

### 7. Thread name constants are inconsistent

**Status:** Open
**File:** `TcpServer.cs:50-52`

```csharp
private const string AcceptThreadName = "TcpServer";
private const string TimeoutThreadName = "AsyncEventServer_Timeout";
private const string DisconnectCleanupThreadName = "AsyncEventServer_DisconnectCleanup";
```

The accept thread uses `"TcpServer"` while the others use `"AsyncEventServer_*"`. This makes thread dumps harder to correlate.

---

### 8. Duplicate XML doc on `ClientSocketSettings`

**Status:** Open
**File:** `TcpServerSettings.cs:108-112`

The `ClientSocketSettings` property has the doc-comment "Gets or sets the socket configuration applied to listener and accepted sockets." — which is copy-pasted from `ListenSocketSettings` (line 98). It should say "client sockets" instead.

---

### 9. `BytesSend` compatibility alias should be marked `[Obsolete]`

**Status:** Open
**File:** `ClientHandle.cs:127-129`

```csharp
public ulong BytesSend => Client.BytesSent;
```

This property exists as a backward-compatibility alias for the typo `BytesSend` (should be `BytesSent`). It should be annotated with `[Obsolete("Use BytesSent instead.")]` to guide consumers toward the correct name.

---

### 10. Double generation increment per connection cycle

**Status:** Open
**File:** `Client.cs:190, 321`

Generation is incremented in `Activate()` (line 190) and again in `ResetForPool()` (line 321). This means each connection cycle consumes two generation values, halving the effective generation space before `uint` wraps. While wrapping is unlikely in practice (4 billion connections per pool slot), the double-increment is non-obvious and should be documented or consolidated.

---

## Minor / Code Quality

### 11. `SocketSettings` default `Ttl = 32` is lower than typical OS defaults

**Status:** Open
**File:** `SocketSettings.cs:31`

Most operating systems default to TTL 64 (Linux) or 128 (Windows). A TTL of 32 could cause packets to be dropped in networks with many hops, especially across cloud regions or CDN edges.

---

### 12. `SocketSettings.ConfigureSocket` applies `DontFragment` only for `SocketType.Dgram`

**Status:** Open
**File:** `SocketSettings.cs:176`

Since this library creates `SocketType.Stream` (TCP) sockets, the `DontFragment` setting is never applied. The property exists in the configuration but is effectively dead code for TCP use cases.

---

### 13. `GetHumanReadableSize` uses floating-point `Math.Log` for index calculation

**Status:** Open
**File:** `Service.cs:103`

```csharp
int mag = (int)Math.Log(value, 1024);
```

Floating-point imprecision can cause `Math.Log` to return a value slightly below an integer boundary, producing an off-by-one result for exact powers of 1024. An integer loop dividing by 1024 would be more reliable.

---

### 14. `SocketOption.Value` typed as `object` may not round-trip through `[DataContract]` serialization

**Status:** Open
**File:** `SocketOption.cs:33`

The `Value` property is `object`, which means DataContract serialization will fail for types not known to the serializer. If socket options are ever persisted/deserialized, this will need a `[KnownType]` declaration or a custom serialization strategy.

---

### 15. `RecreateRunResources` does not clear the disconnect cleanup queue

**Status:** Open
**File:** `TcpServer.cs:1194-1204`

When the server is restarted via `Stop()` then `Start()`, the `_disconnectCleanupQueue` is not cleared. Stale handles from the previous run will be harmlessly skipped (generation mismatch), but they add unnecessary iteration and noise to the cleanup thread.

---

### 16. `EventConsumer.OnError` event name shadows `IConsumer.OnError`

**Status:** Open
**File:** `EventConsumer.cs:28`

The public event `OnError` has the same name as the `IConsumer.OnError` method. While explicit interface implementation resolves the ambiguity at the CLR level, it's confusing in an IDE and violates the .NET naming guideline that events should not be prefixed with "On" (that prefix is for the method that raises the event).

---

### 17. Redundant `Volatile.Read` / `Interlocked.Read` inside lock

**Status:** Open
**File:** `Client.cs:139-161`

`Client.Snapshot()` acquires `_sync` and then uses `Volatile.Read` and `Interlocked.Read` for fields that are already protected by the lock. The lock provides a full memory barrier, making the volatile/interlocked reads redundant inside it.

---

### 18. `TcpServerSettings.Validate()` does not validate `ClientSocketTimeoutSeconds`

**Status:** Open
**File:** `TcpServerSettings.cs:123-168`

Values like `0` or `-2` are silently accepted. The documented contract is `-1` to disable. A value of `0` creates `TimeSpan.Zero`, which disables the timeout thread (correct but undocumented). Negative values other than `-1` create negative TimeSpans (also disables, but undocumented).

**Fix:** Validate that `ClientSocketTimeoutSeconds` is either `-1` or `> 0`.
