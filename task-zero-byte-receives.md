# Zero-Byte Receives

## Summary

Count zero-byte receive completions separately from successful data receives. In TCP, a zero-byte `ReceiveAsync` completion with `SocketError.Success` signals that the remote side has initiated a graceful close (FIN). Currently this triggers a disconnect with `DisconnectReason.RemoteClosed` but is not tracked as a metric. A dedicated counter lets operators distinguish graceful remote closes from error-driven disconnects and correlate remote-close rates with application behavior.

## Current State

In `TcpServer.ProcessReceive` (line 681–691):

```csharp
int bytesTransferred = receiveEventArgs.BytesTransferred;
if (bytesTransferred <= 0)
{
    Log(
        LogLevel.Error,
        nameof(ProcessReceive),
        "No bytes transferred, remote most likely closed",
        client.Identity
    );
    disconnectReason = DisconnectReason.RemoteClosed;
    return false;
}
```

Also in `TcpServerMetricsState.RecordReceive`:

```csharp
if (bytesTransferred <= 0)
{
    return;
}
```

Zero-byte receives are explicitly skipped by `RecordReceive` and produce no metric signal at all.

## Design

### Counter

Add a single `long _zeroByteReceives` field to `TcpServerMetricsState`.

### Instrumentation

Add `IncrementZeroByteReceives()` to `TcpServerMetricsState`:

```csharp
internal void IncrementZeroByteReceives()
{
    if (!IsCaptureEnabled())
    {
        return;
    }

    Interlocked.Increment(ref _zeroByteReceives);
}
```

### Call Site

In `TcpServer.ProcessReceive`, inside the `bytesTransferred <= 0` branch, before setting `disconnectReason`:

```csharp
if (bytesTransferred <= 0)
{
    _metricsState.IncrementZeroByteReceives();
    // ... existing log and disconnect reason
}
```

### Snapshot

Add `long ZeroByteReceives` to `TcpServerMetricsSnapshot`.

## Detailed Changes

### `TcpServerMetricsState`

#### New field
```csharp
private long _zeroByteReceives;
```

#### New method
```csharp
internal void IncrementZeroByteReceives()
{
    if (!IsCaptureEnabled())
    {
        return;
    }

    Interlocked.Increment(ref _zeroByteReceives);
}
```

#### New getter
```csharp
internal long GetZeroByteReceives()
{
    return Interlocked.Read(ref _zeroByteReceives);
}
```

### `TcpServer.ProcessReceive`

Add `_metricsState.IncrementZeroByteReceives()` in the `bytesTransferred <= 0` branch.

### `TcpServerMetricsSnapshot`

Add constructor parameter `long zeroByteReceives` and property:
```csharp
/// <summary>
/// Gets the total number of zero-byte receive completions (remote graceful close signals).
/// </summary>
public long ZeroByteReceives { get; }
```

### `TcpServerMetricsCollector`

In `CreateSnapshot`, read and pass through:
```csharp
_metricsState.GetZeroByteReceives()
```

## Testing

### Unit Tests (`TcpServerMetricsStateTests`)

1. **Counter increments**: enable capture, call `IncrementZeroByteReceives` 3 times, verify `GetZeroByteReceives()` returns 3.
2. **Capture gating**: counter does not increment when capture is disabled.

### Integration Tests

In `TcpServerMetricsTests`, have a client connect and then close its socket (without sending). Verify `ZeroByteReceives >= 1` in the snapshot. This also verifies that `DisconnectsByReason[RemoteClosed]` increments, confirming the two metrics are correlated.
