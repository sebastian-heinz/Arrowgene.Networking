# SendQueue: Pre-chunk at Enqueue Time

## Summary

Restructure `SendQueue` so that incoming payloads are split into buffer-sized chunks during `Enqueue` instead of during `CopyNextChunk`. This removes the chunking work and offset tracking from the send completion hot path, where it currently runs on every async callback.

## Current Flow

```
Send()
  -> Client.QueueSend()
    -> SendQueue.Enqueue()
       1. Allocate a full-size copy of the caller's byte[]
       2. BlockCopy the entire payload into the copy
       3. Enqueue the single copy

StartSend() / SendCompleted()  [HOT PATH]
  -> Client.TryPrepareSendChunk()
    -> SendQueue.CopyNextChunk()
       1. If no current message, dequeue the next full message
       2. Compute remaining = message.Length - offset
       3. Compute chunkSize = min(remaining, maxChunkSize)
       4. BlockCopy from message at offset into the SAEA buffer
    -> SetBuffer(offset, chunkSize)

ProcessSend()  [HOT PATH]
  -> Client.CompleteSend()
    -> SendQueue.CompleteSend()
       1. Advance offset by bytesTransferred
       2. Subtract bytesTransferred from queuedBytes
       3. If offset < message.Length, return true (more chunks from this message)
       4. Else null out current message, check for more queued messages
```

**Two copies per byte:** caller -> full copy (Enqueue), full copy -> SAEA buffer (CopyNextChunk).

**Hot path work:** offset tracking, remaining calculation, min calculation, conditional message advancement, all under lock.

## Proposed Flow

```
Send()
  -> Client.QueueSend()
    -> SendQueue.Enqueue(data, maxChunkSize)
       1. Check overflow against total data.Length
       2. Loop over the caller's byte[] in maxChunkSize-sized segments:
          a. Compute segmentSize = min(remaining, maxChunkSize)
          b. Allocate a byte[] of exactly segmentSize
          c. BlockCopy from caller's data into the segment
          d. Enqueue the segment
       3. Update queuedBytes by data.Length total

StartSend() / SendCompleted()  [HOT PATH]
  -> Client.TryPrepareSendChunk()
    -> SendQueue.CopyNextChunk()
       1. Dequeue the next chunk (already correctly sized)
       2. BlockCopy chunk into the SAEA buffer
       3. chunkSize = chunk.Length
    -> SetBuffer(offset, chunkSize)

ProcessSend()  [HOT PATH]
  -> Client.CompleteSend()
    -> SendQueue.CompleteSend()
       1. Handle partial OS send (offset within current chunk)
       2. Subtract bytesTransferred from queuedBytes
       3. If chunk fully sent, check for more queued chunks
```

**One copy per byte:** caller -> chunk (Enqueue). The chunk is then copied into the SAEA buffer, but this copy is unavoidable since the SAEA buffer is a shared slab region.

**Hot path work:** dequeue, one BlockCopy, simple completion bookkeeping. No offset-into-message tracking across chunks. No remaining calculation. No conditional message advancement.

## Why This Matters

- `CopyNextChunk` and `CompleteSend` run inside `StartSend` which is called from `SendCompleted` async callbacks. These fire in a tight loop draining the queue. Every reduction in per-iteration work improves throughput under load.
- `Enqueue` runs on the caller's `Send()` call, which is the producer side. Moving the chunking work there is acceptable because the caller is already paying for the copy and the lock acquisition.
- The current `_currentMessage` / `_currentOffset` state machine spans multiple send completions for a single large message. Eliminating this simplifies the queue logic and reduces the state that lives across async boundaries.

## Detailed Changes

### `SendQueue`

#### Constructor

Add a `maxChunkSize` parameter. Store it as `_maxChunkSize`. This is the SAEA buffer size and determines how incoming data is segmented.

```csharp
internal SendQueue(int maxQueuedBytes, int maxChunkSize)
```

#### `Enqueue`

Change signature to remove the need for a separate `maxChunkSize` parameter later (it is now stored).

```csharp
internal bool Enqueue(byte[] data, out bool startSend, out bool queueOverflow)
```

Inside the lock, after the overflow check:

1. Loop over `data` in `_maxChunkSize` steps.
2. For each segment, compute `segmentSize = Math.Min(data.Length - sourceOffset, _maxChunkSize)`.
3. Allocate `GC.AllocateUninitializedArray<byte>(segmentSize)`.
4. `Buffer.BlockCopy(data, sourceOffset, chunk, 0, segmentSize)`.
5. `_pendingMessages.Enqueue(chunk)`.
6. Advance `sourceOffset += segmentSize`.
7. After the loop, `_queuedBytes += data.Length`.

The overflow check remains against the total `data.Length`, not per-chunk. This preserves the current backpressure semantics exactly.

#### `CopyNextChunk`

Simplify. Remove `_currentMessage` and `_currentOffset` fields entirely.

```csharp
internal bool CopyNextChunk(byte[] targetBuffer, int targetOffset, out int chunkSize)
```

- Remove the `maxChunkSize` parameter since all chunks are already correctly sized.
- Inside the lock: if `_pendingChunk is not null` (partial send in progress), use it. Otherwise dequeue. If queue is empty, set `_sendInProgress = false`, return false.
- `chunkSize = chunk.Length` (or remaining bytes if resuming a partial send).
- `Buffer.BlockCopy(chunk, chunkOffset, targetBuffer, targetOffset, chunkSize)`.
- Store the chunk as `_pendingChunk` with its offset for partial send handling.

