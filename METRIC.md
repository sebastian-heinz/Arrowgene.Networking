# Metrics Reference

`MetricsCollector<TcpServerMetricsSnapshot>.GetMetricsSnapshot()` captures and returns a fresh `TcpServerMetricsSnapshot`.

`MetricsCollector<TcpServerMetricsSnapshot>.GetPublishedMetricsSnapshot()` returns the latest published `TcpServerMetricsSnapshot` without forcing a new capture.

The collector is constructed externally and receives a `TcpServer` (which implements `IMetricsCapture<TcpServerMetricsSnapshot>`).

## Snapshot Fields

| Field | Kind | Meaning |
|---|---|---|
| `TimestampUtc` | Timestamp | UTC time when the snapshot was captured. |
| `ServerStartedAtUtc` | Timestamp | UTC time when the current server run started and metrics capture began. |
| `Uptime` | Duration | Derived elapsed time between `ServerStartedAtUtc` and `TimestampUtc`. |
| `SnapshotSequenceNumber` | Counter | Monotonically increasing sequence number for published snapshots in the current server run. |
| `AcceptedConnections` | Counter | Total number of connections that were accepted and activated. |
| `RejectedConnections` | Counter | Total number of accepted sockets rejected before activation, for example because the server was stopping or the client pool was full. |
| `ActiveConnections` | Gauge | Current number of active connected clients. |
| `PeakActiveConnections` | Gauge | Maximum simultaneous active connections observed since the previous snapshot. |
| `DisconnectedConnections` | Counter | Total number of finalized disconnects. |
| `TimedOutConnections` | Counter | Total number of disconnects initiated by the idle socket timeout. |
| `SendQueueOverflows` | Counter | Total number of times a client exceeded `MaxQueuedSendBytes`. |
| `SocketAcceptErrors` | Counter | Total number of socket errors seen on the accept path. |
| `SocketReceiveErrors` | Counter | Total number of socket errors seen on the receive path. |
| `SocketSendErrors` | Counter | Total number of socket errors seen on the send path. |
| `ZeroByteReceives` | Counter | Total number of zero-byte receive completions that signaled a graceful remote close. |
| `ReceiveOperations` | Counter | Total number of successful receive completions that transferred bytes. |
| `SendOperations` | Counter | Total number of successful send completions that transferred bytes. |
| `BytesReceived` | Counter | Total number of bytes received from clients. |
| `BytesSent` | Counter | Total number of bytes sent to clients. |
| `ReceiveBytesPerSecond` | Rate | Derived inbound throughput for the most recent sampling interval, in bytes per second. |
| `SendBytesPerSecond` | Rate | Derived outbound throughput for the most recent sampling interval, in bytes per second. |
| `ReceiveOpsPerSecond` | Rate | Derived inbound receive-completion rate for the most recent sampling interval, in operations per second. |
| `SendOpsPerSecond` | Rate | Derived outbound send-completion rate for the most recent sampling interval, in operations per second. |
| `AcceptsPerSecond` | Rate | Derived connection accept rate for the most recent sampling interval, in accepts per second. |
| `AcceptPoolAvailable` | Gauge | Current number of available pooled accept event args. |
| `AvailableClientSlots` | Gauge | Current number of available pooled client slots. |
| `TotalSendQueuedBytes` | Gauge | Current total outbound bytes queued across all active clients. |
| `InFlightAsyncCallbacks` | Gauge | Current number of async socket callbacks still executing. |
| `DisconnectCleanupQueueDepth` | Gauge | Current number of disconnected clients waiting for deferred cleanup finalization. |
| `DisconnectsByReason` | Indexed counter | Disconnect totals indexed by `DisconnectReason`. |
| `LaneActiveConnections` | Indexed gauge | Current active connection count per ordering lane. |
| `ConnectionDurationBuckets` | Indexed counter | Connection lifetime histogram recorded at disconnect time using `MetricBucketDefinitions.DurationBucketNames`: `0..100us`, `100us..500us`, `500us..1ms`, `1ms..5ms`, `5ms..10ms`, `10ms..50ms`, `50ms..100ms`, `100ms..250ms`, `250ms..500ms`, `500ms..1s`, `1s..2s`, `2s..5s`, `5s..10s`, `10s..30s`, `30s..1m`, `1m..2m`, `2m..5m`, `5m..10m`, `10m..30m`, `30m..1h+`. |
| `ReceiveSizeBuckets` | Indexed counter | Receive completions bucketed using `MetricBucketDefinitions.TransferSizeBucketNames`: `0..64`, `65..256`, `257..1024`, `1025..4096`, `4097..8192`, `8193..16384`, `16385..65536`, `65537..262144`, `262145..1048576`, `1048577+`. |
| `SendSizeBuckets` | Indexed counter | Send completions bucketed using `MetricBucketDefinitions.TransferSizeBucketNames`: `0..64`, `65..256`, `257..1024`, `1025..4096`, `4097..8192`, `8193..16384`, `16385..65536`, `65537..262144`, `262145..1048576`, `1048577+`. |
| `ConsumerMetrics` | Nested snapshot | Optional consumer metrics snapshot. `null` when the consumer does not implement `IMetricsCapture<ConsumerMetricsSnapshot>`. |
| `SocketErrorsByCode` | Indexed counter | Socket errors indexed by raw `SocketError` value offset from `SocketErrorCodeMinimum`, or queried via `GetSocketErrorCount(SocketError.X)`. |
| `SocketErrorCodeMinimum` | Scalar | Minimum raw `SocketError` value represented in `SocketErrorsByCode`. |

