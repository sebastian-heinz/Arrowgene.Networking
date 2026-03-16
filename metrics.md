# Metrics Proposal

## Summary

This document proposes adding low-overhead metrics to `Arrowgene.Networking` without pushing exporter, allocation, or string-processing work into the socket hot path.

The core design is:

1. Track metrics in internal custom types.
2. Use atomic counters and fixed-size arrays in hot paths.
3. Run a dedicated background collector thread that periodically snapshots counters, computes rates, and publishes an immutable snapshot.
4. Keep metric cardinality fixed and bounded. No per-client metrics, no dynamic tags, no endpoint labels.

This approach matches the current server design: pooled clients, fixed ordering lanes, and explicit lifecycle threads.

## Goals

- Add useful operational visibility for the TCP server.
- Keep receive, send, and consumer-dispatch instrumentation allocation-free.
- Avoid locks in the hot path that exist only for metrics.
- Avoid external dependencies.
- Expose metrics through simple custom types owned by the library.
- Make the first implementation small enough to review and test with confidence.

## Non-Goals

- Per-client metrics.
- Tracing or request-span style instrumentation.
- Dynamic label systems.
- Exporter integrations in the first pass.
- High-cardinality dimensions such as remote IP, endpoint string, client ID, or exception message.

## Design Principles

### Hot-path rules

Hot-path code should only do the following for metrics:

- `Interlocked.Increment`
- `Interlocked.Add`
- `Volatile.Read`
- `Volatile.Write`
- Fixed bucket index calculation

Hot-path code should not do the following for metrics:

- Allocate objects
- Build strings
- Create tag collections
- Start or stop timers
- Use `DateTime.UtcNow` for every packet
- Take new locks that are only needed for metrics

### Snapshot rules

The metrics collector thread can do heavier work because it runs off the hot path:

- Read and copy atomic counters
- Sum per-lane arrays
- Compute deltas and rates
- Build immutable snapshots
- Read queue depths and lane loads from existing structures

### Cardinality rules

Metric dimensions must remain finite and fixed:

- Per-lane metrics are acceptable because lane count is bounded by configuration.
- Per-reason metrics are acceptable when the reason set is backed by an enum.
- Per-socket-operation metrics are acceptable when the operation set is fixed.
- Per-client, per-endpoint, per-error-message, and per-exception-type metrics are not acceptable.

## Proposed Architecture

### Core internal types

### `TcpServerMetricsState`

Mutable internal state owned by `TcpServer`. This type contains only atomically updated fields and fixed-size arrays.

Proposed responsibilities:

- Maintain cumulative counters.
- Maintain current gauges that are cheap to update inline.
- Maintain per-lane counters.
- Maintain fixed histogram bucket counters when enabled.

Sketch:

```csharp
internal sealed class TcpServerMetricsState
{
    internal long AcceptedConnections;
    internal long RejectedConnections;
    internal long DisconnectedConnections;
    internal long TimedOutConnections;
    internal long SendQueueOverflows;

    internal long SocketAcceptErrors;
    internal long SocketReceiveErrors;
    internal long SocketSendErrors;

    internal long ReceiveOperations;
    internal long SendOperations;
    internal long BytesReceived;
    internal long BytesSent;

    internal long ActiveConnections;
    internal long InFlightAsyncCallbacks;
    internal long DisconnectCleanupQueueDepth;

    internal long[] DisconnectsByReason;
    internal long[] LaneActiveConnections;
    internal long[] ReceiveSizeBuckets;
    internal long[] SendSizeBuckets;
}
```

### `TcpServerMetricsCollector`

Dedicated background thread owned by `TcpServer`.

Responsibilities:

- Wake up on a fixed interval.
- Snapshot the mutable metrics state.
- Read additional gauges from existing runtime structures.
- Compute interval deltas and derived rates.
- Publish the latest immutable snapshot.

Expected behavior:

- Start when the server starts.
- Stop when the server shuts down.
- Recreate cleanly when the server is restarted after `Stop()`.
- Avoid blocking the socket path during shutdown.

### `TcpServerMetricsSnapshot`

Immutable record struct representing the latest published metrics.

Responsibilities:

- Provide safe read access to current totals, gauges, and rates.
- Capture a timestamp for the snapshot.
- Copy arrays so readers cannot observe mutable internal state.

Sketch:

