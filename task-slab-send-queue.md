# Send Queue Modes

## Summary

The current send path copies each outbound payload twice:

1. caller buffer -> owned queued `byte[]`
2. queued `byte[]` -> `SocketAsyncEventArgs` send buffer

The design should move to two explicit modes:

1. `HardCapped`
   One pinned buffer arena with dedicated per-client regions for both receive and send slabs. No global allocator, no cross-client contention, fully lock-free between clients.

2. `Shared`
   Only receive buffers are preallocated in pinned memory. Queued send storage uses `ArrayPool<byte>.Shared`.

Each mode has one coherent storage model with no mixed ownership.

## Current State

Today `BufferSlab` allocates:

```text
MaxConnections * BufferSize * 2
```

Per client:

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
- fixed receive/send transport buffer allocation

The old per-client send slice (the `× 2` factor) is removed in both modes. It is replaced by either dedicated per-client send slabs (`HardCapped`) or rented buffers (`Shared`).

## Goals

- Keep receive buffers permanently preallocated and pinned
- Reduce outbound copies from 2 to 1
- Preserve the rule that caller buffers are always copied on send
- Preserve exact per-client `MaxQueuedSendBytes` enforcement
- Support one strict hard-cap mode with no global allocator
- Support one simpler shared-send mode
- Keep synchronization understandable and defensible

## Modes

```csharp
public enum SendStorageMode
{
    Shared = 0,
    HardCapped = 1
}
```

`Shared` is the default. It is simpler, uses less upfront memory, and delivers the core benefit (two copies to one copy) without any new synchronization concerns. `HardCapped` is opt-in for deployments that need deterministic memory.

## Mode 1: `HardCapped`

### Intent

Provide deterministic memory usage with zero cross-client contention. Every client has its own dedicated receive and send region in one pinned buffer. No global send allocator exists.

### Layout

One physical pinned array with dedicated per-client regions:

```text
[recv0][send0-0][send0-1]...[send0-N][recv1][send1-0][send1-1]...[send1-N]...
```

Each client block contains:

```text
[receive buffer][send slab 0][send slab 1]...[send slab N-1]
```

Where:

```text
SendSlabCount = ceil(MaxQueuedSendBytes / BufferSize)
ClientBlockSize = BufferSize + (SendSlabCount * BufferSize)
              = BufferSize * (1 + SendSlabCount)
TotalPinnedBytes = MaxConnections * ClientBlockSize
```

So total pinned bytes are:

```text
MaxConnections * BufferSize * (1 + ceil(MaxQueuedSendBytes / BufferSize))
```

### Behavior

- all receive buffers are pinned and preallocated
- all queued send storage is pinned and preallocated per client
- no shared rented send fallback exists
- no global send allocator exists
- enqueue can only fail because the client's own slabs are exhausted
- overflow is always per-client, never cross-client

### Why Per-Client Regions

The previous shared-arena `HardCapped` design required a global chunk allocator accessed by all clients concurrently. That introduced:

- a new lock or lock-free structure shared across all IOCP callbacks and user send calls
- lock ordering concerns (`Client._sync` → `SendQueue._sync` → `SendBufferPool`)
- a new failure mode where client A fails because clients B through Z consumed the global pool
- all-or-nothing acquisition logic with rollback considerations

Per-client regions eliminate all of these. Each client's send slabs are managed by that client's existing `SendQueue` lock. No cross-client synchronization is needed. Overflow semantics match today exactly: a client fails only when its own queue is full.

The cost is memory. With defaults (100 connections, 8 KiB buffer, 16 MiB max queue), each client block is ~16.008 MiB, total ~1.56 GiB. This is the price of a deterministic per-client worst-case guarantee. It is explicit and expected.

### Synchronization

No new synchronization. The existing lock hierarchy is unchanged:

```text
Client._sync -> SendQueue._sync
```

Each client's free slab stack lives inside `SendQueue` and is protected by `SendQueue._sync`. No global lock, no cross-client contention.

### `SendBlock` Shape

Since all blocks come from the same per-client region in the pinned arena, the block only needs a slab index and a used length:

```csharp
private readonly struct SendBlock
{
    internal SendBlock(int slabIndex, int length)
    {
        SlabIndex = slabIndex;
        Length = length;
    }

    internal int SlabIndex { get; }
    internal int Length { get; }
}
```

The buffer reference, base offset, and slab size are known to the `SendQueue` instance. No ownership flag needed — all blocks belong to the same per-client arena region.

### Allocation Rules

For payload length `data.Length`:

1. validate exact per-client `_queuedBytes` against `MaxQueuedSendBytes`
2. compute `chunksNeeded = ceil(data.Length / slabSize)`
3. check `chunksNeeded <= freeSlabs.Count`
4. pop slabs, copy payload chunks into them, enqueue `SendBlock` per chunk
5. update `_queuedBytes`

If step 3 fails, the enqueue fails with queue overflow. This is the same per-client overflow as today.

### Send Flow

Enqueue:

