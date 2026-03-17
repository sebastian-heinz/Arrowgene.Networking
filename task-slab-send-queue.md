# Slab-Based Send Queue (Zero-Copy Hot Path)

## Summary

Replace the current `SendQueue` allocation-per-enqueue model with preallocated per-client slab pools carved from the main `BufferSlab`. Data is chunked and copied into slabs on enqueue. The hot path (pop → set buffer → send) becomes zero-copy with no allocations.

## Current State

`SendQueue` uses a `Queue<byte[]>` of heap-allocated copies. On every `Enqueue`, it calls `GC.AllocateUninitializedArray<byte>` + `Buffer.BlockCopy` to take ownership of the caller's data. On the hot path, `CopyNextChunk` does a second `Buffer.BlockCopy` from the queued message into the SAEA's pinned send buffer. Two copies per message, one allocation per enqueue.

### Current Send Flow

1. **Enqueue** (`SendQueue.Enqueue`): allocate `byte[]`, copy data in, push to `Queue<byte[]>`
2. **Prepare** (`Client.TryPrepareSendChunk`): call `CopyNextChunk` which copies from queued `byte[]` → SAEA's slab send buffer, then `SetBuffer(offset, chunkSize)`
3. **Send** (`TcpServer.StartSend`): call `socket.SendAsync(sendEventArgs)`
4. **Complete** (`SendQueue.CompleteSend`): advance offset, release message when fully sent

### Current Memory Layout (BufferSlab)

```
Total = maxConnections × bufferSize × 2
Per client: [recv buffer][send buffer]
```

## Design

### New Memory Layout (BufferSlab)

Each client gets 1 receive buffer + N send slabs, where `N = ⌈maxQueuedSendBytes / bufferSize⌉`.

```
Total = maxConnections × bufferSize × (1 + N)
Per client: [recv buffer][send slab 0][send slab 1]...[send slab N-1]
```

With defaults (bufferSize=8192, maxQueuedSendBytes=16MB): N=2048 slabs per client, ~16.8MB per client. This is the same total capacity currently allowed by `MaxQueuedSendBytes`, just preallocated instead of lazy.

### New Send Flow

1. **Enqueue**: chunk data into slab-sized pieces, copy each chunk into a free slab from the pool, push `(slabIndex, length)` to pending queue. One copy, zero allocations.
2. **Prepare** (`TryGetNextChunk`): dequeue next pending chunk, return buffer offset + size. **Zero copy** — just pointer math.
3. **Send**: `SetBuffer(slabBuffer, slabOffset, chunkSize)` on the SAEA, pointing it directly at the preallocated slab. Call `socket.SendAsync`.
4. **Complete**: return the slab to the free pool. Handle partial sends by tracking offset within current slab.

## Detailed Changes

### `BufferSlab`

#### New fields
```csharp
private readonly int _sendSlabCount;
private readonly int _clientBlockSize;
```

#### Modified constructor
Takes additional `int maxQueuedSendBytes` parameter. Computes:
```csharp
_sendSlabCount = (maxQueuedSendBytes + bufferSize - 1) / bufferSize;
_clientBlockSize = checked(bufferSize * (1 + _sendSlabCount));
_buffer = GC.AllocateArray<byte>(checked(maxConnections * _clientBlockSize), pinned: true);
```

#### New properties / methods
```csharp
internal byte[] Buffer { get; }
internal int SendSlabCount { get; }
internal int SendSlabSize { get; }  // == bufferSize
internal int GetSendSlabBaseOffset(ushort clientId);
```

#### Modified: `CreateReceiveEventArgs`
Offset changes from `clientId * 2 * bufferSize` → `clientId * _clientBlockSize`.

#### Modified: `CreateSendEventArgs`
Remove `ushort clientId` parameter. No longer calls `SetBuffer` — the SAEA starts without a buffer. The send queue sets it per-send.

### `SendQueue` (full rewrite)

#### New fields
```csharp
private readonly byte[] _slabBuffer;       // ref to main pinned slab
private readonly int _slabBaseOffset;       // start of this client's send slabs
private readonly int _slabSize;             // == bufferSize
private readonly int _slabCount;            // N slabs
private readonly Stack<int> _freeSlabs;     // available slab indices
private readonly Queue<(int SlabIndex, int Length)> _pendingChunks;
private int _currentSlabIndex;              // -1 when idle
private int _currentSlabOffset;             // for partial sends
private int _currentSlabLength;
private bool _sendInProgress;
```

