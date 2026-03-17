# Send Queue Depth Gauge

## Summary

Track the aggregate number of bytes currently queued for sending across all active clients. This surfaces backpressure building before it reaches the `MaxQueuedSendBytes` overflow threshold. Exposed as a gauge in the server metrics snapshot.

## Current State

`SendQueueOverflows` counts how many times a client hit the queue limit and was disconnected. There is no metric showing the current total queued bytes across all clients. An operator cannot see backpressure forming — only the aftermath.

`SendQueue` tracks `_queuedBytes` internally but does not expose it. `Client` and `ClientSnapshot` do not surface it either.

## Design

### `SendQueue`

#### New method

```csharp
internal int GetQueuedBytes()
{
    lock (_sync)
    {
        return _queuedBytes;
    }
}
```

### `TcpServerMetricsState`

#### New field

```csharp
private long _totalSendQueuedBytes;
```

#### New methods

```csharp
internal void SetTotalSendQueuedBytes(long value)
{
    Interlocked.Exchange(ref _totalSendQueuedBytes, value);
}

internal long GetTotalSendQueuedBytes()
{
    return Interlocked.Read(ref _totalSendQueuedBytes);
}
```

#### Modified: `ResetCurrentGauges`

Add `Interlocked.Exchange(ref _totalSendQueuedBytes, 0)`.

This gauge is not updated on the hot path. It is sampled by the collector during snapshot creation by iterating active clients. This keeps the send path lock-free.

### `ClientRegistry`

#### New method

```csharp
internal long GetTotalSendQueuedBytes()
{
    long total = 0;
    for (int i = 0; i < _clients.Length; i++)
    {
        Client client = _clients[i];
        if (client.IsAlive)
        {
            total += client.GetSendQueuedBytes();
        }
    }
    return total;
}
```

### `Client`

#### New method

```csharp
internal int GetSendQueuedBytes()
{
    return _sendQueue.GetQueuedBytes();
}
```

### `TcpServerMetricsCollector`

#### Modified: `CreateSnapshot`

Before constructing the snapshot, sample the aggregate:

```csharp
long totalSendQueuedBytes = _clientRegistry.GetTotalSendQueuedBytes();
```

Pass `totalSendQueuedBytes` to the snapshot constructor.

### `TcpServerMetricsSnapshot`

#### New constructor parameter and property

```csharp
public long TotalSendQueuedBytes { get; }
```

Add alongside the existing resource pool gauges (`AcceptPoolAvailable`, `AvailableClientSlots`).

## Testing

### Unit Tests

1. Create a `SendQueue`, enqueue data, verify `GetQueuedBytes()` returns the correct total. Complete sends and verify it decreases. Reset and verify it returns to zero.

### Integration Tests

1. Connect a client, send data from server to client without the client reading. Capture a snapshot and verify `TotalSendQueuedBytes > 0`.
2. After the client reads all data and sends complete, verify `TotalSendQueuedBytes == 0`.
