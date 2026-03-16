# Peak Active Connections (High-Watermark)

## Summary

Track the maximum number of simultaneously active connections observed between consecutive metric snapshots. This gives operators a high-watermark gauge that reveals load spikes that a simple point-in-time `ActiveConnections` reading would miss.

## Current State

`TcpServerMetricsState` tracks `_activeConnections` as a live gauge (incremented on accept, decremented on finalize disconnect). The snapshot captures the gauge's instantaneous value at collection time. If a spike of 500 connections arrives and drains within the 1-second collection interval, the snapshot may only show the residual 50.

## Design

### Counter

Add a single `long _peakActiveConnections` field to `TcpServerMetricsState`.

### Hot-Path Instrumentation

In `IncrementAcceptedConnections`, after incrementing `_activeConnections`, update the peak via a CAS loop:

```csharp
long current = Interlocked.Increment(ref _activeConnections);
long peak;
do
{
    peak = Volatile.Read(ref _peakActiveConnections);
    if (current <= peak)
    {
        break;
    }
} while (Interlocked.CompareExchange(ref _peakActiveConnections, current, peak) != peak);
```

This is the only place the peak can increase — `_activeConnections` only grows in `IncrementAcceptedConnections`.

### Snapshot

Add `GetPeakActiveConnections()` to `TcpServerMetricsState`, same pattern as the other getters.

### Reset Between Snapshots

After the collector reads the peak, reset it to the current `_activeConnections` value so the next interval starts fresh:

```csharp
long currentActive = _metricsState.GetActiveConnections();
long peak = _metricsState.GetAndResetPeakActiveConnections(currentActive);
```

`GetAndResetPeakActiveConnections` atomically reads the peak and writes `currentActive` back using `Interlocked.Exchange`. This ensures the next interval's peak starts at the current level, not zero.

### Snapshot Struct

Add `long PeakActiveConnections` to `TcpServerMetricsSnapshot` constructor and a corresponding property.

### Collector

In `CreateSnapshot`, call `GetAndResetPeakActiveConnections` and pass the result to the snapshot constructor.

## Detailed Changes

### `TcpServerMetricsState`

#### New field
```csharp
private long _peakActiveConnections;
```

#### Modified method: `IncrementAcceptedConnections`
After `Interlocked.Increment(ref _activeConnections)`, add the CAS loop to update `_peakActiveConnections`.

#### New method: `GetAndResetPeakActiveConnections`
```csharp
internal long GetAndResetPeakActiveConnections(long resetValue)
{
    return Interlocked.Exchange(ref _peakActiveConnections, resetValue);
}
```

#### Modified method: `ResetCurrentGauges`
Add `Interlocked.Exchange(ref _peakActiveConnections, 0)`.

### `TcpServerMetricsSnapshot`

Add constructor parameter `long peakActiveConnections` and property:
```csharp
public long PeakActiveConnections { get; }
```

### `TcpServerMetricsCollector`

In `CreateSnapshot`, read the peak and pass it through:
```csharp
long peakActiveConnections = _metricsState.GetAndResetPeakActiveConnections(
    _metricsState.GetActiveConnections()
);
```

## Testing

### Unit Tests (`TcpServerMetricsStateTests`)

1. **Peak tracks maximum**: enable capture, call `IncrementAcceptedConnections` 5 times, call `FinalizeDisconnect` 3 times, call `IncrementAcceptedConnections` 1 more time. Peak should be 5 (the max), not 3 (the current).
2. **Reset returns previous peak**: after establishing a peak of 5, call `GetAndResetPeakActiveConnections(3)`. Returns 5. Next call returns 3 (the reset value, unchanged since no new accepts).
3. **Capture gating**: peak does not update when capture is disabled.

### Integration Tests

Extend existing `TcpServerMetricsTests` to assert `PeakActiveConnections >= ActiveConnections` in the snapshot.
