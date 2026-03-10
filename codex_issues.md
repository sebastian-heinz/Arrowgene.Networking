# SAEA Server Audit

Scope: `Arrowgene.Networking/SAEAServer/Server.cs` and its immediate pooled client infrastructure.

Validation baseline:
- `dotnet test Arrowgene.Networking.Tests/Arrowgene.Networking.Tests.csproj --filter "FullyQualifiedName~ServerIntegrationTests"` passed on March 10, 2026 (`11/11` tests).
- The current test suite does not cover stale-handle ABA races, generation wrap-around, or callback-fatal exception isolation.

## Critical Issues

### 1. ABA race on pooled client slots: stale handles are validated before the operation, not during it

References:
- `Arrowgene.Networking/SAEAServer/ClientHandle.cs:49`
- `Arrowgene.Networking/SAEAServer/Server.cs:473`
- `Arrowgene.Networking/SAEAServer/Server.cs:636`
- `Arrowgene.Networking/SAEAServer/Server.cs:667`
- `Arrowgene.Networking/SAEAServer/Client.cs:223`
- `Arrowgene.Networking/SAEAServer/Client.cs:262`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:93`
- `Arrowgene.Networking/SAEAServer/Client.cs:139`
- `Arrowgene.Networking/SAEAServer/Client.cs:282`

Problem:
- `ClientHandle.TryGetClient` only proves that the handle matched the client generation at that instant.
- After that check returns, server code keeps a raw `Client` reference and performs later operations without re-checking the captured generation under the same client lock.
- `TryDeactivateClient` can return the slot to the pool, and `Activate` can immediately bind that same `Client` instance to a different socket before the original caller reaches `QueueSend` or `TryBeginSocketOperation`.

Failure modes:
- An old user handle can enqueue bytes onto a newly accepted connection.
- An old receive/send path can start a socket operation on the recycled slot.
- The recycled connection can then hit `InvalidOperationException` from double use of the same `SocketAsyncEventArgs`, followed by a spurious disconnect.

Why this matters:
- This defeats the entire generation-based stale-handle design under high churn.
- It is a correctness bug, not just a logging/race nuisance: data can be delivered to the wrong connection.

### 2. `uint` generation will wrap on a long-lived churn-heavy server

References:
- `Arrowgene.Networking/SAEAServer/Client.cs:23`
- `Arrowgene.Networking/SAEAServer/Client.cs:165`
- `Arrowgene.Networking/SAEAServer/Client.cs:286`
- `Arrowgene.Networking/SAEAServer/ClientHandle.cs:26`

Problem:
- The stale-handle key is a 32-bit generation.
- That counter increments twice per connection lifecycle: once in `Activate`, once again in `ResetForPool`.
- The pool is LIFO (`Stack<Client>`), so the same hot slot is preferentially reused.

Impact:
- After `2,147,483,648` connect/disconnect cycles on one slot, an old stale handle becomes valid again.
- At `1,000` cycles/sec on one hot slot, wrap-around happens in about `24.8` days.
- At `10,000` cycles/sec, it drops to about `2.5` days.

Why this matters:
- This turns a dormant stale handle into a live handle for an unrelated future client.
- In a single-run server intended to stay up, that is an eventual correctness fault.

### 3. Unhandled exceptions inside SAEA completion callbacks can terminate the process

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:393`
- `Arrowgene.Networking/SAEAServer/Server.cs:526`
- `Arrowgene.Networking/SAEAServer/Server.cs:731`
- `Arrowgene.Networking/SAEAServer/Server.cs:754`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:57`
- `Arrowgene.Networking/SAEAServer/SendQueue.cs:97`

Problem:
- `AcceptCompleted`, `ReceiveCompleted`, and `SendCompleted` only use `try/finally` to balance `_inFlightAsyncCallbacks`.
- They do not catch unexpected exceptions from internal logic.

Examples of internal exceptions that currently escape the callback thread:
- `ClientRegistry.TryActivateClient` can throw if the pooled client is in an invalid activation state.
- `SendQueue.CompleteSend` throws if queued state and transferred byte count diverge.
- Any future invariant bug inside `ProcessAccept`, `ProcessReceive`, or `ProcessSend` becomes process-fatal instead of isolating the connection.

Why this matters:
- A single pooled-state bug or unexpected runtime edge case can bring down the entire server.
- For a high-connection server, callback entrypoints must be process-isolating boundaries.

## Optimization Suggestions

### 1. Remove restart support and make lifecycle single-run

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:175`
- `Arrowgene.Networking/SAEAServer/Server.cs:1158`
- `Arrowgene.Networking.Tests/ServerIntegrationTests.cs:343`

Reason:
- `AGENTS.md` states the server lifecycle is `start once` and `stop once`.
- The current restart path keeps extra state transitions, thread recreation, and disposal/reinitialization logic alive for a mode that should not exist.

Recommendation:
- Allow `Start()` only from `Created`.
- Make `Stop()` terminal.
- Remove `RecreateRunResources()`.
- Delete or invert the restart test so the contract matches the intended architecture.