```csharp
public readonly record struct TcpServerMetricsSnapshot(
    DateTime TimestampUtc,
    long AcceptedConnections,
    long RejectedConnections,
    long ActiveConnections,
    long DisconnectedConnections,
    long TimedOutConnections,
    long SendQueueOverflows,
    long SocketAcceptErrors,
    long SocketReceiveErrors,
    long SocketSendErrors,
    long ReceiveOperations,
    long SendOperations,
    long BytesReceived,
    long BytesSent,
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond,
    long InFlightAsyncCallbacks,
    long DisconnectCleanupQueueDepth,
    long[] DisconnectsByReason,
    long[] LaneActiveConnections
);
```

### `DisconnectReason`

Introduce an internal enum for metric accounting instead of relying on free-form strings.

Rationale:

- Existing `Disconnect(ClientHandle, string reason = "")` is useful for logs.
- Metrics should not parse or aggregate free-form strings.
- An enum-backed array is cheaper and safer.

Sketch:

```csharp
internal enum DisconnectReason
{
    None = 0,
    RemoteClosed = 1,
    AcceptFailure = 2,
    ReceiveSocketError = 3,
    ReceiveCompletedFailure = 4,
    SendSocketError = 5,
    SendCompletedFailure = 6,
    SendQueueOverflow = 7,
    Timeout = 8,
    Shutdown = 9,
    StaleHandle = 10
}
```

The exact enum values can change during implementation. The important part is using a fixed, reviewed reason set.

## Collector thread model

### Sampling interval

Initial proposal:

- Internal fixed interval of `1000 ms`.

Why:

- Good enough for operational visibility.
- Keeps rate calculations stable.
- Avoids frequent wake-ups and snapshot churn.

Possible later extension:

- Add `TcpServerSettings.EnableMetrics`.
- Add `TcpServerSettings.MetricsSamplingIntervalMs`.

Those settings are not required for the first pass.

### Collector workflow

On each interval:

1. Read all atomic counters from `TcpServerMetricsState`.
2. Read current lane loads from `ClientRegistry`.
3. Read accept-pool availability from `AcceptPool` if exposed in the snapshot.
4. Optionally read threaded-consumer queue depths if the active consumer supports that surface.
5. Compute deltas against the previous snapshot sample.
6. Compute interval rates such as bytes per second.
7. Publish a new immutable `TcpServerMetricsSnapshot`.

Publishing model:

- Store the latest snapshot in a field updated with `Volatile.Write`.
- Readers obtain a copy through a server method or property.
- No reader should access mutable internal arrays directly.

## Metrics to implement

### Phase 1: Core server metrics

These are the metrics I would implement first.

| Metric | Type | Source | Notes |
|---|---|---|---|
| `AcceptedConnections` | Counter | Accept success | Count successful activations. |
| `RejectedConnections` | Counter | Accept reject paths | Includes pool exhaustion and server-not-running rejection. |
| `ActiveConnections` | Gauge | Activation and final disconnect | Current connected clients. |
| `DisconnectedConnections` | Counter | Finalized disconnect | Increment once when client returns to pool. |
| `DisconnectsByReason` | Counter array | Finalized disconnect | Fixed enum-indexed counts. |
| `TimedOutConnections` | Counter | Timeout thread | Separate from generic disconnect total. |
| `ReceiveOperations` | Counter | Successful receive processing | Count payload receive callbacks that produced bytes. |
| `SendOperations` | Counter | Successful send processing | Count send completions with bytes transferred. |
| `BytesReceived` | Counter | Receive processing | Total inbound bytes. |
| `BytesSent` | Counter | Send processing | Total outbound bytes. |
| `ReceiveBytesPerSecond` | Derived rate | Collector | Delta over sample interval. |
| `SendBytesPerSecond` | Derived rate | Collector | Delta over sample interval. |
| `SocketAcceptErrors` | Counter | Accept socket exception/error paths | Operation-level total only in phase 1. |
| `SocketReceiveErrors` | Counter | Receive socket error paths | Operation-level total only in phase 1. |
| `SocketSendErrors` | Counter | Send socket error paths | Operation-level total only in phase 1. |
| `SendQueueOverflows` | Counter | Send queue overflow path | Critical backpressure signal. |
| `InFlightAsyncCallbacks` | Gauge | `EnterAsyncCallback` / `ExitAsyncCallback` | Useful during shutdown and error diagnosis. |
| `DisconnectCleanupQueueDepth` | Gauge | Deferred cleanup queue | Detect disconnect finalization backlog. |
| `LaneActiveConnections` | Gauge array | Client registry | Validate least-loaded lane balancing. |

