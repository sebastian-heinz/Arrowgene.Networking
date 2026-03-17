# Shared Pinned Buffer Arena Design

## Summary

The current send path copies each outbound payload twice:

1. caller buffer -> owned queued `byte[]`
2. queued `byte[]` -> `SocketAsyncEventArgs` send buffer

The design should move to one physical pinned array that is shared across receive and send storage.

That pinned array is split into two logical regions:

- a fixed receive region sized for all clients
- a shared send region used as a chunked global send pool

This keeps receive buffers permanently available, removes the send-path re-copy, and allows deterministic upfront memory reservation.

Two modes are proposed:

- `PreallocatedOnly`
  Hard-cap mode. The send region is large enough for every client to hit `MaxQueuedSendBytes` at the same time.

- `PreallocatedThenShared`
  Hybrid mode. The send region is smaller by default and falls back to `ArrayPool<byte>.Shared` when exhausted.

## Current State

Today the library allocates one pinned `BufferSlab` sized as:

```text
MaxConnections * BufferSize * 2
```

Per client that is:

```text
[receive buffer][send buffer]
```

The current send flow is:

1. `SendQueue.Enqueue` allocates a new `byte[]` and copies the caller payload into it
2. `Client.TryPrepareSendChunk` calls `SendQueue.CopyNextChunk`
3. `CopyNextChunk` copies from the queued message into the pinned `SendEventArgs` buffer
4. `TcpServer.StartSend` calls `socket.SendAsync`
5. `SendQueue.CompleteSend` advances the offset and drops the message when finished

That means:

- one allocation per enqueue
- two copies per payload
- exact `MaxQueuedSendBytes` accounting
- a small fixed pinned baseline

## Goals

- Always keep receive storage in pinned memory
- Use one physical pinned array for both receive and preallocated send storage
- Reduce outbound copies from 2 to 1
- Preserve the rule that caller buffers are always copied on send
- Preserve exact per-client `MaxQueuedSendBytes` enforcement
- Support a strict hard-cap mode
- Support a smaller preallocated burst mode with shared fallback

## Why Per-Client Worst-Case Send Slabs Are Still Wrong

The original per-client slab idea scales send memory like this:

```text
MaxConnections * MaxQueuedSendBytes
```

but reserved independently inside every client slot. That is the wrong layout.

The right memory model is:

- receive memory is per-client and fixed
- send queue memory is shared globally across clients

The hard-cap requirement does not require per-client send slabs. It requires a global pinned send budget that is large enough when `PreallocatedOnly` is enabled.

## Proposed Architecture

Use one shared pinned buffer arena owned by `BufferSlab`, then layer a global send allocator on top of its send region.

### Components

- `BufferSlab`
  Owns one pinned `byte[]` and knows the region boundaries.

- receive region
  Fixed per-client receive slices used by `ReceiveEventArgs`.

- send region
  Shared chunked storage used by queued outbound payloads.

- `SendBufferPool`
  Manages chunk allocation inside the send region.

- `SendQueue`
  Copies caller data into owned send blocks backed by the send region, or by shared fallback storage when configured.

## Arena Layout

One physical pinned array:

```text
[recv client 0][recv client 1]...[recv client N-1][send chunk 0][send chunk 1]...[send chunk M-1]
```

Where:

```text
ReceiveRegionBytes = MaxConnections * BufferSize
SendRegionBytes = depends on mode
TotalPinnedBytes = ReceiveRegionBytes + SendRegionBytes
```

## Modes

```csharp
public enum SendStorageMode
{
    PreallocatedOnly = 0,
    PreallocatedThenShared = 1
}
```

### `PreallocatedOnly`

This is the strict hard-cap mode.

The send region is computed as:

```text
SendRegionBytes = MaxConnections * MaxQueuedSendBytes
```

Total pinned bytes become:

```text
TotalPinnedBytes =
    (MaxConnections * BufferSize) +
    (MaxConnections * MaxQueuedSendBytes)
```

Effect:

- all receive buffers are pinned
- all queued send storage is preallocated and pinned
- no `ArrayPool<byte>.Shared` fallback is used
- if the send region cannot satisfy an enqueue, it is treated as backpressure / overflow

This mode gives the cleanest memory guarantee:

- each client is still bounded by `MaxQueuedSendBytes`
- the server also has a deterministic total queued-send memory budget
- every client can hit its own queue cap simultaneously

### `PreallocatedThenShared`

This is the hybrid burst mode.

The send region is configurable, with default:

```text
SendRegionBytes = (MaxConnections * MaxQueuedSendBytes) / 4
```

Total pinned bytes become:

```text
TotalPinnedBytes =
    (MaxConnections * BufferSize) +
    PreallocatedSendPoolBytes
```

Default pinned total:

```text
TotalPinnedBytes =
    (MaxConnections * BufferSize) +
    ((MaxConnections * MaxQueuedSendBytes) / 4)
```

Effect:

- all receive buffers are pinned
- some queued send storage is preallocated and pinned
- if the send region is exhausted, enqueue falls back to `ArrayPool<byte>.Shared`

Important:

