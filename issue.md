# Arrowgene.Networking - AsyncEventServer Correctness Analysis

**Date**: 2026-03-06
**Branch**: `feat/pass-2`
**Scope**: Deep correctness review of `AsyncEventServer.cs` and all supporting types

---

## Critical Issues

### 1. Send chunk size degrades after first small message (Bug)

**Location**: `AsyncEventServer.cs:767-780`, `AsyncEventClient.cs:176-186`

`TryPrepareSendChunk` passes `SendEventArgs.Count` as `maxChunkSize` to `CopyNextChunk`. But `SetBuffer(offset, chunkSize)` on line 780 overwrites `Count` with the actual chunk size of the current send. On subsequent iterations, `maxChunkSize` is the **previous chunk size**, not the buffer capacity.

```csharp
// StartSend loop iteration:
if (!client.TryPrepareSendChunk(out int chunkSize))  // uses SendEventArgs.Count as maxChunkSize
    return;
// ...
sendEventArgs.SetBuffer(sendEventArgs.Offset, chunkSize);  // OVERWRITES Count with chunkSize
```

**Concrete scenario**: Buffer size = 2000, message 1 = 10 bytes, message 2 = 2000 bytes.
- Chunk 1: `maxChunkSize=2000`, copies 10 bytes, `SetBuffer(offset, 10)` → `Count=10`
- Chunk 2: `maxChunkSize=10`, copies only 10 of 2000 bytes → needs 200 round trips instead of 1

**Fix**: Store `_bufferSize` in `AsyncEventClient` and use it as the max chunk size instead of `SendEventArgs.Count`.

---

### 2. Socket leak race in `TryDisconnect`

**Location**: `AsyncEventClient.cs:141-156`

`TryDisconnect` reads `_socket` **outside the lock** after setting `_isAlive = false`:

```csharp
internal void TryDisconnect(out bool wasAlive)
{
    lock (_sync)
    {
        if (!_isAlive) { wasAlive = false; return; }
        _isAlive = false;
    }
    Service.CloseSocket(_socket);  // ← reads _socket outside lock
    wasAlive = true;
}
```

After `_isAlive` is set to false and the lock is released, if `pendingOperations` is 0, another thread can call `TryRecycleClient` → `CanReturnToPool` (succeeds) → `ResetForPool` (sets `_socket = null`). Then `CloseSocket` receives null, and the actual socket is never closed.

**Race timeline**:
1. Thread A (`TryDisconnect`): sets `_isAlive = false`, exits lock
2. Thread B (`InvokeReceivedData` finally): `DecrementPendingOperations` → 0, calls `TryRecycleClient`
3. Thread B: `CanReturnToPool` → `!_isAlive && !_isInPool && pendingOps == 0` → true
4. Thread B: `ResetForPool` → `_socket = null`
5. Thread A: `Service.CloseSocket(null)` → socket is leaked

**Fix**: Capture `_socket` into a local variable inside the lock before calling `CloseSocket`.

```csharp
internal void TryDisconnect(out bool wasAlive)
{
    Socket? socketToClose;
    lock (_sync)
    {
        if (!_isAlive) { wasAlive = false; return; }
        _isAlive = false;
        socketToClose = _socket;
    }
    Service.CloseSocket(socketToClose);
    wasAlive = true;
}
```

---

### 3. `Disconnect` has no pending operations guard — allows premature recycle during callback

**Location**: `AsyncEventServer.cs:199-243`

`Disconnect(AsyncEventClient)` does not increment `pendingOperations` before `TryDisconnect`. The disconnect callback (`InvokeClientDisconnected`) also does not hold pending operations. This means a concurrent thread can recycle the client while the disconnect sequence is still running.

```csharp
internal void Disconnect(AsyncEventClient client)
{
    // No IncrementPendingOperations() here!
    AsyncEventClientHandle disconnectHandle = new AsyncEventClientHandle(this, client);
    client.TryDisconnect(out bool wasAlive);
    if (!wasAlive) { ... return; }

    _clientRegistry.TryRemoveActiveClient(client, disconnectHandle, out int currentConnections);
    // ...
    InvokeClientDisconnected(disconnectHandle, clientIdentity);  // No pending ops held!
    TryRecycleClient(client);
}
```