#### `CompleteSend`

Simplify to work with the current pending chunk.

```csharp
internal bool CompleteSend(int bytesTransferred)
```

- Advance the offset within `_pendingChunk`.
- Subtract `bytesTransferred` from `_queuedBytes`.
- If the chunk is fully sent, clear `_pendingChunk`.
- If more chunks in queue, return true.
- If queue empty, set `_sendInProgress = false`, return false.

Note: partial OS sends (where `bytesTransferred < chunkSize`) must still be handled. The OS can send fewer bytes than requested. Keep a `_pendingChunk` and `_pendingChunkOffset` for this case. This replaces the old `_currentMessage` / `_currentOffset` but is simpler because the chunk is already buffer-sized, so partial sends within a chunk are less common and span at most one chunk.

#### `Reset`

Update to clear `_pendingChunk` and `_pendingChunkOffset` instead of `_currentMessage` and `_currentOffset`.

#### Fields

Remove:
- `_currentMessage`
- `_currentOffset`

Add:
- `_maxChunkSize` (readonly, set in constructor)
- `_pendingChunk` (the chunk currently being sent, if partially sent)
- `_pendingChunkOffset` (offset into `_pendingChunk` for partial sends)

### `Client`

#### `TryPrepareSendChunk`

Update the call to `CopyNextChunk` to drop the `maxChunkSize` parameter.

```csharp
// Before:
bool send = _sendQueue.CopyNextChunk(sendBuffer, SendEventArgs.Offset, maxChunkSize, out chunkSize);

// After:
bool send = _sendQueue.CopyNextChunk(sendBuffer, SendEventArgs.Offset, out chunkSize);
```

The `maxChunkSize` parameter on `TryPrepareSendChunk` itself can be removed or kept for the `SetBuffer` call. It is no longer forwarded to the queue.

#### Constructor / `CreateClient`

Pass `bufferSize` (or equivalent max chunk size) to the `SendQueue` constructor.

### `TcpServer`

#### `CreateClient`

Pass `_bufferSize` through to the `SendQueue` constructor via `Client`.

No other changes. `StartSend`, `ProcessSend`, `Send`, and `SendCompleted` are unchanged in structure. The `Client.TryPrepareSendChunk` signature change is the only call-site update.

## Partial Send Handling

The OS can return `bytesTransferred < chunkSize` from `SendAsync`. This is uncommon but must be handled correctly.

Current behavior: `CompleteSend` advances `_currentOffset` within the full message. The next `CopyNextChunk` call re-copies from the new offset.

New behavior: `CompleteSend` advances `_pendingChunkOffset` within the current chunk. The next `CopyNextChunk` call copies the remaining portion of `_pendingChunk` starting from `_pendingChunkOffset`. This is the same pattern, just scoped to a single chunk instead of a full message.

## Overflow Semantics

The overflow check must remain atomic with respect to the total `data.Length`:

```csharp
if (data.Length > _maxQueuedBytes - _queuedBytes)
{
    queueOverflow = true;
    return false;
}
```

Do not check per-chunk. The caller's message is either fully accepted or fully rejected. This matches the current behavior exactly.

## Allocation Trade-off

For a message of size `N` with buffer size `B`:

| | Current | Proposed |
|---|---|---|
| Allocations in Enqueue | 1 array of size N | ceil(N/B) arrays of size <= B |
| Copies in Enqueue | 1 full BlockCopy of N bytes | ceil(N/B) BlockCopys totaling N bytes |
| Copies in CopyNextChunk | ceil(N/B) BlockCopys of <= B bytes each | ceil(N/B) BlockCopys of <= B bytes each |
| Total bytes copied | 2N | 2N |

Total bytes copied is the same. The difference is when and where the work happens:

- Current: N bytes copied at enqueue, N bytes copied across multiple hot-path calls.
- Proposed: N bytes copied (as chunks) at enqueue, N bytes copied across multiple hot-path calls.

The hot-path copy into the SAEA buffer is unavoidable either way. The win is that the hot path no longer does offset arithmetic, remaining calculation, or message advancement logic. It just dequeues and copies a ready-to-go chunk.

For messages smaller than or equal to `bufferSize` (the common case for many protocols), the allocation count is identical: one array, one copy at enqueue, one copy at send.

## Testing

### Unit Tests for `SendQueue`

Create `SendQueueTests.cs` covering:

1. **Single chunk message**: enqueue a message smaller than `maxChunkSize`, verify single dequeue produces exact data.
2. **Multi-chunk message**: enqueue a message larger than `maxChunkSize`, verify chunks are dequeued in order and reassembled data matches original.
3. **Exact boundary**: enqueue a message exactly equal to `maxChunkSize`, verify one chunk.
4. **Multiple messages**: enqueue several messages, verify chunks come out in FIFO order with correct boundaries.
5. **Partial send within chunk**: call `CompleteSend` with fewer bytes than chunk size, verify next `CopyNextChunk` copies the remainder.
6. **Overflow rejection**: verify a message that exceeds remaining capacity is fully rejected (no partial enqueue).
7. **startSend flag**: verify `startSend` is true on first enqueue, false on subsequent enqueues while send is in progress.
8. **Reset**: verify reset clears all state including pending chunks.

### Integration Tests

The existing `TcpServerMetricsTests` echo test (`MetricsSnapshot_TracksConnectionLifecycleAndTraffic`) already exercises the send path end-to-end. Verify it still passes. Consider adding a test that sends a payload larger than `bufferSize` to exercise multi-chunk sends through the full stack.