```csharp
int offset = 0;
while (offset < data.Length)
{
    int slabIndex = _freeSlabs.Pop();
    int chunkLen = Math.Min(_slabSize, data.Length - offset);
    int slabOffset = _slabBaseOffset + slabIndex * _slabSize;
    Buffer.BlockCopy(data, offset, _slabBuffer, slabOffset, chunkLen);
    _pendingBlocks.Enqueue(new SendBlock(slabIndex, chunkLen));
    offset += chunkLen;
}
_queuedBytes += data.Length;
```

Prepare (zero copy):

```csharp
int bufferOffset = _slabBaseOffset + _currentBlock.SlabIndex * _slabSize + _currentSlabOffset;
int remaining = _currentBlock.Length - _currentSlabOffset;
sendEventArgs.SetBuffer(_slabBuffer, bufferOffset, remaining);
```

Complete:

```csharp
_currentSlabOffset += bytesTransferred;
_queuedBytes -= bytesTransferred;

if (_currentSlabOffset >= _currentBlock.Length)
{
    _freeSlabs.Push(_currentBlock.SlabIndex);
    // advance to next block or mark idle
}
```

## Mode 2: `Shared`

### Intent

Keep the receive side fixed and simple, but keep queued send storage adaptive with minimal upfront memory.

### Layout

The pinned arena contains receive storage only:

```text
[recv client 0][recv client 1]...[recv client N-1]
```

Where:

```text
TotalPinnedBytes = MaxConnections * BufferSize
```

There is no preallocated send region.

### Behavior

- all receive buffers are pinned and preallocated
- queued send storage uses `ArrayPool<byte>.Shared`
- send storage is not hard-capped by the pinned arena
- per-client `MaxQueuedSendBytes` still applies exactly

### Send Ownership Model

`SendQueue` stores owned rented buffers:

```csharp
private readonly Queue<(byte[] Buffer, int Length)> _pendingMessages;
```

Enqueue:

```csharp
byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
Buffer.BlockCopy(data, 0, rented, 0, data.Length);
_pendingMessages.Enqueue((rented, data.Length));
_queuedBytes += data.Length;
```

Prepare (note: uses stored `Length`, not `rented.Length`, because `ArrayPool` may return a larger array):

```csharp
int remaining = _currentLength - _currentOffset;
chunkSize = remaining <= maxChunkSize ? remaining : maxChunkSize;
sendEventArgs.SetBuffer(_currentBuffer, _currentOffset, chunkSize);
```

Complete:

```csharp
ArrayPool<byte>.Shared.Return(_currentBuffer);
```

Reset must return both the current in-flight buffer and all pending queued buffers.

### Synchronization

No new synchronization. Lock hierarchy unchanged:

```text
Client._sync -> SendQueue._sync
```

No global allocator, no cross-client contention, no lock ordering concerns.

## Implementation: Two Separate Classes

`SendQueue` should be two separate implementations, not one class with mode branches.

Each mode has different fields, different allocation logic, different block types, and different cleanup paths. A mode flag checked in every method would add unnecessary branching and make each implementation harder to read and verify.

Suggested approach:

- Define a common interface or abstract base with the shared contract: `Enqueue`, `TryGetNextChunk`, `CompleteSend`, `Reset`, `GetQueuedBytes`
- `ArenaBackedSendQueue` implements `HardCapped` with per-client slab management
- `SharedSendQueue` implements `Shared` with `ArrayPool` rent/return

`Client` holds the appropriate implementation based on the configured `SendStorageMode`. The rest of the send pipeline (`TryPrepareSendChunk`, `StartSend`, `ProcessSend`) is identical for both — they just call the interface methods.

## Why These Two Modes Are Better

| Concern | `HardCapped` | `Shared` |
|---|---|---|
| Receive buffers preallocated | Yes | Yes |
| Queued send storage preallocated | Yes, per client | No |
| Hard-cap behavior | Yes, per client | No |
| Global send allocator | None | None |
| Cross-client contention | None | None |
| Mixed ownership model | No | No |
| New synchronization risk | None | None |
| Memory behavior clarity | High | High |

Both modes avoid a global send allocator entirely. `HardCapped` achieves this through per-client dedicated regions. `Shared` achieves this by using `ArrayPool`.

## Settings

Add:

```csharp
public SendStorageMode SendStorageMode { get; set; }
```

Default:

```csharp
SendStorageMode = SendStorageMode.Shared;
```

`SendPoolChunkSize` is not needed as a separate setting. In `HardCapped`, slabs are always `BufferSize`. In `Shared`, there is no chunking — messages are stored as single rented buffers.

Validation:

- `BufferSize > 0` (already exists)
- `MaxQueuedSendBytes > 0` (already exists)
- `MaxQueuedSendBytes >= BufferSize` (ensures at least one slab per client in `HardCapped`)
- all size calculations must use `checked` arithmetic

## Memory Formulas

### `HardCapped`

```text
SendSlabCount = ceil(MaxQueuedSendBytes / BufferSize)
TotalPinnedBytes = MaxConnections * BufferSize * (1 + SendSlabCount)
```

With defaults (100 connections, 8192 buffer, 16 MiB max queue):

```text
SendSlabCount = ceil(16777216 / 8192) = 2048
TotalPinnedBytes = 100 * 8192 * 2049 = 1,678,540,800 bytes (~1.56 GiB)
```

