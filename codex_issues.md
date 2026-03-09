# AsyncEventServer Review Findings

## Critical

### Send completion disconnects healthy clients

- `AsyncEventServer.ProcessSend()` returns the result of `AsyncEventSendQueue.CompleteSend()`.
- `AsyncEventSendQueue.CompleteSend()` returns `false` when the queue is drained normally.
- `AsyncEventServer.StartSend()` and `AsyncEventServer.SendCompleted()` interpret `false` as a fatal condition and call `Disconnect()`.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:592`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:609`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:650`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventSendQueue.cs:113`

Impact:

- A normal send that fully flushes the queue can close the client immediately after sending.

## High

### Stop and dispose can stall on the accept thread

- `ServerStop()` and `Dispose()` call `Shutdown()` while holding `_lifecycleLock`.
- `Shutdown()` joins `_acceptThread`.
- `Run()` begins by taking `_lifecycleLock`.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:142`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:168`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:788`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:794`

Impact:

- If stop races startup, shutdown can wait for the full join timeout because the accept thread is blocked on the same lock.

### Startup failure leaves the server marked as running

- `ServerStart()` sets `_isRunning = true` before listener startup succeeds.
- `Run()` exits on `TrySocketListen()` failure without clearing `_isRunning` or transitioning to stopped.
- The timeout thread may already have been started.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:128`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:131`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:175`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:236`

Impact:

- Failed startup can leave the instance unusable: it is not listening, but restart is blocked because `_isRunning` remains true.

### Dispose can tear down pooled async state before in-flight completions finish

- `Shutdown()` disconnects clients once, but `TryDeactivateClient()` refuses to return a client to the pool while operations are still pending.
- `Dispose()` then disposes the accept pool, client registry, and cancellation source immediately after shutdown.
- Receive/send/accept completions can still arrive against disposed pooled state.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:781`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:805`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventAcceptPool.cs:83`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:408`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:601`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientRegistry.cs:113`

Impact:

- Under load, completion callbacks may run after shared pooled resources have already been disposed.

## Medium

### Disconnect callback sees cleared client state

- `Disconnect()` captures stats locally, then deactivates the client before calling `_consumer.OnClientDisconnected(clientHandle)`.
- Deactivation resets the pooled client fields.
- The handle generation is still current until the next activation, so consumer code can still read the now-reset client slot.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:677`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:700`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientRegistry.cs:118`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:235`

Impact:

- `OnClientDisconnected` can observe `[Unknown Client]`, zeroed counters, and cleared endpoint data instead of the disconnected client snapshot.

### Ordering lane accounting decrements the wrong lane

- `TryDeactivateClient()` calls `ResetForPool()` before reading `client.UnitOfOrder`.
- `ResetForPool()` clears `UnitOfOrder` to `0`.
- The lane load decrement therefore targets lane `0` regardless of the client’s actual lane.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientRegistry.cs:118`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientRegistry.cs:121`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:244`

Impact:

- Lane load tracking drifts over time and violates the intended lowest-available assignment behavior.

## Test Coverage Note

- I did not find a test project in the repository during this review.
- `dotnet test` completed after restore without discovering any tests.
