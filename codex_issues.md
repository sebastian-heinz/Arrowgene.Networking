# AsyncEventServer Review Findings

## High

### 1. Small sends permanently shrink future send chunk size for the pooled client slot

- `AsyncEventServer.StartSend()` mutates `SendEventArgs.Count` to the current chunk length.
- `AsyncEventClient.TryPrepareSendChunk()` then reuses that mutable `Count` as the maximum size for the next chunk.
- `ResetForPool()` and `Activate()` never restore the original buffer length.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:561`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:199`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:235`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventBufferSlab.cs:49`

Impact:

- After a small payload is sent, all later sends on that pooled slot are capped to that smaller size, including future connections that reuse the slot.
- This silently degrades throughput and increases syscall/completion overhead under load.

### 2. `_lifecycleLock` is held across blocking startup and shutdown work

- `Run()` enters `_lifecycleLock` and calls `TrySocketListen()` while still holding it.
- `TrySocketListen()` performs bind/listen attempts and can sleep for one second between retries.
- `ServerStop()` and `Dispose()` also hold `_lifecycleLock` while calling `Shutdown()`, and `Shutdown()` joins `_acceptThread`.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:142`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:168`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:191`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:230`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:785`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:791`

Impact:

- A stop/dispose request cannot interrupt listener retry sleeps because it cannot acquire the same lock needed to cancel shutdown.
- If stop beats the accept thread to `_lifecycleLock`, shutdown can sit in `JoinThread()` until the full 10-second timeout because the accept thread is blocked trying to enter `Run()`.

### 3. `Dispose()` can tear down pooled async state while completions are still in flight

- `Shutdown()` disconnects each active client once, but `TryDeactivateClient()` refuses to recycle a client while `PendingOperations > 0`.
- `Dispose()` then disposes the accept pool, client registry, and cancellation source immediately after shutdown returns.
- Receive/send/accept completions can still arrive on thread-pool callbacks after those shared objects have been disposed.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:675`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:778`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:802`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:408`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:601`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventAcceptPool.cs:96`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientRegistry.cs:104`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:166`

Impact:

- Under load, callbacks can race disposed `SocketAsyncEventArgs`, semaphore state, or client objects during shutdown.

## Medium

### 4. Handles are not invalidated on disconnect, so they still resolve to a reset pooled client until reuse

- `Disconnect()` deactivates and resets the pooled client before invoking `_consumer.OnClientDisconnected(clientHandle)`.
- `ResetForPool()` clears identity, endpoint, timestamps, counters, and marks the slot as pooled.
- `AsyncEventClientHandle.TryGetClient()` only checks generation, and generation is only advanced on the next `Activate()`.

Affected code:

- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:661`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:697`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:139`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClient.cs:235`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventClientHandle.cs:47`
- `/Users/shiba/dev/Arrowgene.Networking/Arrowgene.Networking/Server/AsyncEventServer.cs:718`

Impact:

- `OnClientDisconnected` can observe `[Unknown Client]`, zeroed counters, and cleared endpoint data instead of the disconnected client state.
- Snapshot-based paths like the timeout scanner can still act on a handle that now points at an inactive pooled slot.

## Test Coverage Note

- I did not find a test project in the repository during this review.
- `dotnet test` at the repo root did not discover any tests.