## Consumer Metrics

When `ConsumerMetrics` has a value on `TcpServerMetricsSnapshot`, the nested `ConsumerMetricsSnapshot` contains:

| Field | Kind | Meaning |
|---|---|---|
| `HandlerErrors` | Counter | Total number of consumer handler invocations that threw. |
| `QueueDepthByLane` | Indexed gauge | Current queued event count per ordering lane. |
| `EventsProcessed` | Indexed counter | Successfully processed consumer events indexed by `ClientEventType`. |
| `HandlerDurationBuckets` | Indexed counter | Successful consumer-event handler durations across all event types bucketed using `MetricBucketDefinitions.DurationBucketNames`: `0..100us`, `100us..500us`, `500us..1ms`, `1ms..5ms`, `5ms..10ms`, `10ms..50ms`, `50ms..100ms`, `100ms..250ms`, `250ms..500ms`, `500ms..1s`, `1s..2s`, `2s..5s`, `5s..10s`, `10s..30s`, `30s..1m`, `1m..2m`, `2m..5m`, `5m..10m`, `10m..30m`, `30m..1h+`. |
| `ReceivedDataQueueDelayBuckets` | Indexed counter | Received-data queue delays from threaded-consumer handoff attempt to handler start bucketed using `MetricBucketDefinitions.DurationBucketNames`: `0..100us`, `100us..500us`, `500us..1ms`, `1ms..5ms`, `5ms..10ms`, `10ms..50ms`, `50ms..100ms`, `100ms..250ms`, `250ms..500ms`, `500ms..1s`, `1s..2s`, `2s..5s`, `5s..10s`, `10s..30s`, `30s..1m`, `1m..2m`, `2m..5m`, `5m..10m`, `10m..30m`, `30m..1h+`. Empty when the consumer does not publish this detail. |
| `ReceivedDataHandlerDurationBuckets` | Indexed counter | Successful received-data handler durations bucketed using `MetricBucketDefinitions.DurationBucketNames`: `0..100us`, `100us..500us`, `500us..1ms`, `1ms..5ms`, `5ms..10ms`, `10ms..50ms`, `50ms..100ms`, `100ms..250ms`, `250ms..500ms`, `500ms..1s`, `1s..2s`, `2s..5s`, `5s..10s`, `10s..30s`, `30s..1m`, `1m..2m`, `2m..5m`, `5m..10m`, `10m..30m`, `30m..1h+`. Empty when the consumer does not publish this detail. |

## DisconnectReason

Use `DisconnectsByReason.Span[(int)DisconnectReason.X]` to read a specific disconnect count.

| Value | Meaning |
|---|---|
| `None` | No specific reason was recorded. |
| `RemoteClosed` | The remote peer closed the connection. |
| `AcceptFailure` | Activation failed after the socket was accepted. |
| `ReceiveFailure` | The receive path failed before more data could be processed. |
| `ReceiveCompletedFailure` | The receive completion callback failed unexpectedly. |
| `SendFailure` | The send path failed before queued data could be fully flushed. |
| `SendCompletedFailure` | The send completion callback failed unexpectedly. |
| `SendQueueOverflow` | The client exceeded `MaxQueuedSendBytes`. |
| `Timeout` | The client exceeded `ClientSocketTimeoutSeconds`. |
| `Shutdown` | The server disconnected the client during shutdown. |
| `StaleHandle` | The client handle was stale or no longer mapped to an active connection. |

## Notes

| Item | Meaning |
|---|---|
| Counters | Monotonic totals since the current `TcpServer` instance started. |
| Gauges | Current values at snapshot time. |
| Rates | Derived values computed from recent counter deltas. |
| Per-lane values | Array positions match the ordering lane index. |
| Connection duration buckets | Bucket positions map to `MetricBucketDefinitions.DurationBucketNames`. |
| Size buckets | Bucket positions map to `MetricBucketDefinitions.TransferSizeBucketNames`. |
| Consumer metrics | When `ConsumerMetrics` has a value, use `HandlerErrors`, `QueueDepthByLane`, `GetEventsProcessedCount(ClientEventType.X)`, and the histogram fields on the nested snapshot. |
| Socket error buckets | Use `GetSocketErrorCount(SocketError.X)` for direct lookup, or compute `((int)socketError) - SocketErrorCodeMinimum` to index `SocketErrorsByCode`. |
