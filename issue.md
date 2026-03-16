# Arrowgene.Networking - Code Review Issues

## Bugs

### 1. Race condition in `EnterAsyncCallback` / `ExitAsyncCallback`

**File:** `TcpServer.cs:1103-1115`

The `ManualResetEventSlim` drain gate has a race window between `EnterAsyncCallback` (Increment then Reset) and `ExitAsyncCallback` (Decrement then Set). Consider the following interleaving:

1. Thread A: `Interlocked.Increment` -> count = 1
2. Thread B: `Interlocked.Decrement` -> count = 0, calls `Set()`
3. Thread A: calls `Reset()` — overwrites the `Set()` even though count is 0

This leaves `_asyncCallbacksDrained` in the reset state with zero in-flight callbacks. `WaitForShutdownQuiescence` self-corrects via its timeout-retry loop, but shutdown can stall for up to `DisconnectCleanupDelayMs` (500ms) per occurrence.

**Fix:** Use `Interlocked` compare-and-swap or a `CountdownEvent` pattern to make enter/exit atomic with respect to the event state.

---

## Design Issues

### 2. Inconsistent stale-handle behavior on `ClientHandle`

**File:** `ClientHandle.cs`

Property accessors (`Identity`, `RemoteIpAddress`, `Port`, etc.) throw `ObjectDisposedException` via the `Client` getter when the handle is stale (line 37-43). But `Send()` and `Disconnect()` silently no-op on stale handles (lines 137-161). This inconsistency means the same handle can throw on read but silently drop writes, making it harder for consumers to reason about handle validity.

**Recommendation:** Either make all operations consistently throw on stale handles, or make all operations consistently no-op (with documentation stating which behavior to expect).

---

### 3. Double generation increment per connection cycle

**File:** `Client.cs:190, 321`

Generation is incremented in `Activate()` (line 190) and again in `ResetForPool()` (line 321). This means each connection cycle consumes two generation values, halving the effective generation space before `uint` wraps. While wrapping is unlikely in practice (4 billion connections per pool slot), the double-increment is non-obvious and should be documented or consolidated.

---

## Minor / Code Quality

### 4. `SocketSettings.ConfigureSocket` applies `DontFragment` only for `SocketType.Dgram`

**File:** `SocketSettings.cs:176`

Since this library creates `SocketType.Stream` (TCP) sockets, the `DontFragment` setting is never applied. The property exists in the configuration but is effectively dead code for TCP use cases.

---

### 5. `SocketOption.Value` typed as `object` may not round-trip through `[DataContract]` serialization

**File:** `SocketOption.cs:33`

The `Value` property is `object`, which means DataContract serialization will fail for types not known to the serializer. If socket options are ever persisted/deserialized, this will need a `[KnownType]` declaration or a custom serialization strategy.