- this mode is not a strict memory cap once fallback is enabled
- it is a burst-tolerant mode, not a deterministic cap mode

## Settings

Add:

```csharp
public SendStorageMode SendStorageMode { get; set; }
public int PreallocatedSendPoolBytes { get; set; }
public int SendPoolChunkSize { get; set; }
```

Recommended defaults:

```csharp
SendStorageMode = SendStorageMode.PreallocatedThenShared;
PreallocatedSendPoolBytes = (MaxConnections * MaxQueuedSendBytes) / 4;
SendPoolChunkSize = BufferSize;
```

Rules:

- `PreallocatedSendPoolBytes` applies only to `PreallocatedThenShared`
- `PreallocatedOnly` computes its send region from `MaxConnections * MaxQueuedSendBytes`
- `SendPoolChunkSize` must be positive
- `PreallocatedSendPoolBytes` must be positive in `PreallocatedThenShared`
- the send region size must be a multiple of `SendPoolChunkSize`
- all total-size calculations must use `checked` arithmetic

If strict caps matter, deployments should use `PreallocatedOnly`.

## Memory Examples

With current defaults:

- `MaxConnections = 100`
- `BufferSize = 8192`
- `MaxQueuedSendBytes = 16 MiB`

Receive region:

```text
100 * 8192 = 819,200 bytes
```

### `PreallocatedOnly`

Send region:

```text
100 * 16 MiB = 1,677,721,600 bytes
```

Total pinned:

```text
819,200 + 1,677,721,600 = 1,678,540,800 bytes
```

This is large, but it is explicit and deterministic.

### `PreallocatedThenShared`

Default send region:

```text
(100 * 16 MiB) / 4 = 419,430,400 bytes
```

Total pinned:

```text
819,200 + 419,430,400 = 420,249,600 bytes
```

This is materially smaller, but it trades away the hard-cap guarantee once fallback occurs.

## Send Region Layout

The send region is chunked:

```text
[chunk 0][chunk 1][chunk 2]...[chunk N-1]
```

Where:

```text
ChunkCount = SendRegionBytes / SendPoolChunkSize
```

Each chunk lives inside the single pinned arena and is addressed by:

- shared arena buffer reference
- chunk base offset
- used length inside the chunk

Messages larger than one chunk are split into multiple send blocks in order.

## Queue Storage Model

`SendQueue` should store owned send blocks, not caller buffers:

```csharp
private readonly Queue<SendBlock> _pendingBlocks;
```

Suggested shape:

```csharp
private readonly struct SendBlock
{
    internal SendBlock(
        byte[] buffer,
        int offset,
        int length,
        int poolChunkIndex,
        bool returnToSharedPool)
    {
        Buffer = buffer;
        Offset = offset;
        Length = length;
        PoolChunkIndex = poolChunkIndex;
        ReturnToSharedPool = returnToSharedPool;
    }

    internal byte[] Buffer { get; }
    internal int Offset { get; }
    internal int Length { get; }
    internal int PoolChunkIndex { get; }
    internal bool ReturnToSharedPool { get; }
}
```

Ownership:

- `poolChunkIndex >= 0`
  Block came from the pinned arena send region

- `returnToSharedPool == true`
  Block came from `ArrayPool<byte>.Shared`

Exactly one ownership path should apply for a block.

## Allocation Behavior

### Preferred Path: Arena Send Region

For payload length `data.Length`:

```text
ChunksNeeded = ceil(data.Length / SendPoolChunkSize)
```

Then:

1. enforce exact per-client `_queuedBytes` against `MaxQueuedSendBytes`
2. try to acquire `ChunksNeeded` chunks from `SendBufferPool`
3. copy the payload into those chunk slices
4. enqueue one `SendBlock` per chunk in order

This gives:

- one copy from caller memory into owned storage
- zero extra copy when preparing the actual socket send

### Fallback Path: Shared Pool

If the arena send region cannot satisfy the request:

- in `PreallocatedOnly`, fail the enqueue
- in `PreallocatedThenShared`, rent one array from `ArrayPool<byte>.Shared`, copy once, and enqueue one fallback `SendBlock`

## Send Flow

### Enqueue

1. validate exact per-client `MaxQueuedSendBytes`
2. allocate from the arena send region if possible
3. otherwise use shared fallback if the mode allows it
4. copy the caller data into owned storage
5. enqueue owned `SendBlock` instances

### Prepare

`Client.TryPrepareSendChunk` should stop copying into a dedicated send SAEA buffer.

Instead it should bind `SocketAsyncEventArgs` directly to the current block:

```csharp
int remaining = _currentBlock.Length - _currentOffset;
chunkSize = remaining <= maxChunkSize ? remaining : maxChunkSize;
sendEventArgs.SetBuffer(_currentBlock.Buffer, _currentBlock.Offset + _currentOffset, chunkSize);
```

For preallocated blocks, `Buffer` is the single pinned arena.

For fallback blocks, `Buffer` is the rented shared array.

### Complete

When a block is fully sent:

- if it came from the arena send region, return the chunk index to `SendBufferPool`
- if it came from `ArrayPool<byte>.Shared`, return the array there

