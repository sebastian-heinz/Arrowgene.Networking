# Phase 1: Low-Level Requirement Identification

Analysis of `AsyncEventServer.cs` and its supporting classes to identify the low-level requirements necessary for high-performance networking.

---

## 1. Pinned Memory Management

The server must prevent the GC from relocating buffers while async I/O operations are in flight. The current implementation addresses this with a single allocation strategy:

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **M1** | **Allocate a pinned byte array at construction time** that the GC cannot relocate during async I/O. | `GC.AllocateArray<byte>(totalBufferSize, true)` — the `true` parameter pins the array on the POH (Pinned Object Heap). | `AsyncEventServer.cs:76` |
| **M2** | **Size the buffer for all concurrent connections**, covering both read and write regions. | `bufferSize * maxConnections * 2` — one read slice + one write slice per client. | `AsyncEventServer.cs:72` |
| **M3** | **Assign fixed, non-overlapping buffer regions** to each client's `SocketAsyncEventArgs` at construction, not at runtime. | Sequential `SetBuffer(_buffer, bufferOffset, _bufferSize)` calls during the constructor loop. Read SAEA gets `[offset, offset+bufferSize)`, write SAEA gets `[offset+bufferSize, offset+2*bufferSize)`. | `AsyncEventServer.cs:106-116` |
| **M4** | **Keep the buffer reference alive** for the entire server lifetime so the pinned allocation is not collected. | `private readonly byte[] _buffer` field on the server. | `AsyncEventServer.cs:39` |

**Key observation:** There is no standalone `BufferManager` class — all buffer management is inlined in the `AsyncEventServer` constructor. The `_buffer` field comment (`// ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable`) confirms it exists solely to prevent collection.

---

## 2. Object Reuse (Pooling)

Three distinct pools exist, each with different lifecycle mechanics:

### 2a. Client Object Pool

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **P1** | **Preallocate all client objects** at construction, not on-demand. | `_allClients[maxConnections]` array + `Stack<AsyncEventClient> _clientPool` initialized with all clients. | `AsyncEventServer.cs:96-127` |
| **P2** | **Pop on accept, push on recycle** — deterministic acquire/release. | `_clientPool.TryPop()` in `ProcessAccept`, `_clientPool.Push(client)` in `TryRecycleClient`. | `AsyncEventServer.cs:521`, `981` |
| **P3** | **Guard with a state machine** (`Initialize -> Alive -> Shutdown -> Pooled`) so a client cannot be double-recycled or used while pooled. | `AsyncEventClient._isAlive` + `_isInPool` flags, guarded by `_lock`. `Initialize()` sets alive=true/inPool=false; `TryBeginShutdown()` sets alive=false; `TryMarkPooled()` sets inPool=true. | `AsyncEventClient.cs:82-104`, `135-148`, `182-199` |
| **P4** | **Track pending async operations** per client and only recycle when the count reaches zero. | `Interlocked.Increment/Decrement` on `_pendingOperations`. `TryRecycleClient` checks `PendingOperations > 0` before returning to pool. | `AsyncEventClient.cs:150-158`, `AsyncEventServer.cs:960-983` |
| **P5** | **Reset per-client write state** on recycle to clear stale queued data. | `client.WriteState.Reset()` called in `TryRecycleClient`. | `AsyncEventServer.cs:977`, `AsyncEventWriteState.cs:24-35` |

### 2b. Accept SAEA Pool

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **P6** | **Preallocate accept `SocketAsyncEventArgs`** (fixed count = 10). | `_allAcceptEventArgs[NumAccepts]` + `Stack<SocketAsyncEventArgs> _acceptPool`. | `AsyncEventServer.cs:129-135` |
| **P7** | **Gate concurrent accepts** with a counting semaphore to prevent pool exhaustion. | `SemaphoreSlim _maxNumberAccepts(NumAccepts, NumAccepts)` — `Wait()` before popping, `Release()` after pushing. | `AsyncEventServer.cs:85`, `369`, `996` |
| **P8** | **Clear `AcceptSocket`** before returning the SAEA to the pool (required by the framework). | `acceptEventArgs.AcceptSocket = null` in `ReturnAcceptEventArgs`. | `AsyncEventServer.cs:987` |

### 2c. Generation-Guarded Recycling

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **P9** | **Per-client generation counter** to detect stale handles after recycling. | `_clientGenerations[clientId]` incremented on each accept via `Interlocked.Increment`. `AsyncEventClientHandle` captures the generation at creation and compares on every access. | `AsyncEventServer.cs:41,548`, `AsyncEventClientHandle.cs:18-23,27-37` |
| **P10** | **Per-server run generation** to invalidate all pending IOCP callbacks from a previous start/stop cycle. | `_runGeneration` incremented via `Interlocked.Increment` on both `ServerStart` and `ServerStop`. Accept SAEA carries `runGeneration` as `UserToken`; `ProcessAccept` validates it. | `AsyncEventServer.cs:61,201,257,462-483` |
| **P11** | **Lightweight handle (`readonly struct`)** for external consumers, so pooled client internals are never directly exposed. | `AsyncEventClientHandle` is a `readonly struct` that wraps a `AsyncEventClient` reference + generation. All `ITcpSocket` property accesses go through a generation check. | `AsyncEventClientHandle.cs:7-86` |

