# AsyncEventServer post-refactor analysis

The `send` / `recv` memory copy behavior was still treated as intentional for isolation and is not called out as an issue.

## Lifecycle redesign

`AsyncEventServer` is now explicitly single-use:

- `ServerStart()` rejects any second start attempt and no longer contains restart-drain logic or run-generation reuse checks.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L88).
- `ServerStop()` transitions the instance into a permanent stopped state through `_stopRequested`.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L133).
- The base `TcpServer` only fires `OnStart` / `OnStop` when the transition actually succeeded.
  - See [TcpServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/TcpServer.cs#L54).

The removed restart-specific pieces were:

- `_runGeneration`
- reinitializing `_cancellation` on every stop
- `AsyncEventClientRegistry.PrepareForStart()`
- accept-pool generation stamping
- waiting for the client pool to drain before allowing a new start

## Concrete fixes made

### 1. Reentrant stop/dispose no longer wait on the callback that invoked them

Before the refactor, shutdown waited for `PendingOperations` and client-pool drain while user callbacks were still on the stack. The new code releases the socket-operation count before consumer code runs and no longer treats pool drain as a stop prerequisite.

- Receive completions decrement the pending operation before dispatching user code.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L698).
- Send completions also decrement first.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L835).
- `StopCore()` now closes the listener, cancels the token, disconnects active clients, joins worker threads, and returns without waiting for the client pool to recycle.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L372).

### 2. Callback handles stay valid for the full callback duration

The server now reserves a client while `OnClientConnected(...)` and `OnReceivedData(...)` are running, so another thread cannot recycle that pooled slot out from under the callback.

- `AsyncEventClient` now tracks `_callbackReservations`.
  - See [AsyncEventClient.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventClient.cs#L25).
- `TryAcquireCallbackHandle(...)` / `ReleaseCallbackHandle()` guard callback lifetime.
  - See [AsyncEventClient.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventClient.cs#L209).
- `TryReturnToPool()` now refuses to recycle while a callback reservation exists.
  - See [AsyncEventClient.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventClient.cs#L332).

### 3. Disconnect callbacks no longer receive a handle that can go stale mid-callback

`OnClientDisconnected(...)` now receives an immutable disconnected-socket snapshot instead of a generation-checked pooled handle.

- Snapshot creation happens in `Disconnect(...)`.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L195).
- The snapshot type is [AsyncEventDisconnectedSocket.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventDisconnectedSocket.cs#L6).

### 4. Final disposal now waits for real shutdown conditions

Disposal is now deferred until all of the following are true:

- shutdown has been requested
- accept thread exited
- timeout thread exited
- no consumer callback is active
- no client still has pending socket operations
- no accept operation is still holding a pooled `SocketAsyncEventArgs`

Evidence:

- `Dispose()` only requests stop and optionally waits; actual disposal happens in `TryFinalizeDispose(...)`.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L928).
- `CanFinalizeDispose()` encodes the final drain conditions.
  - See [AsyncEventServer.cs](/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Tcp/Server/AsyncEvent/AsyncEventServer.cs#L1001).

## Current findings

After the one-shot lifecycle redesign, I do not see a remaining concrete synchronization bug in `AsyncEventServer` on the same level as the pre-refactor issues.

Remaining non-server observations from the build are broader codebase warnings, not new async-event defects:

- nullable-reference warnings across older types
- obsolete `Thread.Abort()` usage in `UdpSocket`
- some older equality implementations with nullability mismatches