Partial sends keep the current block and only advance the block-local offset.

## Why This Design Is Better

| Concern | Per-client send slabs | Shared pinned arena |
|---|---|---|
| Physical pinned objects | Many logical per-client regions | One physical pinned array |
| Receive buffers always pinned | Yes | Yes |
| Send-path re-copy | Removed | Removed |
| Hard-cap mode available | Yes | Yes |
| Shared send budget | No | Yes |
| Burst mode available | Poor fit | Yes |
| Complexity | High | Moderate |

## Important Tradeoffs

### 1. `PreallocatedOnly` Is Expensive By Design

If the goal is "every client can hit `MaxQueuedSendBytes` simultaneously without any fallback", the total pinned size will be large. That is expected.

This is not a design flaw. It is the cost of a deterministic worst-case guarantee.

### 2. `PreallocatedThenShared` Is Not Hard-Capped

Once fallback to `ArrayPool<byte>.Shared` is enabled, total send memory is no longer strictly bounded by the pinned arena size.

### 3. Chunk Size Controls Fragmentation

Large chunk sizes reduce allocator metadata overhead but waste more space for small messages.

Small chunk sizes improve packing but increase chunk-management overhead.

## Detailed Changes

### `TcpServerSettings`

Add:

```csharp
public SendStorageMode SendStorageMode { get; set; }
public int PreallocatedSendPoolBytes { get; set; }
public int SendPoolChunkSize { get; set; }
```

Validation:

- `SendPoolChunkSize > 0`
- `PreallocatedSendPoolBytes > 0` when `SendStorageMode == SendStorageMode.PreallocatedThenShared`
- `PreallocatedSendPoolBytes % SendPoolChunkSize == 0`
- `checked(MaxConnections * BufferSize)`
- `checked(MaxConnections * MaxQueuedSendBytes)`
- `checked(ReceiveRegionBytes + SendRegionBytes)`

### `BufferSlab`

Refactor it to own the whole pinned arena.

Suggested responsibilities:

- allocate the single pinned `byte[]`
- expose receive offsets for each client
- expose the send region base offset and length
- create receive `SocketAsyncEventArgs` with fixed receive slices
- create send `SocketAsyncEventArgs` without a permanent bound buffer

### `SendBufferPool`

Add a helper responsible for chunk allocation inside the send region of `BufferSlab`.

Suggested members:

```csharp
internal sealed class SendBufferPool
{
    internal byte[] Buffer { get; }
    internal int SendRegionBaseOffset { get; }

    internal bool TryAcquireChunks(int chunkCount, Span<int> destination);
    internal void ReleaseChunk(int chunkIndex);
}
```

### `SendQueue`

Rewrite queue storage around `SendBlock`.

Requirements:

- keep `_queuedBytes` exact
- preserve send order
- support partial sends
- release all owned storage in `Reset`
- never expose caller-owned arrays directly

### `Client`

`TryPrepareSendChunk` still validates generation and aliveness, but it should no longer copy into `SendEventArgs.Buffer`.

It should bind the current `SendBlock` slice directly using `SetBuffer(...)`.

### `TcpServer`

Construct one `BufferSlab` that contains both receive and send regions.

Construct one `SendBufferPool` over the send region of that same `BufferSlab`.

Pass the shared send pool into each `Client` / `SendQueue`.

The rest of `StartSend` and `ProcessSend` can keep the current shape because they already support:

- single in-flight send
- synchronous completion
- asynchronous completion
- partial send continuation

## Metrics

Add:

- `PreallocatedSendPoolExhaustions`
- `SharedSendFallbacks`

Meaning:

- `PreallocatedSendPoolExhaustions`
  Count every time the arena send region cannot satisfy an allocation request

- `SharedSendFallbacks`
  Count every time `PreallocatedThenShared` successfully switches to shared rented storage

## Testing

### Unit Tests

Add tests for:

1. receive offsets stay fixed and do not overlap send-region offsets
2. `PreallocatedOnly` computes total pinned bytes using the full worst-case send formula
3. `PreallocatedThenShared` computes total pinned bytes using the configured send-pool size
4. enqueue uses arena chunks and returns them after completion
5. exhaustion in `PreallocatedOnly` fails without fallback
6. exhaustion in `PreallocatedThenShared` falls back to shared rented storage
7. exact per-client `MaxQueuedSendBytes` still applies by bytes
8. partial sends advance offsets correctly across multiple completions
9. `Reset` returns both pinned chunks and shared fallback buffers
10. caller mutation after `Send` does not affect queued data

### Integration Tests

Re-run existing send and overflow tests and add:

1. `PreallocatedOnly` disconnect behavior still matches send-queue overflow expectations
2. `PreallocatedThenShared` records fallback metrics and still delivers payloads
3. recycled clients do not retain pinned send chunks from earlier generations

## Verdict

Use one physical pinned buffer arena shared across receive and preallocated send storage.

For strict deterministic memory behavior, use `PreallocatedOnly`.

For lower upfront memory with burst tolerance, use `PreallocatedThenShared`.