### 2. Reduce disconnect latency and thread wakeups by removing the polling cleanup thread

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:864`
- `Arrowgene.Networking/SAEAServer/Server.cs:1130`
- `Arrowgene.Networking/SAEAServer/Client.cs:311`

Reason:
- Closed clients with drained pending operations are only returned to the pool when the cleanup thread polls, up to `500 ms` later.
- That slows slot reuse under churn and adds a permanent wakeup thread.

Recommendation:
- When `DecrementPendingOperations()` reaches zero on a dead client, trigger cleanup immediately.
- A signaled queue or inline finalization path is better than timer polling for this case.

### 3. Remove O(N) hot-path work in the client registry

References:
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:71`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:124`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:139`

Reason:
- `FindLeastLoadedLane()` is O(lane count) on every accept.
- `RemoveActiveHandleFastNoLock()` is actually O(connection count) because it does `List.IndexOf`.
- `SnapshotActiveHandles()` copies the full active list for timeout scans and shutdown loops.

Recommendation:
- Give each client an active-list index for O(1) removal.
- If lane balancing matters, track a min-heap or bitmap instead of scanning every lane on every activation.
- If timeouts stay enabled at high scale, consider a structure that avoids copying the full active set each sweep.

### 4. Current copy semantics are isolation-safe but allocation-heavy

References:
- `Arrowgene.Networking/SAEAServer/Server.cs:593`
- `Arrowgene.Networking/SAEAServer/SendQueue.cs:46`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:86`
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:87`

Reason:
- Every receive allocates a new `byte[]`.
- Every send enqueues a copied `byte[]`.
- Every activation boxes `ClientHandle` twice into `SocketAsyncEventArgs.UserToken`.

Recommendation:
- Keep the copy boundary, but consider a pooled owned-buffer abstraction if the public API can change.
- If the API must remain `byte[]`, keep payload sizes below LOH thresholds and avoid excessive fragmentation from very large messages.
- Replace boxed `ClientHandle` tokens with a pooled reference token object if connection churn is high.

### 5. Disconnect logging is expensive for churn-heavy workloads

Reference:
- `Arrowgene.Networking/SAEAServer/Server.cs:836`

Reason:
- Every disconnect emits a multiline formatted payload including human-readable conversions.
- On busy edge servers, this quickly dominates CPU and allocation cost.

Recommendation:
- Keep the full disconnect summary behind debug/trace logging or sample it.
- Emit compact counters by default.

## Code Refactor

### 1. Pin generation to the actual client mutation/socket operation

```csharp
internal bool TryBeginSocketOperation(uint generation, out Socket socket)
{
    lock (_sync)
    {
        if (_generation != generation || !_isAlive || _isInPool || _socket is not { } currentSocket)
        {
            socket = null!;
            return false;
        }

        _pendingOperations++;
        socket = currentSocket;
        return true;
    }
}

internal bool TryQueueSend(uint generation, byte[] data, out bool startSend, out bool queueOverflow)
{
    lock (_sync)
    {
        if (_generation != generation || !_isAlive || _isInPool)
        {
            startSend = false;
            queueOverflow = false;
            return false;
        }

        return _sendQueue.Enqueue(data, out startSend, out queueOverflow);
    }
}
```

Then call those with the captured handle generation instead of validating once and using a raw `Client` reference later.

### 2. Make SAEA callback entrypoints exception-isolating

```csharp
private void SendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
{
    EnterAsyncCallback();
    try
    {
        if (eventArgs.UserToken is not ClientHandle clientHandle)
        {
            Log(LogLevel.Error, nameof(SendCompleted), "Unexpected user token.");
            return;
        }

        if (ProcessSend(clientHandle))
        {
            StartSend(clientHandle);
        }
    }
    catch (Exception exception)
    {
        Logger.Exception(exception);

        if (eventArgs.UserToken is ClientHandle clientHandle)
        {
            Disconnect(clientHandle, "SendCompletedFailure");
        }
    }
    finally
    {
        ExitAsyncCallback();
    }
}
```

Apply the same guard shape to `AcceptCompleted` and `ReceiveCompleted`.

### 3. Simplify lifecycle to match the single-run contract

```csharp
public void Start()
{
    lock (_lifecycleLock)
    {
        if (_state != ServerState.Created)
        {
            throw new InvalidOperationException("Server is single-run and can only be started once.");
        }

        _state = ServerState.Running;
    }

    _disconnectCleanupThread.Start();
    if (_socketTimeout > TimeSpan.Zero)
    {
        _timeoutThread.Start();
    }

    _acceptThread.Start();
}
```

This removes the need to recreate cancellation, threads, and shutdown events after `Stop()`.

## Open Questions

### 1. Ordering semantics do not match the architecture note

References:
- `Arrowgene.Networking/SAEAServer/ClientRegistry.cs:71`
- `AGENTS.md` higher concept: "always assign the lowest available"

Current behavior is "least-loaded shared lane", not "lowest available unique unit". If the architecture note is the real contract, the current `OrderingLaneCount` model needs a redesign rather than a tuning pass.