---

## 3. Zero-Copy Principles

The codebase has partial zero-copy support with an important gap:

### Receive Path

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **Z1** | **Pass buffer/offset/count** from the SAEA directly to the data processing method, avoiding an immediate copy. | `AsyncEventServer.ProcessReceive` calls `OnReceivedData(clientHandle, readEventArgs.Buffer, readEventArgs.Offset, readEventArgs.BytesTransferred)`. | `AsyncEventServer.cs:710-711` |
| **Z2** | **Provide a zero-copy consumer interface** that receives (buffer, offset, count) so consumers can parse data in-place from the pinned buffer. | `IBufferConsumer.OnReceivedData(ITcpSocket, byte[], int, int)` exists as an opt-in interface. | `IBufferConsumer.cs:10` |
| **Z3** | **Current gap:** `TcpServer.OnReceivedData(buffer, offset, count)` **always copies** into a new `byte[]` before calling `IConsumer.OnReceivedData`. `IBufferConsumer` is defined but never consulted. | `byte[] data = new byte[count]; Buffer.BlockCopy(buffer, offset, data, 0, count);` — this is a mandatory allocation + copy for every receive. | `TcpServer.cs:38-41` |

### Send Path

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **Z4** | **Defer copy to send time** — enqueued `byte[]` references are stored without copying in the write queue. | `_sendQueue.Enqueue(data)` stores the caller's array reference directly. | `AsyncEventWriteState.cs:53` |
| **Z5** | **Copy into the pinned buffer only when sending** — `Buffer.BlockCopy` from the queued data into the SAEA's pinned buffer region, sized to the chunk limit. | `Buffer.BlockCopy(data, dataOffset, writeEventArgs.Buffer, writeEventArgs.Offset, chunkSize)` in `StartSend`. | `AsyncEventServer.cs:789` |
| **Z6** | **Chunk large payloads** to fit the SAEA buffer size, tracking progress with `_transferredCount`/`_outstandingCount`. | `TryGetSendChunk` returns min(outstandingCount, maxChunkSize). `CompleteSend` advances the progress cursor and dequeues the next message when the current one finishes. | `AsyncEventWriteState.cs:72-97,99-130` |

---

## 4. Concurrency Model

The server runs on IOCP threads with five distinct synchronization strategies used simultaneously:

### IOCP Completion Threading

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **C1** | **Async I/O via `SocketAsyncEventArgs`** — completions fire on ThreadPool IOCP threads, not dedicated threads. | `AcceptAsync`, `ReceiveAsync`, `SendAsync` return `bool`; `false` = synchronous completion (process inline), `true` = `Completed` event fires on a pool thread. | `AsyncEventServer.cs:408,617,795` |
| **C2** | **Synchronous completion fallback** — when the async method returns `false`, process inline on the calling thread to avoid an unnecessary context switch. | `if (!willRaiseEvent) { ProcessAccept/ProcessReceive/ProcessSend(...); }` | `AsyncEventServer.cs:424-428,634-645,812-823` |

### Lock Strategy

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **C3** | **Fine-grained locks per concern**, not a single global lock. | `_clientLock` (pool + handle list + UnitOfOrder counters), `_acceptPoolLock` (accept pool + semaphore), `_isRunningLock` (start/stop lifecycle), `_lock` per-client (state machine), `_sendLock` per-client (write queue). | Server: `51-53`, Client: `49`, WriteState: `13` |
| **C4** | **Lock scope minimization** — locks are held only for data structure mutations, never across I/O operations. | e.g., `ProcessAccept` acquires `_clientLock` only for pool pop + handle list add, then releases before calling `OnClientConnected` and `StartReceive`. | `AsyncEventServer.cs:500-558` |

### Atomic Operations

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **C5** | **`Interlocked` for counters** — pending operations, byte counters, generation counters. | `Interlocked.Increment/Decrement` for `_pendingOperations`; `Interlocked.Add` for `_bytesReceived/_bytesSend`; `Interlocked.Increment` for `_clientGenerations[]` and `_runGeneration`. | `AsyncEventClient.cs:150-158,168,179`, `AsyncEventServer.cs:201,257,548` |
| **C6** | **`Volatile.Read/Write` for tick timestamps and flags** — ensures visibility across threads without a full lock. | `_lastReadTicks` / `_lastWriteTicks` via `Volatile.Write`/`Volatile.Read`; `_isRunning` is `volatile bool`. | `AsyncEventClient.cs:19-20,115-116,167,178`, `AsyncEventServer.cs:63` |

