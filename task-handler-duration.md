# Consumer Handler Duration

## Summary

Track how long consumer handler functions (`HandleReceived`, `HandleConnected`, `HandleDisconnected`, `HandleError`) take to execute, using a fixed-bucket histogram in the consumer metrics. This helps operators identify slow handlers that back up the ordering lane queues and cause backpressure.

## Design Principles

- Duration tracking belongs to the **consumer**, not the server — it measures consumer-side work.
- Uses the existing `ConsumerMetricsState` and `ConsumerMetricsSnapshot` pattern.
- Fixed-bucket histogram, same approach as transfer-size and connection-duration buckets.
- Measurement uses `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime()` for high-resolution, allocation-free timing.

## Bucket Layout

7 fixed buckets covering sub-microsecond handlers through pathologically slow ones:

| Index | Range |
|-------|-------|
| 0 | 0–100 μs |
| 1 | 100 μs – 1 ms |
| 2 | 1–10 ms |
| 3 | 10–50 ms |
| 4 | 50–250 ms |
| 5 | 250 ms – 1 s |
| 6 | 1 s+ |

Most well-behaved handlers should land in buckets 0–2. Entries in buckets 4+ indicate handlers doing blocking I/O or heavy computation on the consumer thread.

## Current State

In `ThreadedBlockingQueueConsumer.Consume` (lines 147–178), each event is dispatched to the appropriate `Handle*` method, then `RecordProcessedEvent` is called. Duration is not measured.

`ConsumerMetricsSnapshot` currently carries: `HandlerErrors`, `QueueDepthByLane`, `EventsProcessed`.

`ConsumerMetricsState` holds the mutable atomic counters.

## Detailed Changes

### `ConsumerMetricsState`

#### New constant
```csharp
private const int HandlerDurationBucketCount = 7;
```

#### New field
```csharp
private readonly long[] _handlerDurationBuckets;
```

Initialize in constructor: `_handlerDurationBuckets = new long[HandlerDurationBucketCount];`

#### New property
```csharp
internal int HandlerDurationBucketCount_ => _handlerDurationBuckets.Length;
```

#### New method
```csharp
internal void RecordHandlerDuration(long elapsedTicks)
{
    if (!IsCaptureEnabled())
    {
        return;
    }

    Interlocked.Increment(ref _handlerDurationBuckets[GetHandlerDurationBucketIndex(elapsedTicks)]);
}
```

#### New method
```csharp
internal void CopyHandlerDurationBuckets(long[] destination)
{
    if (destination is null)
    {
        throw new ArgumentNullException(nameof(destination));
    }

    if (destination.Length < _handlerDurationBuckets.Length)
    {
        throw new ArgumentOutOfRangeException(
            nameof(destination),
            "Destination must be at least as large as the handler-duration counter array."
        );
    }

    for (int index = 0; index < _handlerDurationBuckets.Length; index++)
    {
        destination[index] = Volatile.Read(ref _handlerDurationBuckets[index]);
    }
}
```

#### New static method
```csharp
private static int GetHandlerDurationBucketIndex(long elapsedTicks)
{
    // Convert ticks to microseconds using Stopwatch.Frequency
    double microseconds = (double)elapsedTicks / Stopwatch.Frequency * 1_000_000.0;

    if (microseconds <= 100.0)
        return 0;
    if (microseconds <= 1_000.0)
        return 1;
    if (microseconds <= 10_000.0)
        return 2;
    if (microseconds <= 50_000.0)
        return 3;
    if (microseconds <= 250_000.0)
        return 4;
    if (microseconds <= 1_000_000.0)
        return 5;
    return 6;
}
```

Alternative: precompute the tick thresholds in the constructor from `Stopwatch.Frequency` to avoid the division on each call. This is a minor optimization since handler duration recording runs at event-processing frequency, not I/O completion frequency.

### `ThreadedBlockingQueueConsumer.Consume`

Wrap the handler dispatch in `Stopwatch.GetTimestamp()` calls:

