# Uptime and Snapshot Sequence Number

## Summary

Add a server start timestamp and a monotonically increasing snapshot sequence number to each metrics snapshot. Together these provide:

- **Uptime**: `snapshot.TimestampUtc - snapshot.ServerStartedAtUtc` gives total server uptime at any snapshot.
- **Sequence number**: detects missed snapshots, measures collection health, and provides a monotonic ordering for snapshot consumers.

## Current State

`TcpServerMetricsSnapshot` already carries `TimestampUtc` (when the snapshot was taken). There is no record of when the server started or how many snapshots have been published.

## Design

### Server Start Timestamp

`TcpServerMetricsCollector` already knows when it starts (the `Start` method records `_previousTimestampUtc = now`). Store this as `_serverStartedAtUtc` and pass it through to every snapshot.

### Snapshot Sequence Number

A `long _snapshotSequenceNumber` in `TcpServerMetricsCollector`, incremented with `Interlocked.Increment` each time `CreateSnapshot` is called. Starts at 0; first published snapshot is sequence 1.

### No Hot-Path Impact

Both values are read/written only in the collector thread (1Hz), not on any I/O hot path.

## Detailed Changes

### `TcpServerMetricsCollector`

#### New fields
```csharp
private DateTime _serverStartedAtUtc;
private long _snapshotSequenceNumber;
```

#### Modified method: `Start`
After computing `now`:
```csharp
_serverStartedAtUtc = now;
_snapshotSequenceNumber = 0;
```

#### Modified method: `CreateSnapshot`
```csharp
long sequenceNumber = Interlocked.Increment(ref _snapshotSequenceNumber);
```

Pass `_serverStartedAtUtc` and `sequenceNumber` to the snapshot constructor.

### `TcpServerMetricsSnapshot`

#### New constructor parameters
```csharp
DateTime serverStartedAtUtc,
long snapshotSequenceNumber
```

#### New properties
```csharp
/// <summary>
/// Gets the UTC timestamp when the server started and metrics collection began.
/// </summary>
public DateTime ServerStartedAtUtc { get; }

/// <summary>
/// Gets the monotonically increasing sequence number of this snapshot.
/// </summary>
public long SnapshotSequenceNumber { get; }
```

### Convenience

Snapshot consumers can compute uptime:
```csharp
TimeSpan uptime = snapshot.TimestampUtc - snapshot.ServerStartedAtUtc;
```

No need to add an explicit `Uptime` property — keep the snapshot a plain data carrier.

## Testing

### Unit Tests

1. **Sequence increments**: call `CaptureSnapshot` three times, read snapshots, verify sequence numbers are 1, 2, 3 (or appropriate based on initial publish in Start).
2. **Server start timestamp stable**: all three snapshots have the same `ServerStartedAtUtc`.
3. **Uptime derivable**: `TimestampUtc >= ServerStartedAtUtc` for every snapshot.

### Integration Tests

In `TcpServerMetricsTests`, after server start and at least one snapshot, verify `SnapshotSequenceNumber > 0` and `ServerStartedAtUtc <= TimestampUtc`.
