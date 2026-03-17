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
[recv0][sendRing0][recv1][sendRing1]...
```

Each client block contains:

```text
[receive buffer (BufferSize bytes)][send circular buffer (MaxQueuedSendBytes bytes)]
```

Where:

```text
ClientBlockSize = BufferSize + MaxQueuedSendBytes
TotalPinnedBytes = MaxConnections * (BufferSize + MaxQueuedSendBytes)
```

The per-client send region is a circular buffer of exactly `MaxQueuedSendBytes` bytes. Enqueue writes data at the write cursor and advances it; send reads from the read cursor and advances it. Both cursors wrap at `MaxQueuedSendBytes`. Occupied space is `_queuedBytes`; free space is `MaxQueuedSendBytes - _queuedBytes`. These two regions are always disjoint.

### Behavior

- all receive buffers are pinned and preallocated
- all queued send storage is pinned and preallocated per client
- no shared rented send fallback exists
- no global send allocator exists
- enqueue can only fail because the client's own circular buffer is full
- overflow is always per-client, never cross-client
- the byte check (`_queuedBytes + length <= capacity`) is the **only** capacity check — there is no secondary slab or block count limit that can diverge from it

### Why Per-Client Regions

The previous shared-arena `HardCapped` design required a global chunk allocator accessed by all clients concurrently. That introduced:

- a new lock or lock-free structure shared across all IOCP callbacks and user send calls
- lock ordering concerns (`Client._sync` → `SendQueue._sync` → `SendBufferPool`)
- a new failure mode where client A fails because clients B through Z consumed the global pool
- all-or-nothing acquisition logic with rollback considerations

Per-client regions eliminate all of these. Each client's send ring is managed by that client's existing `SendQueue` lock. No cross-client synchronization is needed. Overflow semantics match today exactly: a client fails only when its own queue is full.

The cost is memory. With defaults (100 connections, 8 KiB buffer, 16 MiB max queue), each client block is ~16.008 MiB, total ~1.56 GiB. This is the price of a deterministic per-client worst-case guarantee. It is explicit and expected.

### Synchronization

No new synchronization. The existing lock hierarchy is unchanged:

```text
Client._sync -> SendQueue._sync
```

Each client's circular buffer state lives inside `SendQueue` and is protected by `SendQueue._sync`. No global lock, no cross-client contention.

### Why a Circular Buffer Instead of Fixed-Size Slabs

The earlier slab-based design divided the per-client send region into `ceil(MaxQueuedSendBytes / BufferSize)` fixed-size slabs. This introduced a secondary capacity dimension — slab count — that could diverge from the byte limit:

- When `_count == 1 && _headInFlight`, coalescing into the tail was unsafe (the kernel is DMA-reading it), so Enqueue had to pop a fresh slab even for a 1-byte write. That consumed an entire `BufferSize`-worth of slab capacity for 1 byte of data.
- This meant slab exhaustion could reject a send while `_queuedBytes` was well below `MaxQueuedSendBytes`. The "exact per-client byte limit" claim was not actually preserved.
- Fixing this required coalescing logic, a `_headInFlight` flag, a free-slab stack, a ring of `SendBlock` structs, and careful case analysis of when coalescing was safe.

A circular buffer eliminates all of this. There is no secondary capacity dimension. The buffer is exactly `MaxQueuedSendBytes` bytes, and the occupied region (`_readPos` to `_writePos`, wrapping) is always exactly `_queuedBytes` bytes. The free region is always exactly `MaxQueuedSendBytes - _queuedBytes` bytes. The byte check is the only check.

The in-flight safety concern also disappears structurally. When `TryGetNextChunk` hands a region to the kernel, that region is part of the occupied space. Enqueue writes into the free space. Occupied and free space are disjoint by construction — no flag or case analysis needed.

### Circular Buffer Fields

```csharp
private readonly byte[] _buffer;     // the backing pinned array
private readonly int _baseOffset;    // start of this client's send region within the array
private readonly int _capacity;      // = MaxQueuedSendBytes
private int _readPos;                // next byte to send, [0, _capacity)
private int _writePos;               // next byte to write, [0, _capacity)
private int _queuedBytes;            // occupied bytes in the ring
private bool _sendInProgress;
```

Empty: `_queuedBytes == 0`, `_readPos == _writePos`.
Full: `_queuedBytes == _capacity`, `_readPos == _writePos`.

### Send Flow

Enqueue:

```csharp
if (_queuedBytes + data.Length > _capacity)
{
    // per-client overflow — exact byte semantics
    return false;
}

