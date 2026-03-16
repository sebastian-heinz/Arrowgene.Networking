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

### 2. `Stop()` / `Dispose()` can deadlock when called from a consumer callback

**File:** `TcpServer.cs:563-593`, `TcpServer.cs:980-1016`, `TcpServer.cs:1128-1143`

`ReceiveCompleted`, `SendCompleted`, and `AcceptCompleted` mark the callback as in-flight before invoking consumer code. If that consumer code calls `Stop()` or `Dispose()` on the same `TcpServer`, shutdown enters `WaitForShutdownQuiescence()` and waits for `_inFlightAsyncCallbacks` to reach zero. But the current callback cannot reach `ExitAsyncCallback()` because it is blocked inside shutdown, so the server waits on itself indefinitely.

This is reproducible with a consumer that captures the server instance and calls `Stop()` from `OnReceivedData`, `OnClientConnected`, or `OnError`.

**Fix:** Detect shutdown reentrancy from a socket callback and avoid waiting for the currently executing callback, or offload shutdown onto a separate thread/task before waiting for quiescence.

---

## Design Issues

### 3. Inconsistent stale-handle behavior on `ClientHandle`

**File:** `ClientHandle.cs`, `ThreadedBlockingQueueConsumer.cs:309-345`, `ClientDataEvent.cs:18-26`, `ClientEvent.cs:13-20`, `ClientErrorEvent.cs:15-34`

Property accessors (`Identity`, `RemoteIpAddress`, `Port`, etc.) throw `ObjectDisposedException` via the `Client` getter when the handle is stale (line 37-43). But `Send()` and `Disconnect()` silently no-op on stale handles (lines 137-161). This inconsistency means the same handle can throw on read but silently drop writes, making it harder for consumers to reason about handle validity.

The impact is larger than the handle type alone: `BlockingQueueConsumer` and `ThreadedBlockingQueueConsumer` queue live `ClientHandle` instances for later processing. If a queue backs up, the eventual consumer can observe a stale handle whose reads throw while `Send()`/`Disconnect()` silently do nothing, so the same queued event becomes only partially usable depending on timing.

**Recommendation:** Either make all operations consistently throw on stale handles, or make all operations consistently no-op (with documentation stating which behavior to expect).

---

### 4. Double generation increment per connection cycle

**File:** `Client.cs:190, 321`

Generation is incremented in `Activate()` (line 190) and again in `ResetForPool()` (line 321). This means each connection cycle consumes two generation values, halving the effective generation space before `uint` wraps. While wrapping is unlikely in practice (4 billion connections per pool slot), the double-increment is non-obvious and should be documented or consolidated.

---

## Minor / Code Quality

### 5. `SocketSettings.ConfigureSocket` applies `DontFragment` only for `SocketType.Dgram`

**File:** `SocketSettings.cs:176`

Since this library creates `SocketType.Stream` (TCP) sockets, the `DontFragment` setting is never applied. The property exists in the configuration but is effectively dead code for TCP use cases.

---

### 6. `SocketOption` serialization and nullable contracts are brittle

**File:** `SocketOption.cs:16-20`, `SocketOption.cs:32-33`, `SocketOption.cs:74-102`

The `Value` property is `object`, which means DataContract serialization will fail for types not known to the serializer. In addition, `DataLevel` silently maps unknown enum strings to `default(SocketOptionLevel)` instead of failing fast, so malformed configuration can deserialize into the wrong socket option level without any error.

This type also emits nullable warnings during `dotnet test` (`CS8767` and `CS8765`) because `Equals(SocketOption)` and `Equals(object)` are not annotated for nullable reference types. That keeps the project from building warning-clean and makes new warnings easier to miss.

**Recommendation:** Use an explicit serialization strategy for `Value`, reject invalid `DataLevel` values during deserialization, and update the equality signatures to `SocketOption? other` / `object? obj`.