#### Constructor
Takes `(byte[] slabBuffer, int slabBaseOffset, int slabSize, int slabCount)`. Initializes `_freeSlabs` with indices `0..slabCount-1`.

#### New property
```csharp
internal byte[] SlabBuffer { get; }  // so Client can pass it to SetBuffer
```

#### `Enqueue(byte[] data, out bool startSend, out bool queueOverflow)`
```
chunksNeeded = ⌈data.Length / slabSize⌉
if chunksNeeded > freeSlabs.Count → overflow

for each chunk:
    slabIndex = freeSlabs.Pop()
    slabOffset = slabBaseOffset + slabIndex × slabSize
    BlockCopy(data, offset, slabBuffer, slabOffset, chunkLen)
    pendingChunks.Enqueue((slabIndex, chunkLen))

if !sendInProgress: sendInProgress=true, startSend=true
```

#### `TryGetNextChunk(out int bufferOffset, out int chunkSize)` (replaces `CopyNextChunk`)
```
if currentSlabIndex < 0:
    if pendingChunks empty → sendInProgress=false, return false
    dequeue next chunk → set currentSlab*

bufferOffset = slabBaseOffset + currentSlabIndex × slabSize + currentSlabOffset
chunkSize = currentSlabLength - currentSlabOffset
return true
```
No copy. Just returns the offset into the pinned buffer.

#### `CompleteSend(int bytesTransferred)`
```
advance currentSlabOffset by bytesTransferred
if slab fully sent:
    freeSlabs.Push(currentSlabIndex)
    reset currentSlab* to idle
    return pendingChunks.Count > 0
else:
    return true  // partial send, more of this slab to go
```

#### `Reset()`
Return current slab (if any) and all pending chunk slabs back to `_freeSlabs`. Reset `_sendInProgress`.

### `Client`

#### Modified constructor
Replace `int maxQueuedSendBytes` with `(byte[] sendSlabBuffer, int sendSlabBaseOffset, int sendSlabSize, int sendSlabCount)`. Pass through to new `SendQueue` constructor.

#### Modified: `TryPrepareSendChunk`
Remove `int maxChunkSize` parameter. Replace body:
```csharp
bool send = _sendQueue.TryGetNextChunk(out int bufferOffset, out chunkSize);
if (send)
{
    SendEventArgs.SetBuffer(_sendQueue.SlabBuffer, bufferOffset, chunkSize);
}
```
No intermediate buffer, no copy. `SetBuffer` points the SAEA directly at the preallocated slab.

### `TcpServer`

#### Modified: `BufferSlab` construction
```csharp
_bufferSlab = new BufferSlab(_settings.MaxConnections, _bufferSize, _settings.MaxQueuedSendBytes);
```

#### Modified: `CreateClient`
```csharp
return new Client(
    this, clientId,
    _bufferSlab.CreateReceiveEventArgs(clientId, ReceiveCompleted),
    _bufferSlab.CreateSendEventArgs(SendCompleted),
    _bufferSlab.Buffer,
    _bufferSlab.GetSendSlabBaseOffset(clientId),
    _bufferSlab.SendSlabSize,
    _bufferSlab.SendSlabCount
);
```

#### Modified: `StartSend`
```csharp
// remove _bufferSize parameter
if (!client.TryPrepareSendChunk(clientHandle.Generation, out int chunkSize))
```

## Tradeoffs

| | Current | Slab-based |
|---|---|---|
| **Enqueue allocation** | 1 `byte[]` per call | 0 |
| **Enqueue copies** | 1 (data → heap array) | 1 (data → slab) |
| **Hot path copies** | 1 (heap array → SAEA buffer) | 0 |
| **Memory model** | Lazy, grows with queue depth | Preallocated upfront per client |
| **Memory footprint** | Low baseline, grows under load | `maxConnections × maxQueuedSendBytes` upfront |
| **GC pressure** | Proportional to send rate | Zero on hot path |

## Testing

Existing tests cover the full send pipeline (enqueue → send → complete → multi-chunk → overflow → reset). The public API shape (`Enqueue`/`CompleteSend`/`Reset` signatures, `TryPrepareSendChunk` behavior) stays the same. All 45 existing tests should pass without modification.

Additional test cases to consider:
1. **Partial send within slab**: enqueue one slab-sized message, complete with half the bytes, verify second complete finishes it
2. **Slab recycling**: fill all slabs, complete all sends, verify all slabs returned and re-enqueueable
3. **Multi-chunk enqueue**: enqueue a message larger than slab size, verify it splits into correct number of pending chunks