### Pending Operations Pattern

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **C7** | **Bracket every async I/O with increment-before / decrement-in-finally** to prevent premature recycling while IOCP callbacks are outstanding. | `IncrementPendingOperations()` before `ReceiveAsync`/`SendAsync`; `DecrementPendingOperations()` in the `finally` block of `Receive_Completed`/`Send_Completed`. On synchronous completion, the same try/finally pattern applies. | `AsyncEventServer.cs:613-646,662-671,791-823,840-849` |

### Consumer-Side Parallelism

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **C8** | **UnitOfOrder assignment** — each accepted client is assigned to the bucket with the fewest current clients (simple load balancing). | Linear scan of `_unitOfOrders[]` to find the minimum, then increment. | `AsyncEventServer.cs:534-546` |
| **C9** | **Per-UnitOfOrder threading** in the consumer layer — `ThreadedBlockingQueueConsumer` creates one `BlockingCollection<ClientEvent>` and one dedicated `Thread` per UnitOfOrder. Events for the same client always land on the same thread. | `_queues[socket.UnitOfOrder].Add(...)` routes events; `Consume(unitOfOrder)` blocks on `Take()`. | `ThreadedBlockingQueueConsumer.cs:38-71,98-111` |

---

## 5. Backpressure & Flow Control

| Req | Requirement | Current Implementation | Source |
|-----|------------|----------------------|--------|
| **B1** | **Hard connection limit** — the pool size equals `MaxConnections`; when exhausted, new connections are rejected immediately. | `_clientHandles.Count >= _maxConnections` check + `_clientPool.TryPop` failure. Both paths close the accepted socket. | `AsyncEventServer.cs:509-531` |
| **B2** | **Accept throttling** — a counting semaphore (`SemaphoreSlim`) limits the number of concurrent in-flight accept operations to `NumAccepts` (10). | `_maxNumberAccepts.Wait()` blocks the accept loop until a slot is available; `_maxNumberAccepts.Release()` returns the slot after each accept completes. | `AsyncEventServer.cs:85,369,996` |
| **B3** | **Per-client write queue cap** — outbound data is capped at 16 MB (`MaxQueuedBytes`). If `EnqueueSend` would exceed this, the enqueue is rejected with `queueOverflow = true`. | `if (data.Length > MaxQueuedBytes - _queuedBytes) { queueOverflow = true; return false; }` | `AsyncEventWriteState.cs:7,47-51` |
| **B4** | **Disconnect on write overflow** — when the write queue overflows, the server disconnects the client to release resources rather than silently dropping data. | `if (queueOverflow) { DisconnectClient(clientHandle); }` | `AsyncEventServer.cs:759-763` |
| **B5** | **Socket timeout / half-open detection** — a background thread polls all active clients' last-activity timestamps and disconnects any whose idle time exceeds the configured timeout. | `CheckSocketTimeout()` compares `Environment.TickCount64` against `LastReadTicks`/`LastWriteTicks`; polls every `clamp(socketTimeoutMs, 1000, 30000)` ms. | `AsyncEventServer.cs:1009-1057` |
| **B6** | **Orderly drain on shutdown** — `WaitForDrain` spin-waits (10ms intervals, up to 10s) for three conditions: all pending operations complete, all clients return to pool, all accept SAEA return to pool. Only then are resources disposed. | `WaitForClientOperationsDrain`, `WaitForClientPoolDrain`, `WaitForAcceptPoolDrain` — all checked in `ServerStop` and `Dispose`. | `AsyncEventServer.cs:1120-1196` |
| **B7** | **In-flight send preservation during disconnect** — `ClearQueuedSends` clears the queue but preserves the in-flight chunk's state so that any outstanding send completion callback remains consistent. | `if (_outstandingCount > 0) { _queuedBytes = _outstandingCount; _sendInProgress = true; return; }` | `AsyncEventWriteState.cs:132-151` |

---

## Identified Gaps

While analyzing the requirements, two structural issues stand out as candidates for refactoring:

1. **Z3 — Zero-copy receive not wired up.** `IBufferConsumer` exists but `TcpServer.OnReceivedData(buffer, offset, count)` unconditionally copies into a new `byte[]` at `TcpServer.cs:38-41`. This means every received packet incurs a heap allocation + `BlockCopy`, negating the zero-copy intent of the pinned buffer.

2. **No extracted buffer/pool abstractions.** All buffer management (M1-M4) and pooling logic (P1-P8) is inlined directly in `AsyncEventServer`'s constructor and methods. There are no reusable `BufferManager`, `ObjectPool<T>`, or `SaeaPool` classes, making the server class ~1200 lines and difficult to test or extend independently.
