# Send Queue Rewrite Evaluation

## Summary

The optimization target is real: the current send path copies each payload twice and allocates one `byte[]` per enqueue. That is worth fixing.

The original per-client slab proposal is not a good fit as written. It removes the second copy, but it does so by reserving worst-case pinned send capacity for every pooled client slot up front. With the current defaults, that changes the pinned slab from about `1.56 MiB` to about `1.56 GiB`. It also weakens the meaning of `MaxQueuedSendBytes` because capacity becomes rounded to slab count instead of tracked by exact queued bytes.

The better direction is a pooled single-copy queue:

1. Copy the caller's buffer once on enqueue into an internal rented buffer.
2. Point `SocketAsyncEventArgs` directly at that queued buffer during send.
3. Return the buffer to the pool when the message is fully sent or the client is reset.

This keeps the existing ownership rule, removes the hot-path re-copy, avoids per-enqueue GC allocations, and preserves lazy memory growth.

## Current State

Today the send path works like this:

1. `SendQueue.Enqueue` allocates a new `byte[]` and copies the caller payload into it.
2. `Client.TryPrepareSendChunk` calls `SendQueue.CopyNextChunk`.
3. `CopyNextChunk` copies from the queued message into the pinned `SendEventArgs` buffer.
4. `TcpServer.StartSend` calls `socket.SendAsync`.
5. `SendQueue.CompleteSend` advances the offset and drops the message when finished.

That means:

- One allocation per enqueue.
- Two copies per payload.
- Exact `MaxQueuedSendBytes` accounting.
- Very small pinned baseline: `maxConnections * bufferSize * 2`.

## What Is Good In The Original Proposal

- It targets the right bottleneck: the second copy from queued heap buffer into the send SAEA buffer.
- It keeps copy-on-enqueue ownership, so callers still cannot mutate queued payloads after calling `Send`.
- It preserves the existing single in-flight send model and partial-send handling.
- It would remove hot-path allocations entirely if the memory model were acceptable.

## What Is Not Good

### 1. Pinned Memory Explosion

The proposal turns `MaxQueuedSendBytes` into reserved pinned memory for every pooled client, not just for clients that are actively backed up.

With current defaults:

- Current slab: `100 * 8192 * 2 = 1,638,400` bytes, about `1.56 MiB`
- Proposed slab count: `ceil(16 MiB / 8192) = 2048`
- Proposed slab: `100 * 8192 * (1 + 2048) = 1,678,540,800` bytes, about `1.56 GiB`

That is roughly a `1000x` increase in baseline pinned memory before any client sends a byte.

### 2. `MaxQueuedSendBytes` Stops Meaning Exact Bytes

The current queue enforces the limit by exact queued bytes:

```csharp
if (data.Length > _maxQueuedBytes - _queuedBytes)
```

The slab design enforces capacity by free slab count. That means small sends waste the unused tail of each slab.

Example with `BufferSize = 256` and `MaxQueuedSendBytes = 512`:

- Current queue accepts `512` one-byte sends before overflow.
- A two-slab design overflows on the third one-byte send if each message takes its own slab.

That is a major behavioral regression for workloads with many small packets.

### 3. Worst-Case Provisioning Is Bound To `MaxConnections`

The current design pays for queue memory only when a client actually has queued outbound data.

The slab proposal reserves the worst-case queue size for every pooled `Client`, including idle slots. That is the wrong scaling factor for this library. The expensive part should scale with actual queued bytes, not with the size of the connection pool.

### 4. `BufferSlab` Becomes A Large Allocator Instead Of A Simple Transport Slab

Right now `BufferSlab` is easy to reason about:

```text
[recv][send]
```

The proposal turns it into:

```text
[recv][send slab 0][send slab 1]...[send slab N-1]
```

That couples queue storage, queue policy, and socket buffer layout into one object. It makes the send queue rewrite more invasive than it needs to be and raises the cost of future buffer changes.

### 5. The Testing Section Is Too Optimistic

The statement that all existing tests should pass without modification is not strong enough for a change this invasive.

If the implementation changes queue accounting, send buffer ownership, and reset behavior, the following must be revalidated explicitly:

- Overflow behavior with many small sends
- Partial sends across multiple callbacks
- Reset and disconnect cleanup returning all internal buffers
- Stale handle and client recycle behavior
- Metrics integration for `SendQueueOverflow`

## Recommended Design

### Pooled Single-Copy Queue

Keep the public semantics and the current send loop, but replace the internal queue storage.

### Core Idea

- On enqueue, rent a buffer from `ArrayPool<byte>.Shared`
- Copy the caller payload into the rented buffer
- Queue `(buffer, length)`
- During send, call `SendEventArgs.SetBuffer(buffer, offset, chunkSize)` directly on the queued buffer
- When the message is fully sent, return the buffer to the pool