**Race**: Thread A is in `InvokeClientDisconnected` (user callback running). Thread B calls `TryRecycleClient` from `StartReceive` or `InvokeReceivedData`. Since `pendingOperations == 0`, `CanReturnToPool` succeeds. The client is recycled and potentially re-activated with new state while Thread A's callback is still running. The callback's handle still passes generation check (generation only changes on `Activate`, not `ResetForPool`), but properties now return reset values (`"[Unknown Client]"`, `IPAddress.None`, etc.).

**Fix**: Increment pending operations before `TryDisconnect` and decrement after `InvokeClientDisconnected`, similar to how `InvokeClientConnected` and `InvokeReceivedData` do it.

---

### 4. Generation not invalidated on recycle — stale handles remain "valid"

**Location**: `AsyncEventClient.cs:129`, `AsyncEventClient.cs:225-243`

`Generation` is only incremented in `Activate()`. `ResetForPool()` does not change it. This means handles created during a connection's lifetime remain valid (pass `TryGetClient`) after the client is recycled and reset. They only become stale when the client is **re-activated** for a new connection.

```csharp
internal void Activate(Socket socket, int unitOfOrder)
{
    lock (_sync)
    {
        // ...
        Generation = unchecked(Generation + 1);  // only place generation changes
    }
}

internal void ResetForPool()
{
    lock (_sync)
    {
        _socket = null;
        _isAlive = false;
        // Generation NOT changed — old handles still pass TryGetClient
    }
}
```

Between recycle and re-activation, `TryGetClient` returns true but the client is in pool state. Operations would fail gracefully (`TryBeginSocketOperation` checks `_isInPool`), but:
- `IsAlive` returns false
- `Identity` returns `"[Unknown Client]"`
- Properties return zeroed/default values

This is related to the TODO items at lines 212, 716, and 1051. The handle should be verified as alive, not just generation-matched.

---

## Medium Issues

### 5. `StartReceive` calls both `Disconnect` and `TryRecycleClient` on `TryBeginSocketOperation` failure

**Location**: `AsyncEventServer.cs:632-637`

```csharp
if (!client.TryBeginSocketOperation(out Socket socket))
{
    Disconnect(client);
    TryRecycleClient(client);  // ← also called inside Disconnect if wasAlive
    return;
}
```

If `Disconnect` succeeds (`wasAlive = true`), it internally calls `TryRecycleClient`. Then `StartReceive` also calls `TryRecycleClient`. The second call is a no-op due to `_isInPool` check in `CanReturnToPool`, so no crash — but it's redundant and indicates the lifecycle isn't clean.

The same pattern appears in `StartSend` at lines 773-777.

**Fix**: Either remove the `TryRecycleClient` call after `Disconnect`, or restructure so `Disconnect` always handles recycling.

---

### 6. `InvokeClientConnected` calls `TryRecycleClient` — can recycle a just-connected client

**Location**: `AsyncEventServer.cs:1042-1068`

```csharp
private void InvokeClientConnected(AsyncEventClient client, string clientIdentity)
{
    // ...
    client.IncrementPendingOperations();
    // ...
    finally
    {
        ExitUserCallback();
        client.DecrementPendingOperations();
        TryRecycleClient(client);    // ← can recycle if user called Close() in callback
        TryFinalizeDispose(...);
    }
}
```

If the user calls `handle.Close()` in the connect callback, the client is disconnected. After `DecrementPendingOperations`, the client has `pendingOps == 0` and `_isAlive == false`, so `TryRecycleClient` succeeds. The client is returned to pool.

Then `ProcessAccept` continues at line 594:
```csharp
if (clientHandle.TryGetClient(out AsyncEventClient connectedClient))  // ← succeeds (gen unchanged)
{
    InvokeClientConnected(connectedClient, clientIdentity);
}

if (!clientHandle.TryGetClient(out AsyncEventClient clientToReceive))  // ← also succeeds!
{
    // ...
}

StartReceive(clientToReceive);  // ← called on recycled client
```

