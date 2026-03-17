# Uptime as a First-Class Snapshot Field

## Summary

Add a `TimeSpan Uptime` property to `TcpServerMetricsSnapshot` so consumers do not need to compute `TimestampUtc - ServerStartedAtUtc` themselves.

## Current State

`ServerStartedAtUtc` and `TimestampUtc` are both present on the snapshot. Uptime is derivable but not surfaced directly. Every consumer that wants to display uptime must repeat the same subtraction.

## Design

### `TcpServerMetricsSnapshot`

#### New property

```csharp
public TimeSpan Uptime { get; }
```

#### Modified constructor

Compute uptime from the two timestamps already passed in:

```csharp
Uptime = timestampUtc - serverStartedAtUtc;
```

No new constructor parameter needed. The value is derived from existing parameters.

### No changes to `TcpServerMetricsState` or `TcpServerMetricsCollector`

This is purely a convenience property on the snapshot struct.

## Testing

### Unit Tests

1. Construct a snapshot with `ServerStartedAtUtc = T` and `TimestampUtc = T + 5 minutes`. Verify `Uptime == TimeSpan.FromMinutes(5)`.
2. Verify the initial snapshot (where `ServerStartedAtUtc == DateTime.MinValue`) produces a large `Uptime` value without throwing. This is the pre-start state and consumers should not rely on it, but it must not crash.