### Phase 2: Optional low-cost detail

These are still compatible with the proposed architecture, but I would defer them until the first pass is in place and tested.

| Metric | Type | Source | Notes |
|---|---|---|---|
| `ReceiveSizeBuckets` | Counter array | Receive processing | Fixed receive-size histogram buckets. |
| `SendSizeBuckets` | Counter array | Send processing | Fixed send-size histogram buckets. |
| `AcceptPoolAvailable` | Gauge | Accept pool | Indicates accept concurrency saturation. |
| `AvailableClientSlots` | Gauge | Client registry | Derived from `MaxConnections - ActiveConnections` or pool count. |
| `SocketErrorsByCode` | Counter array | Error paths | Optional enum-indexed `SocketError` counts. |

### Phase 3: Threaded consumer metrics

These metrics apply only when `ThreadedBlockingQueueConsumer` is used.

| Metric | Type | Source | Notes |
|---|---|---|---|
| `ConsumerQueueDepthByLane` | Gauge array | Enqueue/dequeue path | Shows handler backlog. |
| `ConsumerHandlerErrors` | Counter | Catch block in worker loop | Indicates handler failures. |
| `ConsumerEventsProcessed` | Counter array | Worker loop | One counter per event type or per lane. |
| `ConsumerHandlerDurationBuckets` | Counter array | Worker loop | Optional and only if needed; not phase 1. |

## Histogram bucket proposal

If phase 2 histogram-style tracking is added, use fixed bucket counters instead of general-purpose histogram objects.

Suggested buckets for send and receive sizes:

- `0..64`
- `65..256`
- `257..1024`
- `1025..4096`
- `4097..8192`
- `8193..16384`
- `16385+`

Implementation note:

- Bucket selection is a small branch chain.
- Bucket counters are incremented atomically.
- Aggregation remains off-path.

## Integration points

### `TcpServer`

### Accept path

Relevant methods:

- `ProcessAccept`
- `AcceptCompleted`
- `Run`

Proposed updates:

- Increment `AcceptedConnections` after successful client activation.
- Increment `RejectedConnections` when no client slot is available.
- Increment `RejectedConnections` when the server is no longer running and an accepted socket is rejected.
- Increment `SocketAcceptErrors` on accept error paths.
- Increment `ActiveConnections` when a client becomes live.

### Receive path

Relevant methods:

- `ProcessReceive`
- `ReceiveCompleted`
- `StartReceive`

Proposed updates:

- Increment `SocketReceiveErrors` on receive socket error paths.
- Increment `ReceiveOperations` when bytes are successfully received.
- Add `bytesTransferred` to `BytesReceived`.
- Optionally increment the receive-size bucket counter.
- Do not allocate any metric objects.

### Send path

Relevant methods:

- `Send`
- `StartSend`
- `ProcessSend`
- `SendCompleted`

Proposed updates:

- Increment `SendQueueOverflows` before disconnecting on queue overflow.
- Increment `SocketSendErrors` on send socket error paths.
- Increment `SendOperations` when bytes are successfully sent.
- Add `bytesTransferred` to `BytesSent`.
- Optionally increment the send-size bucket counter.

### Disconnect path

Relevant methods:

- `Disconnect`
- `TryFinalizeDisconnect`
- `CheckSocketTimeout`
- `CleanupDisconnectedClients`
- `ProcessDisconnectCleanupQueue`

Proposed updates:

- Increment `TimedOutConnections` when disconnect is initiated by socket timeout.
- Increment the appropriate `DisconnectsByReason` bucket when disconnect is finalized.
- Increment `DisconnectedConnections` once per finalized disconnect.
- Decrement `ActiveConnections` once per finalized disconnect.
- Track deferred cleanup queue depth when handles are enqueued and drained.

### Callback tracking

Relevant methods:

- `EnterAsyncCallback`
- `ExitAsyncCallback`

Proposed updates:

- Mirror `_inFlightAsyncCallbacks` into metrics state or expose the existing field via the collector.
- Keep this as a current gauge only.

### `ClientRegistry`

Relevant responsibilities:

- Least-loaded lane selection
- Active handle tracking
- Pool availability