`TryGetClient` still succeeds because generation hasn't changed. `StartReceive` is called on a pooled client. `TryBeginSocketOperation` fails (`_isInPool == true`), leading to a spurious `Disconnect` + `TryRecycleClient` cycle and error log noise.

---

### 7. `StartAcceptLoop` can spin on repeated `SocketException`

**Location**: `AsyncEventServer.cs:465-486`

```csharp
catch (SocketException exception)
{
    Logger.Exception(exception);
    _acceptPool.Return(acquiredEventArgs);
    TryFinalizeDispose(nameof(StartAcceptLoop));
    if (_isRunning) { Log(...); }
    continue;  // ← immediately retries with no backoff
}
```

If `AcceptAsync` throws a `SocketException` (e.g., listen socket in a bad state), the loop immediately retries with no delay or retry limit. This can cause a tight CPU-burning loop with continuous exception logging until the server is stopped.

**Fix**: Add exponential backoff or a retry limit before giving up.

---

### 8. `ProcessReceive` — race between `IsAlive` check and `IncrementPendingOperations`

**Location**: `AsyncEventServer.cs:706-718`

```csharp
if (client.IsAlive)                          // takes _sync lock, returns true
{
    client.IncrementPendingOperations();      // Interlocked, no lock
    callbackHandle = new AsyncEventClientHandle(this, client);
    invokeCallback = true;
}
```

Between `IsAlive` returning true and `IncrementPendingOperations`, another thread can set `_isAlive = false`. The pending operation is then incremented on a dead client. This is **benign** in practice — the callback runs, the decrement eventually happens, and the client can then be recycled. But it means a data callback can fire for a client that's already disconnecting. The data itself is valid (it was received before disconnect), so this is more of a semantic concern than a data corruption issue.

---

### 9. `InvokeClientDisconnected` does not guard pending operations

**Location**: `AsyncEventServer.cs:1101-1118`

Unlike `InvokeClientConnected` and `InvokeReceivedData`, the disconnect callback does not call `IncrementPendingOperations`/`DecrementPendingOperations`:

```csharp
private void InvokeClientDisconnected(AsyncEventClientHandle clientHandle, string clientIdentity)
{
    // No IncrementPendingOperations()
    EnterUserCallback();
    try { _consumer.OnClientDisconnected(clientHandle); }
    catch (...) { }
    finally
    {
        ExitUserCallback();
        // No DecrementPendingOperations()
        TryFinalizeDispose(...);
    }
}
```

This means recycling isn't blocked by the disconnect callback being in progress (see Issue #3). It also means `CanFinalizeDispose` can return true while a disconnect callback is still running (it only checks `_activeUserCallbacks`, not pending operations on the client).

---

## Low / Informational Issues

### 10. `AsyncEventClientHandle.BytesSend` typo

**Location**: `AsyncEventClientHandle.cs:80`

```csharp
public ulong BytesSent => Client.BytesSent;
public ulong BytesSend => Client.BytesSent;  // ← "Send" should be "Sent", duplicate property
```

`BytesSend` appears to be a compatibility alias with a typo. If this is intentional, it should be marked `[Obsolete]`. If not, it should be removed.

---

### 11. TODO items indicate unfinished handle verification

**Location**: `AsyncEventServer.cs:212,716,1051`, `AsyncEventClient.cs:190`

All four TODOs relate to handle validity verification at creation time. The current code creates handles but doesn't verify they're alive before invoking callbacks. This is the root cause of Issues #3, #4, and #8. The intended design appears to be: create handle, verify generation + alive status atomically, then proceed.

| Line | Context | What's missing |
|------|---------|----------------|
| 212 | Disconnect handle created | Should verify handle is valid before invoking disconnect callback |
| 716 | Receive callback handle | Should verify handle + alive atomically under lock |
| 1051 | Connect callback handle | Should verify handle + alive atomically under lock |
| 190 | `TryBeginSocketOperation` | Comment suggests null check of socket should be part of the sync block (it already is — TODO may be stale) |

---

### 12. `Dispose` can deadlock if called from user callback

**Location**: `AsyncEventServer.cs:934-958`

