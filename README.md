# Arrowgene.Networking

A pooled TCP server library for .NET built on `SocketAsyncEventArgs` (SAEA).

Client slots are pre-allocated and recycled, send/receive buffers come from a shared pinned slab, payloads are copied for isolation, and connected clients are assigned to the least-loaded ordering lane.

[![NuGet](https://img.shields.io/nuget/v/Arrowgene.Networking.svg)](https://www.nuget.org/packages/Arrowgene.Networking)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.md)

## Requirements

- .NET 10.0

## Installation

```bash
dotnet add package Arrowgene.Networking
```

## Core Types

| Type | Purpose |
|---|---|
| `TcpServer` | Owns the listener, client pool, send queues, timeout checks, and shutdown lifecycle. |
| `TcpServerSettings` | Server configuration: connection caps, buffer size, ordering lanes, timeouts. |
| `SocketSettings` | Socket-level tuning applied via `ListenSocketSettings` and `ClientSocketSettings`. |
| `ClientHandle` | Generation-checked struct to `Send`, `Disconnect`, or inspect a live client. |
| `ClientSnapshot` | Immutable state captured at disconnect or error time (ID, endpoint, bytes, timestamps). |
| `TcpServerMetricsSnapshot` | Immutable metrics snapshot returned by `TcpServer.GetMetricsSnapshot()`. |
| `DisconnectReason` | Enum used for disconnect logs and `DisconnectsByReason` indexing. |

## Quick Start

Event-driven echo server:

```csharp
using System;
using System.Net;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer.EventConsumption;

TcpServerSettings settings = new TcpServerSettings
{
    Identity = "Echo",
    MaxConnections = 100,
    BufferSize = 2048,
    OrderingLaneCount = 4,
    ConcurrentAccepts = 8,
    MaxQueuedSendBytes = 16 * 1024 * 1024,
    ClientSocketTimeoutSeconds = -1
};

settings.ListenSocketSettings.Backlog = 128;
settings.ClientSocketSettings.NoDelay = true;

EventConsumer consumer = new EventConsumer();
consumer.ClientConnected += (_, e) =>
{
    Console.WriteLine($"Connected: {e.Socket.Identity} lane={e.Socket.UnitOfOrder}");
};
consumer.ReceivedPacket += (_, e) =>
{
    e.Socket.Send(e.Data);
};
consumer.ClientDisconnected += (_, e) =>
{
    Console.WriteLine(
        $"Disconnected: {e.ClientSnapshot.Identity} recv={e.ClientSnapshot.BytesReceived} sent={e.ClientSnapshot.BytesSent}"
    );
};
consumer.Error += (_, e) =>
{
    Console.WriteLine($"Consumer error for {e.ClientSnapshot.Identity}: {e.Exception.Message}");
};

using TcpServer server = new TcpServer(IPAddress.Any, 2345, consumer, settings);
server.Start();

Console.WriteLine("Press Enter to stop.");
Console.ReadLine();

server.Stop();
```

## Metrics

Poll metrics from the server with `GetMetricsSnapshot()`:

```csharp
using System;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Metric;

TcpServerMetricsSnapshot metrics = server.GetMetricsSnapshot();

Console.WriteLine(
    $"active={metrics.ActiveConnections} " +
    $"accepted={metrics.AcceptedConnections} " +
    $"availableSlots={metrics.AvailableClientSlots} " +
    $"recv={metrics.BytesReceived} " +
    $"sent={metrics.BytesSent} " +
    $"in={metrics.ReceiveBytesPerSecond:F0}/s " +
    $"out={metrics.SendBytesPerSecond:F0}/s"
);

long timeoutDisconnects = metrics.DisconnectsByReason.Span[(int)DisconnectReason.Timeout];
long laneZeroConnections = metrics.LaneActiveConnections.Span[0];
long shortLivedConnections = metrics.ConnectionDurationBuckets.Span[0];
long smallReceives = metrics.ReceiveSizeBuckets.Span[0];
```

The snapshot includes:

- Connection totals and gauges: accepted, rejected, active, disconnected.
- Throughput totals and rates: receive/send operations, bytes, bytes per second.
- Failure and backpressure counters: socket errors, timeouts, send queue overflows.
- Current server state: accept-pool availability, available client slots, in-flight async callbacks, deferred disconnect cleanup depth, per-lane active connections.
- Optional low-cost detail: connection-duration buckets, receive/send size buckets, and per-socket-error-code counters via `GetSocketErrorCount(SocketError.X)`.
- Optional consumer detail via `ConsumerMetrics` when the consumer implements `IConsumerMetrics`; for `ThreadedBlockingQueueConsumer` this includes per-lane queue depth, processed event counts, handler duration buckets, and handler error totals.
- Disconnect reason counters indexed by `DisconnectReason`.

Use `GetMetricsSnapshot()` from your own timer, background service, or health endpoint.

## Consumer Models

### EventConsumer

Simple callback wiring without implementing `IConsumer`. Exposes `ClientConnected`, `ReceivedPacket`, `ClientDisconnected`, and `Error` events. See the Quick Start above.

### BlockingQueueConsumer

Read server events from your own processing loop:

```csharp
using System;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

BlockingQueueConsumer consumer = new BlockingQueueConsumer();

foreach (IClientEvent clientEvent in consumer.ClientEvents.GetConsumingEnumerable())
{
    switch (clientEvent)
    {
        case ClientConnectedEvent connected:
            Console.WriteLine($"Connected: {connected.ClientHandle.Identity}");
            break;
        case ClientDataEvent data:
            data.ClientHandle.Send(data.Data);
            break;
        case ClientDisconnectedEvent disconnected:
            Console.WriteLine($"Disconnected: {disconnected.ClientSnapshot.Identity}");
            break;
        case ClientErrorEvent error:
            Console.WriteLine($"Error for {error.ClientSnapshot.Identity}: {error.Exception.Message}");
            break;
    }
}
```

### ThreadedBlockingQueueConsumer

One worker thread per ordering lane with FIFO ordering preserved inside each lane. The type is abstract -- implement the four handlers then call `Start()` before handing it to `TcpServer`.

```csharp
using System;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

public sealed class EchoConsumer : ThreadedBlockingQueueConsumer
{
    public EchoConsumer(int orderingLaneCount)
        : base(orderingLaneCount, queueCapacityPerLane: 1024, identity: nameof(EchoConsumer))
    {
    }

    protected override void HandleReceived(ClientHandle clientHandle, byte[] data)
    {
        clientHandle.Send(data);
    }

    protected override void HandleDisconnected(ClientSnapshot clientSnapshot)
    {
        Console.WriteLine($"Disconnected: {clientSnapshot.Identity}");
    }

    protected override void HandleConnected(ClientHandle clientHandle)
    {
        Console.WriteLine($"Connected: {clientHandle.Identity} lane={clientHandle.UnitOfOrder}");
    }

    protected override void HandleError(ClientSnapshot clientSnapshot, Exception exception, string message)
    {
        Console.WriteLine($"Error for {clientSnapshot.Identity}: {message} / {exception.Message}");
    }
}
```

Setup:

```csharp
using System.Net;
using Arrowgene.Networking.SAEAServer;

TcpServerSettings settings = new TcpServerSettings
{
    MaxConnections = 200,
    OrderingLaneCount = 4,
    ConcurrentAccepts = 8
};

using EchoConsumer consumer = new EchoConsumer(settings.OrderingLaneCount);
consumer.Start();

using TcpServer server = new TcpServer(IPAddress.Any, 2345, consumer, settings);
server.Start();
```

## Configuration

### TcpServerSettings

| Option | Default | Purpose | When to change |
|---|---|---|---|
| `Identity` | `""` | Label prefixed to all log lines and thread names for this server instance. | Set when running multiple `TcpServer` instances in the same process so logs and thread dumps are distinguishable. |
| `MaxConnections` | `100` | Upper bound on simultaneous connected clients. Determines the size of the client pool and the pinned buffer slab (`MaxConnections * BufferSize * 2` bytes). | Raise for high-concurrency services (game servers, chat). Lower to cap memory on resource-constrained hosts. Connections beyond this limit are refused at accept time. |
| `BufferSize` | `8192` | Size in bytes of the pinned receive and send buffer allocated per client per direction. Receive completions copy at most this many bytes per callback; outbound sends are chunked to this size. | Increase when your protocol frames are large (file transfer, media streaming) to reduce per-message callback overhead. Decrease for many small-message workloads (chat, telemetry) to reduce pinned memory. |
| `OrderingLaneCount` | `4` | Number of ordering lanes. Each connected client is assigned to the least-loaded lane. `ThreadedBlockingQueueConsumer` creates one worker thread per lane and guarantees FIFO within a lane. | Match to the number of consumer worker threads. More lanes = more parallelism but less ordering guarantee across clients. Fewer lanes = stronger cross-client ordering but higher head-of-line blocking risk. |
| `ConcurrentAccepts` | `10` | Maximum simultaneous pending `AcceptAsync` operations. Controls how many accept event args are pooled and handed to the OS at once. | Raise under burst-connect workloads (load tests, game lobby joins) where many clients connect within milliseconds. Lower if accept throughput is not a bottleneck to save a few allocations. |
| `MaxQueuedSendBytes` | `16 MB` | Per-client outbound queue byte limit. When a client's queued send data exceeds this, the client is disconnected. | Raise for clients that receive large bursts (bulk data push, replay streaming). Lower to shed slow consumers faster and protect server memory. |
| `ListenSocketRetries` | `5` | Number of times the server retries `Bind`+`Listen` (with a 1-second delay) before giving up on startup. | Raise in environments where port release is slow (container restarts, CI). Set to `0` for fail-fast startup. |
| `ClientSocketTimeoutSeconds` | `-1` | Idle timeout in seconds. Clients with no send or receive activity for this long are disconnected. `-1` or `0` disables the timeout. | Enable (`30`-`300`) for public-facing servers to reclaim slots from idle or half-open connections. Leave disabled for trusted internal services or long-lived connections. |
| `ListenSocketSettings` | *(default `SocketSettings`)* | `SocketSettings` instance applied to the listener socket before `Bind`. | Configure `Backlog`, `ExclusiveAddressUse`, `DualMode`, or raw socket options for the listening socket. |
| `ClientSocketSettings` | *(default `SocketSettings`)* | `SocketSettings` instance applied to each accepted client socket. | Set `NoDelay = true` for low-latency protocols, tune `ReceiveBufferSize`/`SendBufferSize` for throughput, or enable `LingerEnabled` for graceful close. |

### SocketSettings

Applied via `ListenSocketSettings` (listener socket) and `ClientSocketSettings` (accepted sockets).

| Option | Default | Purpose | When to change |
|---|---|---|---|
| `Backlog` | `128` | Maximum pending connection queue length passed to `Socket.Listen`. | Raise under high-burst connect rates. Only applies to `ListenSocketSettings`. |
| `NoDelay` | `false` | Disables the Nagle algorithm when `true`, sending data immediately without buffering small writes. | Set `true` on `ClientSocketSettings` for latency-sensitive protocols (games, real-time). Leave `false` for throughput-oriented workloads. |
| `ReceiveBufferSize` | `8192` | OS-level socket receive buffer size in bytes. | Increase for high-throughput streams. Decrease to limit per-socket kernel memory. |
| `SendBufferSize` | `8192` | OS-level socket send buffer size in bytes. | Increase for bursty outbound traffic. Decrease to apply tighter backpressure to the send path. |
| `ReceiveTimeout` | `0` | Synchronous receive timeout in milliseconds. `0` means infinite. | Rarely needed since the server uses async I/O. Useful if synchronous reads are added externally. |
| `SendTimeout` | `0` | Synchronous send timeout in milliseconds. `0` means infinite. | Same as `ReceiveTimeout`. |
| `Ttl` | `64` | IP Time To Live. Decremented at each router hop; packets are dropped when it reaches zero. | Raise (`128`) for cross-region or multi-hop cloud deployments. Lower for LAN-only services. |
| `DualMode` | `false` | Enables IPv4+IPv6 dual-stack on an `InterNetworkV6` socket. | Set `true` when binding to `IPAddress.IPv6Any` and you want to accept both IPv4 and IPv6 clients. |
| `ExclusiveAddressUse` | `false` | Prevents other sockets from binding to the same port. | Set `true` on `ListenSocketSettings` to prevent port hijacking in production. |
| `DontFragment` | `true` | Sets the IP Don't Fragment flag. Only applied to datagram sockets. | Not relevant for TCP. Applies if `SocketSettings` is reused for UDP sockets. |
| `LingerEnabled` | `false` | Enables the linger option on socket close. When `true`, `Close` blocks for up to `LingerTime` seconds to flush queued data. | Enable for protocols that require guaranteed delivery of the final bytes before disconnect. |
| `LingerTime` | `30` | Linger duration in seconds when `LingerEnabled` is `true`. | Shorten for faster teardown. Lengthen if the remote peer is slow to acknowledge. |
| `SocketOptions` | `[ReuseAddress=false]` | Additional raw `SetSocketOption` calls applied after the typed settings above. | Use for platform-specific or uncommon socket options not covered by the typed properties. |

## Build and Test

```bash
dotnet build
dotnet test
dotnet test --logger "console;verbosity=detailed"
```

## Dependency

- [Arrowgene.Logging](https://github.com/sebastian-heinz/Arrowgene.Logging)

## License

MIT License. See [LICENSE.md](LICENSE.md).
