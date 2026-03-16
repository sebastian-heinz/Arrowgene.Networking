# Metrics Reference

`TcpServer.GetMetricsSnapshot()` returns a `TcpServerMetricsSnapshot` with the current server metrics.

## Snapshot Fields

| Field | Kind | Meaning |
|---|---|---|
| `TimestampUtc` | Timestamp | UTC time when the snapshot was captured. |
| `AcceptedConnections` | Counter | Total number of connections that were accepted and activated. |
| `RejectedConnections` | Counter | Total number of accepted sockets rejected before activation, for example because the server was stopping or the client pool was full. |
| `ActiveConnections` | Gauge | Current number of active connected clients. |
| `DisconnectedConnections` | Counter | Total number of finalized disconnects. |
| `TimedOutConnections` | Counter | Total number of disconnects initiated by the idle socket timeout. |
| `SendQueueOverflows` | Counter | Total number of times a client exceeded `MaxQueuedSendBytes`. |
| `SocketAcceptErrors` | Counter | Total number of socket errors seen on the accept path. |
| `SocketReceiveErrors` | Counter | Total number of socket errors seen on the receive path. |
| `SocketSendErrors` | Counter | Total number of socket errors seen on the send path. |
| `ReceiveOperations` | Counter | Total number of successful receive completions that transferred bytes. |
| `SendOperations` | Counter | Total number of successful send completions that transferred bytes. |
| `BytesReceived` | Counter | Total number of bytes received from clients. |
| `BytesSent` | Counter | Total number of bytes sent to clients. |
| `ReceiveBytesPerSecond` | Rate | Derived inbound throughput for the most recent sampling interval, in bytes per second. |
| `SendBytesPerSecond` | Rate | Derived outbound throughput for the most recent sampling interval, in bytes per second. |
| `AcceptPoolAvailable` | Gauge | Current number of available pooled accept event args. |
| `AvailableClientSlots` | Gauge | Current number of available pooled client slots. |
| `InFlightAsyncCallbacks` | Gauge | Current number of async socket callbacks still executing. |
| `DisconnectCleanupQueueDepth` | Gauge | Current number of disconnected clients waiting for deferred cleanup finalization. |
| `DisconnectsByReason` | Indexed counter | Disconnect totals indexed by `DisconnectReason`. |
| `LaneActiveConnections` | Indexed gauge | Current active connection count per ordering lane. |
| `ReceiveSizeBuckets` | Indexed counter | Receive completions bucketed into these ranges: `0..64`, `65..256`, `257..1024`, `1025..4096`, `4097..8192`, `8193..16384`, `16385+`. |
| `SendSizeBuckets` | Indexed counter | Send completions bucketed into these ranges: `0..64`, `65..256`, `257..1024`, `1025..4096`, `4097..8192`, `8193..16384`, `16385+`. |
| `SocketErrorsByCode` | Indexed counter | Socket errors indexed by raw `SocketError` value offset from `SocketErrorCodeMinimum`, or queried via `GetSocketErrorCount(SocketError.X)`. |
| `SocketErrorCodeMinimum` | Scalar | Minimum raw `SocketError` value represented in `SocketErrorsByCode`. |

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
| Size buckets | Bucket positions map to the fixed ranges listed above. |
| Socket error buckets | Use `GetSocketErrorCount(SocketError.X)` for direct lookup, or compute `((int)socketError) - SocketErrorCodeMinimum` to index `SocketErrorsByCode`. |