### `Shared`

```text
TotalPinnedBytes = MaxConnections * BufferSize
```

With defaults:

```text
TotalPinnedBytes = 100 * 8192 = 819,200 bytes (~800 KiB)
```

## Detailed Changes

### `TcpServerSettings`

Add `SendStorageMode` with default `Shared`.

Remove `SendPoolChunkSize` and `PreallocatedSendPoolBytes` if they existed from prior iterations.

Add validation: `MaxQueuedSendBytes >= BufferSize`.

### `BufferSlab`

Mode-dependent:

- `HardCapped`: allocate one pinned array with per-client blocks containing receive buffer + send slabs. `CreateReceiveEventArgs` binds to the receive slice within each client block. `CreateSendEventArgs` creates the SAEA without binding a buffer.
- `Shared`: allocate one pinned array with receive buffers only (`MaxConnections * BufferSize`). `CreateReceiveEventArgs` binds to receive slices. `CreateSendEventArgs` creates the SAEA without binding a buffer.

In both modes, the old `× 2` layout (one receive + one send per client) is removed.

### `ArenaBackedSendQueue` (new, `HardCapped` only)

Fields:

```csharp
private readonly byte[] _slabBuffer;
private readonly int _slabBaseOffset;
private readonly int _slabSize;
private readonly int _slabCount;
private readonly Stack<int> _freeSlabs;
private readonly Queue<SendBlock> _pendingBlocks;
private SendBlock _currentBlock;
private int _currentSlabOffset;
private int _queuedBytes;
private bool _sendInProgress;
```

Methods: `Enqueue`, `TryGetNextChunk`, `CompleteSend`, `Reset`, `GetQueuedBytes`.

### `SharedSendQueue` (new, `Shared` only)

Fields:

```csharp
private readonly Queue<(byte[] Buffer, int Length)> _pendingMessages;
private readonly int _maxQueuedBytes;
private byte[]? _currentBuffer;
private int _currentLength;
private int _currentOffset;
private int _queuedBytes;
private bool _sendInProgress;
```

Methods: same contract as `ArenaBackedSendQueue`.

### `Client`

Constructor takes the appropriate send queue implementation based on `SendStorageMode`.

`TryPrepareSendChunk` removes the `maxChunkSize` parameter in `HardCapped` (slabs are always `BufferSize`). In `Shared`, `maxChunkSize` remains because rented buffers can be larger than the socket buffer.

Alternatively, keep `maxChunkSize` in both for uniformity — `ArenaBackedSendQueue.TryGetNextChunk` simply ignores it since slabs are already capped at `BufferSize`.

`TryPrepareSendChunk` calls the send queue to get buffer, offset, and size, then calls `SendEventArgs.SetBuffer(buffer, offset, size)`. No intermediate copy.

### `TcpServer`

Construct `BufferSlab` and send queues according to `SendStorageMode`.

`StartSend` and `ProcessSend` remain structurally unchanged. They already support partial sends and async/sync completion.

## Metrics

### `HardCapped`

No new metrics needed. Overflow is per-client and already tracked by `SendQueueOverflows`. The failure mode is identical to today: the client's own queue is full.

### `Shared`

Optional: `SharedSendRentals` counter, incremented on each `ArrayPool.Rent`. This would show the steady-state allocation rate. Low priority — `ArrayPool` is designed for high-frequency rent/return and does not normally require monitoring.

## Implementation Order

1. **`Shared` mode first.** It is simpler, lower risk, and delivers the core optimization (two copies to one). No new data structures, no arena layout changes, minimal diff from current code. The pinned slab shrinks from `× 2` to `× 1`. Can ship independently and be validated in production.

2. **`HardCapped` mode second.** Adds the per-client arena layout, `ArenaBackedSendQueue`, and the mode switch in `BufferSlab`. Layered on top of the `Shared` work since both modes share the same `Client`/`TcpServer` send pipeline changes.

This phasing reduces risk and gives a working improvement before the arena work lands.

## Testing

### Unit Tests

Add tests for:

1. `HardCapped` computes pinned memory as `MaxConnections * BufferSize * (1 + SendSlabCount)`
2. `Shared` computes pinned memory as `MaxConnections * BufferSize`
3. `HardCapped` enqueue pops slabs from the per-client free stack and release returns them
4. `Shared` enqueue rents from `ArrayPool` and complete/reset returns them
5. per-client `MaxQueuedSendBytes` overflow is exact in both modes
6. `HardCapped` slab exhaustion matches per-client overflow (no cross-client failure)
7. partial sends advance offsets correctly in both modes
8. `Reset` returns all owned storage in both modes
9. caller mutation after `Send` does not affect queued data in both modes
10. `Shared` mode uses stored `Length`, not `rented.Length`, when preparing send chunks

### Integration Tests

Re-run the existing send and overflow tests and add:

1. `HardCapped` sends correctly under normal load
2. `Shared` sends correctly with rented buffers
3. overflow disconnect still fires with `DisconnectReason.SendQueueOverflow` in both modes
4. recycled clients do not retain owned send storage across generations in both modes
