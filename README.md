# Arrowgene.Networking

A high-performance, pooled TCP server library for .NET built on `SocketAsyncEventArgs` (SAEA). Designed for scalable, event-driven networking with efficient resource management.

[![NuGet](https://img.shields.io/nuget/v/Arrowgene.Networking.svg)](https://www.nuget.org/packages/Arrowgene.Networking)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.md)

## Features

- **SAEA-based architecture** - Built on .NET's `SocketAsyncEventArgs` for high-throughput, low-allocation I/O
- **Connection pooling** - Pre-allocated client slots with generation tracking to detect stale references
- **Pinned buffer management** - Single pinned memory slab shared across all clients, avoiding GC pressure
- **Ordering lanes** - Distributes clients across configurable lanes for deterministic per-lane FIFO event ordering
- **Bounded send queues** - Per-client outbound queue with configurable byte limits and overflow protection
- **Idle timeout** - Automatic disconnection of idle clients with configurable timeout
- **Pluggable consumers** - Choose between event-based or blocking-queue-based callback models

## Requirements

- [.NET 10.0](https://dotnet.microsoft.com/download) or later

## Installation

### NuGet

```
dotnet add package Arrowgene.Networking
```

### Build from source

```bash
git clone https://github.com/sebastian-heinz/Arrowgene.Networking.git
cd Arrowgene.Networking
dotnet build
```

## Quick start

```csharp
using System.Net;
using Arrowgene.Networking.Consumer.EventConsumption;
using Arrowgene.Networking.SAEAServer;

// Configure the server
var settings = new ServerSettings
{
    MaxConnections = 100,
    BufferSize = 2000,
    OrderingLaneCount = 4,
    ConcurrentAccepts = 10,
    MaxQueuedSendBytes = 16 * 1024 * 1024,
    ClientSocketTimeoutSeconds = -1 // disabled
};

// Use the event-based consumer
var consumer = new EventConsumer();
consumer.ClientConnected += (s, e) =>
{
    Console.WriteLine($"Client connected: {e.ClientHandle.Id}");
};
consumer.ClientDisconnected += (s, e) =>
{
    Console.WriteLine($"Client disconnected: {e.ClientSnapshot.ClientId}");
};
consumer.ReceivedPacket += (s, e) =>
{
    // Echo received data back to the client
    e.ClientHandle.Send(e.Data);
};

// Start listening
var server = new Server(settings, consumer);
server.Start(new IPEndPoint(IPAddress.Any, 2345));
```

## Architecture

```
Server
├── AcceptPool           - Reusable SocketAsyncEventArgs for accept operations
├── BufferSlab           - Single pinned byte[] divided among all client slots
├── ClientRegistry       - Client pool manager with ordering lane assignment
│   └── Client[]         - Pre-allocated, reusable client slots
│       ├── SendQueue    - Per-client bounded outbound queue
│       └── ClientHandle - Generation-checked reference for consumers
└── IConsumer            - Callback interface for server events
    ├── EventConsumer               - .NET event-based callbacks
    ├── BlockingQueueConsumer       - BlockingCollection-based queue
    └── ThreadedBlockingQueueConsumer - Dedicated processing thread
```

### Key concepts

| Concept | Description |
|---|---|
| **ClientHandle** | A lightweight `readonly struct` that consumers use to reference connected clients. Includes generation tracking so stale handles are safely rejected. |
| **ClientSnapshot** | An immutable `record struct` capturing complete client state at disconnection (ID, IP, bytes sent/received, connection duration). |
| **Ordering lanes** | Clients are assigned to the least-loaded lane on connection. Events within a lane are processed in FIFO order. |
| **BufferSlab** | A single `GC.AllocateArray<byte>(pinned: true)` allocation divided into per-client receive and send regions, eliminating per-operation allocations. |

## Configuration

`ServerSettings` provides the following options:

| Property | Default | Description |
|---|---|---|
| `MaxConnections` | `100` | Maximum concurrent connections |
| `BufferSize` | `2000` | Receive/send buffer size per direction per client |
| `OrderingLaneCount` | `4` | Number of ordering lanes for event dispatch |
| `ConcurrentAccepts` | `10` | Simultaneous pending accept operations |
| `MaxQueuedSendBytes` | `16 MB` | Maximum queued outbound bytes per client before overflow |
| `ClientSocketTimeoutSeconds` | `-1` | Idle timeout in seconds (`-1` = disabled) |
| `ListenSocketRetries` | `5` | Number of bind retries for the listen socket |
| `DebugMode` | `false` | Enable verbose debug logging |

Socket-level settings (backlog, buffer sizes, timeouts, NoDelay, TTL, linger) are configurable via `ListenSocketSettings` and `ClientSocketSettings`.

## Consumer models

### EventConsumer

Raises .NET events for each server callback. Suitable for simple use cases.

```csharp
var consumer = new EventConsumer();
consumer.ClientConnected += (s, e) => { /* handle */ };
consumer.ClientDisconnected += (s, e) => { /* handle */ };
consumer.ReceivedPacket += (s, e) => { /* handle */ };
```

### BlockingQueueConsumer

Enqueues all events into a `BlockingCollection<ClientEvent>` for external processing loops.

```csharp
var consumer = new BlockingQueueConsumer();
// Process events on your own thread
foreach (var evt in consumer.ClientEvents.GetConsumingEnumerable())
{
    // handle evt
}
```

### ThreadedBlockingQueueConsumer

Extends `BlockingQueueConsumer` with a dedicated background thread for event processing.

## Testing

```bash
dotnet test
```

With detailed output:

```bash
dotnet test --logger "console;verbosity=detailed"
```

The test suite covers server lifecycle, connection caps, ordering lanes, pooling behavior, buffer handling, and disconnect/timeout scenarios.

## Project structure

```
Arrowgene.Networking/
├── SAEAServer/
│   ├── Server.cs              # Core TCP server
│   ├── Client.cs              # Pooled client connection
│   ├── ClientHandle.cs        # Generation-checked client reference
│   ├── ClientSnapshot.cs      # Immutable disconnect snapshot
│   ├── ClientRegistry.cs      # Client pool + ordering lanes
│   ├── AcceptPool.cs          # Accept operation pool
│   ├── BufferSlab.cs          # Pinned buffer allocation
│   ├── SendQueue.cs           # Per-client send queue
│   └── ServerSettings.cs      # Configuration
├── Consumer/
│   ├── IConsumer.cs           # Consumer callback interface
│   ├── EventConsumption/      # Event-based consumer
│   └── BlockingQueueConsumption/  # Queue-based consumers
├── Service.cs                 # Utility methods
├── SocketSettings.cs          # Socket configuration
└── SocketOption.cs            # Raw socket options
Arrowgene.Networking.Tests/    # xUnit integration tests
```

## Dependencies

- [Arrowgene.Logging](https://github.com/sebastian-heinz/Arrowgene.Logging) v1.2.1

## Platform support

Published builds are available for:
- `win-x86`
- `win-x64`
- `linux-x64`
- `osx-x64`

## License

MIT License - Copyright 2017-2026 Sebastian Heinz

See [LICENSE.md](LICENSE.md) for details.