Proposed updates:

- Expose a method that snapshots `_laneLoadByIndex` into a caller-supplied buffer.
- Optionally expose available pooled client count.
- Avoid duplicating lane load state unless measurement proves it is necessary.

Collector-facing helper sketch:

```csharp
internal void SnapshotLaneLoads(long[] destination)
{
    // Copies lane load values under the existing registry lock.
}
```

### `ThreadedBlockingQueueConsumer`

This is a separate metrics domain and should remain optional.

Proposed updates:

- Maintain a per-lane queue-depth array updated on enqueue and after dequeue.
- Increment `ConsumerHandlerErrors` in the worker exception handler.
- Optionally maintain fixed bucket counters for handler durations.

Important constraint:

- Handler timing should be a later addition. It is more invasive than the first-pass server counters.

## Public API proposal

The internal state and collector types should remain internal.

The public surface should be small:

- `TcpServerMetricsSnapshot` as a public immutable record struct.
- `TcpServer.GetMetricsSnapshot()` or `TcpServer.Metrics` as the read surface.

Candidate API:

```csharp
/// <summary>
/// Returns the latest metrics snapshot for the server.
/// </summary>
public TcpServerMetricsSnapshot GetMetricsSnapshot();
```

Why this shape:

- Easy to consume.
- No background subscriptions.
- No exposure of mutable state.
- Compatible with future adapters to `System.Diagnostics.Metrics` if desired.

## Implementation sequence

### Step 1: Add server metrics core types

Create:

- `TcpServerMetricsState`
- `TcpServerMetricsCollector`
- `TcpServerMetricsSnapshot`
- `DisconnectReason`

### Step 2: Wire metrics into `TcpServer`

Add counter updates to:

- accept success and rejection paths
- receive success and error paths
- send success and error paths
- timeout disconnect path
- disconnect finalization path
- async callback enter/exit path

### Step 3: Add snapshot helpers to runtime structures

Add collector-facing snapshot methods for:

- lane loads in `ClientRegistry`
- optional available-client count
- optional accept-pool state

### Step 4: Expose public snapshot access

Add:

- `GetMetricsSnapshot()` or equivalent public property

Ensure:

- snapshot reads are safe before `Start()`
- snapshot reads are safe after `Stop()`
- snapshot reads are safe after `Dispose()`

### Step 5: Add tests

Add tests covering:

- accepted and rejected connection counts
- active connection gauge transitions
- byte counters on send and receive
- timeout disconnect counting
- send queue overflow counting
- disconnect reason counting
- snapshot stability across stop and restart

### Step 6: Evaluate optional phase 2 metrics

Only after the core counters are stable:

- add size buckets
- add consumer queue metrics
- add accept-pool and socket-error detail

## Testing strategy

### Functional tests

Use existing integration style to verify:

- connect increments accepted and active metrics
- disconnect decrements active and increments finalized counts
- echo-style traffic increments send and receive bytes
- idle timeout increments timeout-related metrics
- queue overflow increments overflow-related metrics

### Lifecycle tests

Verify:

- metrics collector starts and stops cleanly
- restart after `Stop()` produces valid snapshots
- collector does not block shutdown
- snapshots remain readable even when the server is stopped

## Risk areas

The main risks are:

- double-counting disconnects if both initiation and finalization paths update totals
- counting bytes before the operation is actually confirmed successful
- introducing metric branches that accidentally allocate or lock on every packet
- coupling the server too tightly to threaded-consumer-only concepts

The implementation should count finalized disconnects in one place only: `TryFinalizeDisconnect`.

## Open questions

These can be resolved during implementation review:

- Should metrics be always on, or gated by a new `TcpServerSettings.EnableMetrics` flag?
- Should the first pass expose only server metrics, or should `ThreadedBlockingQueueConsumer` expose its own snapshot too?
- Should detailed `SocketError` code counts be included in the first pass, or deferred?
- Should fixed bucket arrays be public in the first snapshot type, or added only after basic counters prove useful?

## Recommendation

I recommend implementing phase 1 first and stopping there for the initial PR.

That gives the highest-value signals with the lowest performance and review risk:

- connection lifecycle
- throughput
- send queue overflow
- timeout count
- lane load
- async callback and cleanup backlog visibility

After that lands, phase 2 can add fixed buckets and phase 3 can add optional threaded-consumer metrics if they are still needed.