// write into the circular buffer, wrapping at the boundary
int firstPart = Math.Min(data.Length, _capacity - _writePos);
Buffer.BlockCopy(data, 0, _buffer, _baseOffset + _writePos, firstPart);
if (firstPart < data.Length)
{
    Buffer.BlockCopy(data, firstPart, _buffer, _baseOffset, data.Length - firstPart);
}
_writePos = (_writePos + data.Length) % _capacity;
_queuedBytes += data.Length;
```

No coalescing logic, no slab pops, no block ring mutation. Data goes into the buffer at the write cursor.

Prepare (zero copy):

```csharp
if (_queuedBytes == 0)
{
    _sendInProgress = false;
    chunkSize = 0;
    return false;
}

// send the contiguous region from _readPos — up to the buffer boundary or _queuedBytes
chunkSize = Math.Min(_queuedBytes, _capacity - _readPos);
sendEventArgs.SetBuffer(_buffer, _baseOffset + _readPos, chunkSize);
return true;
```

The chunk is the longest contiguous run from the read cursor. If data wraps, this send covers up to the end of the buffer; the next send picks up from offset 0.

Complete:

```csharp
_readPos = (_readPos + bytesTransferred) % _capacity;
_queuedBytes -= bytesTransferred;

if (_queuedBytes > 0)
{
    return true;
}

_sendInProgress = false;
return false;
```

No slab returns, no block ring advancement. The read cursor moves forward, freeing space for future enqueues.

### In-Flight Safety — Why No Flag Is Needed

In the slab design, `_headInFlight` existed to prevent Enqueue from coalescing into a slab the kernel was reading. The circular buffer eliminates this concern structurally:

- The kernel reads from the **occupied** region (starting at `_readPos`, length up to `_queuedBytes`).
- Enqueue writes into the **free** region (starting at `_writePos`, length up to `_capacity - _queuedBytes`).
- These two regions are disjoint as long as `_queuedBytes <= _capacity`, which is enforced by the overflow check at the top of Enqueue.
- No writes can ever land in the in-flight region, regardless of timing.

The `_headInFlight` flag, the coalescing case analysis (`_count == 1 && _headInFlight`), and the `SendBlock` ring are all eliminated.

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
if (_currentBuffer is null)
{
    if (_pendingMessages.Count == 0)
    {
        _sendInProgress = false;
        chunkSize = 0;
        return false;
    }

    var (buffer, length) = _pendingMessages.Dequeue();
    _currentBuffer = buffer;
    _currentLength = length;
    _currentOffset = 0;
}

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

`SendPoolChunkSize` is not needed as a separate setting. In `HardCapped`, the send region is a single circular buffer of `MaxQueuedSendBytes` bytes — there are no discrete slabs. In `Shared`, there is no chunking — messages are stored as single rented buffers.

Validation:

- `BufferSize > 0` (already exists)
- `MaxQueuedSendBytes > 0` (already exists)
- `MaxQueuedSendBytes > 0` when `SendStorageMode == HardCapped` (the circular buffer must have nonzero capacity)
- all size calculations must use `checked` arithmetic

## Memory Formulas

### `HardCapped`

```text
TotalPinnedBytes = MaxConnections * (BufferSize + MaxQueuedSendBytes)
```

With defaults (100 connections, 8192 buffer, 16 MiB max queue):

```text
TotalPinnedBytes = 100 * (8192 + 16777216) = 1,678,540,800 bytes (~1.56 GiB)
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

