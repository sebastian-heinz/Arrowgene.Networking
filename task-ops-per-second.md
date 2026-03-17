# Receive / Send / Accept Operations Per Second

## Summary

Derive per-second operation rates for receive, send, and accept in the metrics collector, using the same delta/elapsed pattern already used for `ReceiveBytesPerSecond` and `SendBytesPerSecond`. This lets operators distinguish "few large transfers" from "many small transfers" and spot connection churn via accept rate.

## Current State

The collector already tracks byte rates by storing the previous snapshot's `BytesReceived` and `BytesSent`, computing deltas, and dividing by elapsed seconds. The underlying operation counters (`ReceiveOperations`, `SendOperations`, `AcceptedConnections`) are already in `TcpServerMetricsState` and already read during `CreateSnapshot`. They just are not turned into rates.

## Design

### `TcpServerMetricsCollector`

#### New fields

```csharp
private long _previousReceiveOperations;
private long _previousSendOperations;
private long _previousAcceptedConnections;
```

#### Modified: `Start`

Initialize the three new previous-values alongside the existing byte counters:

```csharp
_previousReceiveOperations = _metricsState.GetReceiveOperations();
_previousSendOperations = _metricsState.GetSendOperations();
_previousAcceptedConnections = _metricsState.GetAcceptedConnections();
```

#### Modified: `CaptureSnapshot`

Compute the three new rates using the same elapsed-seconds denominator already calculated for byte rates:

```csharp
long receiveOperations = _metricsState.GetReceiveOperations();
long sendOperations = _metricsState.GetSendOperations();
long acceptedConnections = _metricsState.GetAcceptedConnections();

double receiveOpsPerSecond = 0.0d;
double sendOpsPerSecond = 0.0d;
double acceptsPerSecond = 0.0d;

if (elapsedSeconds > 0.0d)
{
    receiveOpsPerSecond = (receiveOperations - _previousReceiveOperations) / elapsedSeconds;
    sendOpsPerSecond = (sendOperations - _previousSendOperations) / elapsedSeconds;
    acceptsPerSecond = (acceptedConnections - _previousAcceptedConnections) / elapsedSeconds;
}

_previousReceiveOperations = receiveOperations;
_previousSendOperations = sendOperations;
_previousAcceptedConnections = acceptedConnections;
```

Pass the three rates through to `CreateSnapshot` and then to the snapshot constructor.

### `TcpServerMetricsSnapshot`

#### New constructor parameters and properties

```csharp
public double ReceiveOpsPerSecond { get; }
public double SendOpsPerSecond { get; }
public double AcceptsPerSecond { get; }
```

Add after the existing `SendBytesPerSecond` parameter in the constructor.

### No changes to `TcpServerMetricsState`

The underlying counters already exist. No new fields or recording methods needed.

## Testing

### Unit Tests (`TcpServerMetricsCollectorTests`)

1. Capture two snapshots with a known number of receive/send/accept increments between them. Verify the three rates are within tolerance of the expected values.
2. Verify rates are `0.0` when no operations occur between snapshots.
3. Verify the initial snapshot (before any interval elapses) has `0.0` for all three rates.

### Integration Tests (`TcpServerMetricsTests`)

Extend the existing `MetricsSnapshot_TracksConnectionLifecycleAndTraffic` test to assert that `ReceiveOpsPerSecond > 0` and `SendOpsPerSecond > 0` after traffic has been exchanged.
