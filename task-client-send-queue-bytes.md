# Per-Client Send Queue Bytes in ClientSnapshot

## Summary

Add the current queued send bytes to `ClientSnapshot` so operators can identify which specific clients are backed up, not just that backpressure exists somewhere in aggregate.

## Current State

`ClientSnapshot` captures `BytesReceived`, `BytesSent`, `PendingOperations`, and other per-client state. It does not include how many bytes are currently waiting in the client's send queue.

`SendQueue` tracks `_queuedBytes` internally. `Client.Snapshot()` does not read it.

## Design

### `SendQueue`

#### New method (if not already added by send-queue-depth-gauge task)

```csharp
internal int GetQueuedBytes()
{
    lock (_sync)
    {
        return _queuedBytes;
    }
}
```

### `Client`

#### Modified: `Snapshot()`

Inside the existing `lock (_sync)` block, read the queued bytes and pass them to the `ClientSnapshot` constructor:

```csharp
int sendQueuedBytes = _sendQueue.GetQueuedBytes();
```

### `ClientSnapshot`

#### New constructor parameter and property

```csharp
public int SendQueuedBytes { get; }
```

Add after the existing `PendingOperations` parameter.

### No changes to `TcpServerMetricsState` or the server snapshot

This is a per-client inspection field, not a server-level metric.

## Testing

### Unit Tests

1. Verify `ClientSnapshot.SendQueuedBytes` reflects the current `SendQueue` state at snapshot time.
2. After enqueuing data, snapshot should show the queued bytes. After completing all sends, snapshot should show zero.

### Integration Tests

1. Connect a client, send data without the client reading. Take a `ClientSnapshot` and verify `SendQueuedBytes > 0`.
2. Verify that after disconnect and pool return, a fresh activation starts with `SendQueuedBytes == 0`.