- `HardCapped`: allocate one pinned array with per-client blocks containing receive buffer + send circular buffer. `CreateReceiveEventArgs` binds to the receive slice within each client block. `CreateSendEventArgs` creates the SAEA without binding a buffer.
- `Shared`: allocate one pinned array with receive buffers only (`MaxConnections * BufferSize`). `CreateReceiveEventArgs` binds to receive slices. `CreateSendEventArgs` creates the SAEA without binding a buffer.

In both modes, the old `× 2` layout (one receive + one send per client) is removed.

### `ArenaBackedSendQueue` (new, `HardCapped` only)

Fields:

```csharp
private readonly byte[] _buffer;     // the backing pinned array (shared with receive buffers)
private readonly int _baseOffset;    // start of this client's send region
private readonly int _capacity;      // = MaxQueuedSendBytes
private int _readPos;                // next byte to send, [0, _capacity)
private int _writePos;               // next byte to write, [0, _capacity)
private int _queuedBytes;            // occupied bytes in the ring
private bool _sendInProgress;
```

Methods: `Enqueue`, `TryGetNextChunk`, `CompleteSend`, `Reset`, `GetQueuedBytes`.

`Reset` must reset `_readPos`, `_writePos`, `_queuedBytes`, and `_sendInProgress`. No slab returns needed — the circular buffer region is fixed and reusable.

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

`TryPrepareSendChunk` in `HardCapped` does not need a `maxChunkSize` parameter — the chunk is naturally bounded by the contiguous region from `_readPos` to the buffer boundary. In `Shared`, `maxChunkSize` remains because rented buffers can be larger than the socket buffer.

Alternatively, keep `maxChunkSize` in both for uniformity — `ArenaBackedSendQueue.TryGetNextChunk` can cap the chunk at `maxChunkSize` to limit individual send sizes if desired.

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

1. `HardCapped` computes pinned memory as `MaxConnections * (BufferSize + MaxQueuedSendBytes)`
2. `Shared` computes pinned memory as `MaxConnections * BufferSize`
3. `HardCapped` enqueue writes into circular buffer and advances write cursor
4. `Shared` enqueue rents from `ArrayPool` and complete/reset returns them
5. per-client `MaxQueuedSendBytes` overflow is exact in both modes — byte check is the only rejection path
6. `HardCapped` overflow is per-client only (no cross-client failure)
7. partial sends advance read cursor correctly in both modes
8. `Reset` resets cursors and `_queuedBytes` in `HardCapped`, returns rented buffers in `Shared`
9. caller mutation after `Send` does not affect queued data in both modes
10. `Shared` mode uses stored `Length`, not `rented.Length`, when preparing send chunks
11. `HardCapped` many 1-byte sends up to `MaxQueuedSendBytes` all succeed (no secondary slab limit to exhaust)
12. `HardCapped` enqueue wraps correctly when the write cursor crosses the buffer boundary (data split across end and start)
13. `HardCapped` `TryGetNextChunk` returns contiguous region up to buffer boundary; next call picks up from offset 0
14. `HardCapped` a send that exactly fills the buffer leaves `_queuedBytes == _capacity` and rejects the next enqueue
15. `HardCapped` after CompleteSend frees space, enqueue into the freed region succeeds
16. `HardCapped` enqueue while a send is in flight writes into the free region without corrupting the in-flight region
17. `HardCapped` interleaved enqueue and send cycles maintain correct `_readPos` / `_writePos` / `_queuedBytes` invariants

### Integration Tests

Re-run the existing send and overflow tests and add:

1. `HardCapped` sends correctly under normal load
2. `Shared` sends correctly with rented buffers
3. overflow disconnect still fires with `DisconnectReason.SendQueueOverflow` in both modes
4. recycled clients do not retain owned send storage across generations in both modes