```csharp
public void Dispose()
{
    // ...
    if (!IsInUserCallback && !WaitForDisposeDrain(ThreadTimeoutMs))
    {
        Log(LogLevel.Error, nameof(Dispose), "Resource disposal is deferred...");
    }
    TryFinalizeDispose(nameof(Dispose));
}
```

If `Dispose` is called from within a user callback, `IsInUserCallback` is true, and `WaitForDisposeDrain` is skipped. The `_disposeRequested` flag is set, and `TryFinalizeDispose` will eventually clean up when all operations drain. This is handled correctly.

However, if `Dispose` is called from a **non-callback thread** while callbacks are active, `WaitForDisposeDrain` spins for up to 10 seconds waiting for `_activeUserCallbacks == 0`. If a callback calls `Send` which triggers more callbacks, disposal could time out. The server logs an error but continues. Resources are cleaned up later by `TryFinalizeDispose` calls scattered throughout the code. This is technically correct but fragile — if all `TryFinalizeDispose` call sites have already executed, disposal never completes.

---

### 13. `_activeUserCallbacks` and `_userCallbackDepth` are not perfectly synchronized

**Location**: `AsyncEventServer.cs:1120-1132`

```csharp
private void EnterUserCallback()
{
    Interlocked.Increment(ref _activeUserCallbacks);  // global count
    _userCallbackDepth++;                              // thread-local count
}
```

`_activeUserCallbacks` is an `int` with `Interlocked` access (thread-safe). `_userCallbackDepth` is `[ThreadStatic]` (per-thread, no synchronization needed). These serve different purposes and are independently correct. However, if a callback spawns work on another thread, `_userCallbackDepth` on that thread would be 0, so `IsInUserCallback` would return false, potentially allowing `Dispose` to call `WaitForDisposeDrain` from that context.

---

### 14. `CanFinalizeDispose` is called under `_lifecycleLock` but reads volatile fields without consistency

**Location**: `AsyncEventServer.cs:1007-1015`

```csharp
private bool CanFinalizeDispose()
{
    return (!_hasStarted || _stopRequested) &&
           _acceptThreadExited &&
           _timeoutThreadExited &&
           Volatile.Read(ref _activeUserCallbacks) == 0 &&
           AreClientOperationsDrained() &&
           AreAcceptOperationsDrained();
}
```

This is called inside `TryFinalizeDispose` which holds `_lifecycleLock`. But the volatile fields (`_acceptThreadExited`, `_timeoutThreadExited`) and the methods (`AreClientOperationsDrained`, `AreAcceptOperationsDrained`) read state that can change between each check. A snapshot that passes all checks might not represent a consistent point in time. In practice, this is fine because the direction is monotonic (threads exit, operations drain, they don't restart), but it's worth noting.

---

## Summary Table

| # | Severity | Issue | Impact |
|---|----------|-------|--------|
| 1 | **Critical** | Send chunk size degrades after small messages | Throughput degradation, excessive syscalls |
| 2 | **Critical** | Socket leak race in `TryDisconnect` | Socket/FD leak under concurrent disconnect+recycle |
| 3 | **Critical** | No pending ops guard in `Disconnect` | Premature recycle during disconnect callback |
| 4 | **High** | Generation unchanged on recycle | Stale handles pass `TryGetClient` for recycled clients |
| 5 | **Medium** | Redundant `TryRecycleClient` calls | Unnecessary work, confusing lifecycle |
| 6 | **Medium** | `InvokeClientConnected` recycles then `ProcessAccept` continues | Spurious errors, wasted cycles |
| 7 | **Medium** | Accept loop spins on `SocketException` | CPU burn, log spam |
| 8 | **Low** | `IsAlive`/`IncrementPendingOps` race | Phantom data callback (data is valid) |
| 9 | **Medium** | Disconnect callback has no pending ops | Contributes to premature recycle (Issue #3) |
| 10 | **Low** | `BytesSend` typo | API inconsistency |
| 11 | **Info** | 4 TODO items | Unfinished handle verification |
| 12 | **Low** | `Dispose` drain timeout edge case | Deferred cleanup if all drain sites passed |
| 13 | **Info** | Callback depth is thread-local | Spawned threads not tracked |
| 14 | **Info** | `CanFinalizeDispose` non-atomic snapshot | Benign due to monotonic state transitions |