This keeps the required copy from caller-owned memory to server-owned memory, but removes the internal re-copy into a separate send slab.

### Why This Is Better

| Concern | Per-client slab plan | Pooled direct-buffer plan |
|---|---|---|
| Ownership isolation | Preserved | Preserved |
| Copies per payload | 1 | 1 |
| Per-enqueue GC allocation | 0 | 0 in steady state |
| Baseline pinned memory | Very high | Low |
| `MaxQueuedSendBytes` accuracy | Rounded by slabs | Exact bytes |
| Memory growth | Worst-case upfront | Lazy, load-driven |
| Code churn | High | Moderate |

### Recommended Implementation Shape

#### `SendQueue`

Replace `Queue<byte[]>` with a queue of pooled payload tuples:

```csharp
private readonly Queue<(byte[] Buffer, int Length)> _pendingMessages;
```

`Enqueue` becomes:

```csharp
byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
Buffer.BlockCopy(data, 0, rented, 0, data.Length);
_pendingMessages.Enqueue((rented, data.Length));
_queuedBytes += data.Length;
```

`TryPrepareSendChunk` should no longer copy into a transport scratch buffer. It should point the send args directly at the queued message:

```csharp
int remaining = _currentMessage.Length - _currentOffset;
chunkSize = remaining <= maxChunkSize ? remaining : maxChunkSize;
sendEventArgs.SetBuffer(_currentMessage.Buffer, _currentOffset, chunkSize);
```

When a message completes, return it:

```csharp
ArrayPool<byte>.Shared.Return(_currentMessage.Buffer);
```

`Reset` must return both the current message buffer and every queued pending buffer.

#### `Client`

`TryPrepareSendChunk` can keep the current shape:

```csharp
internal bool TryPrepareSendChunk(uint generation, int maxChunkSize, out int chunkSize)
```

The important change is that it should delegate to `SendQueue` to bind `SendEventArgs` directly to the queued buffer rather than copying into `SendEventArgs.Buffer`.

#### `TcpServer`

`StartSend` and `ProcessSend` can stay almost unchanged. They already support partial sends and repeated completion callbacks.

The only required change is that `SendEventArgs` is no longer permanently tied to a dedicated send region in `BufferSlab`.

## Optional Follow-Up

Once the pooled direct-buffer queue is stable, the dedicated send half of `BufferSlab` can be removed entirely.

That would shrink the transport slab from:

```text
maxConnections * bufferSize * 2
```

to:

```text
maxConnections * bufferSize
```

At that point `BufferSlab` becomes receive-only, and `CreateSendEventArgs` can simply create a `SocketAsyncEventArgs` without assigning a fixed buffer up front.

This is a good follow-up because it is independent from the queue rewrite and easy to verify once direct-buffer sending already works.

## If A Slab Design Is Still Desired

If the project still wants slab-backed send storage, the slab pool should be global and demand-driven, not reserved per client.

That avoids multiplying worst-case queue capacity by `MaxConnections`. Even then, the design still needs an exact-byte accounting strategy. A slab-per-message queue is not enough because it wastes too much capacity on small writes.

That version is more complex than the pooled direct-buffer approach and should only be considered if profiling shows `ArrayPool` is not sufficient.

## Recommended Plan

1. Rewrite `SendQueue` to store pooled owned buffers instead of freshly allocated arrays.
2. Change `TryPrepareSendChunk` to bind `SendEventArgs` directly to the queued buffer segment.
3. Keep exact byte-based overflow accounting.
4. Add explicit tests for buffer return on complete and reset.
5. After that lands, remove the fixed send region from `BufferSlab` as a follow-up.

## Testing

### Unit Tests

Add focused tests around the new queue behavior:

1. Enqueue copies the caller buffer and later caller mutations do not affect the queued payload.
2. `MaxQueuedSendBytes` is enforced by exact bytes, including many small messages.
3. Partial sends advance the offset correctly across multiple completions.
4. `Reset` returns the current in-flight buffer and all pending queued buffers.
5. Completing the final chunk returns the rented buffer and clears send-in-progress state.

### Integration Tests

Re-run the existing server tests and add explicit assertions for:

1. Send queue overflow still disconnects with `DisconnectReason.SendQueueOverflow`.
2. Disconnect cleanup after pending sends does not leak queued buffers.
3. A recycled `ClientHandle` cannot send using buffers owned by a later generation.

## Verdict

Do not implement the original per-client slab plan as written.

Implement a pooled single-copy queue first. It addresses the real problem, matches the project's copy-on-enqueue rule, preserves exact queue semantics, keeps memory proportional to actual load, and requires much less invasive change.