```csharp
try
{
    long startTimestamp = Stopwatch.GetTimestamp();

    switch (clientEvent)
    {
        case ClientDataEvent dataEvent:
            HandleReceived(dataEvent.ClientHandle, dataEvent.Data);
            break;
        case ClientConnectedEvent connectedEvent:
            HandleConnected(connectedEvent.ClientHandle);
            break;
        case ClientDisconnectedEvent disconnectedEvent:
            HandleDisconnected(disconnectedEvent.ClientSnapshot);
            break;
        case ClientErrorEvent errorEvent:
            HandleError(errorEvent.ClientSnapshot, errorEvent.Exception, errorEvent.Message);
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported client event type: {clientEvent.GetType().FullName}");
    }

    long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
    _consumerMetricsState.RecordHandlerDuration(elapsedTicks);
    _consumerMetricsState.RecordProcessedEvent(clientEvent.ClientEventType);
}
```

Note: `Stopwatch.GetTimestamp()` is a single P/Invoke to `QueryPerformanceCounter` on Windows or `clock_gettime` on Linux — no allocation, sub-microsecond overhead.

Add `using System.Diagnostics;` to the file.

### `ConsumerMetricsSnapshot`

#### New constructor parameter
```csharp
long[] handlerDurationBuckets
```

#### New property
```csharp
/// <summary>
/// Gets handler duration histogram buckets using the ranges 0–100μs, 100μs–1ms, 1–10ms, 10–50ms, 50–250ms, 250ms–1s, and 1s+.
/// </summary>
public ReadOnlyMemory<long> HandlerDurationBuckets { get; }
```

#### Constructor body
```csharp
HandlerDurationBuckets = handlerDurationBuckets ?? throw new ArgumentNullException(nameof(handlerDurationBuckets));
```

### `ThreadedBlockingQueueConsumer` (`IConsumerMetrics.CreateSnapshot`)

In the `CreateSnapshot` method, allocate and copy the duration buckets:

```csharp
ConsumerMetricsSnapshot IConsumerMetrics.CreateSnapshot()
{
    long[] queueDepthByLane = new long[_orderingLaneCount];
    long[] eventsProcessed = new long[_consumerMetricsState.ConsumerEventTypeCount];
    long[] handlerDurationBuckets = new long[_consumerMetricsState.HandlerDurationBucketCount_];

    SnapshotQueueDepthByLane(queueDepthByLane);
    _consumerMetricsState.CopyConsumerEventsProcessed(eventsProcessed);
    _consumerMetricsState.CopyHandlerDurationBuckets(handlerDurationBuckets);

    return new ConsumerMetricsSnapshot(
        _consumerMetricsState.GetConsumerHandlerErrors(),
        queueDepthByLane,
        eventsProcessed,
        handlerDurationBuckets
    );
}
```

### `MetricsAwareTestConsumer` (test helper)

Update to also implement the new `handlerDurationBuckets` parameter in its `CreateSnapshot`:

```csharp
ConsumerMetricsSnapshot IConsumerMetrics.CreateSnapshot()
{
    // ... existing array copies ...
    long[] handlerDurationBuckets = new long[HandlerDurationBucketCount];
    CopyHandlerDurationBuckets(handlerDurationBuckets);

    return new ConsumerMetricsSnapshot(
        Interlocked.Read(ref _handlerErrors),
        queueDepthByLane,
        eventsProcessed,
        handlerDurationBuckets
    );
}
```

Or if the test consumer doesn't measure duration, pass an empty array of the correct length.

## Testing

### Unit Tests (`ConsumerMetricsStateTests` or new file)

1. **Bucket boundaries**: simulate elapsed ticks for each boundary threshold, call `RecordHandlerDuration`, verify correct bucket increments.
2. **Capture gating**: duration not recorded when capture is disabled.
3. **Precomputed thresholds** (if implemented): verify threshold ticks match expected microsecond boundaries for the platform's `Stopwatch.Frequency`.

### Integration Tests (`ThreadedTcpServerMetricsTests`)

1. **Duration recorded for data events**: connect a client, send data, wait for processing, capture snapshot. Verify `ConsumerMetrics.HandlerDurationBuckets` has at least one non-zero entry.
2. **Duration recorded for connect/disconnect**: verify the connected and disconnected handler durations also appear in the buckets.
3. **Bucket distribution**: use a test consumer that sleeps for known durations in `HandleReceived` to verify events land in the expected bucket.

### `MetricsAwareTestConsumer`

Update the test consumer's `CreateSnapshot` to include the `handlerDurationBuckets` parameter. If duration tracking isn't needed for that test consumer, pass a zeroed array of the correct length.
