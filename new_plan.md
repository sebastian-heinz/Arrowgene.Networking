# New SAEA Server Plan

## Goal

Build a new TCP server in `Arrowgene.Networking/Tcp/Server/SAEAServer` that is:

- fully based on `SocketAsyncEventArgs`
- isolated at the application boundary
- preallocated and pooled everywhere else
- simple enough to reason about under failure
- reusable across start/stop cycles without stale-handle bugs

The important change from `new_architecture.md` is intentional: receive and send payloads are always copied at the application boundary. Zero-copy receive is not part of this design.

## Core Decisions

### 1. Copy isolation is mandatory

- Every receive callback gets a fresh `byte[]`.
- Every send call copies the caller payload into server-owned memory before it enters the queue.
- The server never exposes pinned transport buffers to application code.

This costs allocations on receive, but it removes aliasing bugs, accidental buffer retention, and handler-side corruption of the transport state. That is the right trade for a server API that should be hard to misuse.

### 2. The transport stays pooled and pinned

- One pinned receive buffer and one pinned send buffer are reserved per session slot.
- Session objects, SAEA instances, and send queues are all created up front.
- Outbound message copies are rented from `ArrayPool<byte>` and returned as soon as the send chain completes or is cleared.

### 3. No inheritance-based server shape

- The new server is a single facade: `SaeaServer`.
- It does not inherit from `TcpServer`.
- It does not depend on `IConsumer` or `ITcpSocket`.
- The new public contracts are:
  - `ISaeaConnectionHandler`
  - `ISaeaSession`
  - `SaeaSessionHandle`
  - `SaeaServerOptions`

### 4. Accept should not use a dedicated orchestration thread

The older design and `new_architecture.md` both still lean toward an accept loop thread. This implementation does not.

- `SaeaAcceptLoop` posts `ConcurrentAccepts` accept operations immediately.
- Each completion processes the accepted socket and reposts the same `SocketAsyncEventArgs`.
- Shutdown is handled by closing the listener socket and draining the outstanding accept count.

This is leaner, removes one thread entirely, and is the more natural SAEA pattern.

## Objects

### Public surface

#### `SaeaServer`

Owns the full lifecycle:

- startup and shutdown
- listener ownership
- session pool ownership
- session registry ownership
- idle timeout monitor ownership
- handler dispatch
- disconnect orchestration

Public methods:

- `Start()`
- `Stop()`
- `Send(SaeaSessionHandle, byte[])`
- `Send(SaeaSessionHandle, ReadOnlySpan<byte>)`
- `Disconnect(SaeaSessionHandle)`

#### `SaeaServerOptions`

Concrete settings only. No framework-level abstractions.

Current options:

- `Identity`
- `MaxConnections`
- `BufferSize`
- `ConcurrentAccepts`
- `BindRetries`
- `MaxQueuedSendBytes`
- `DrainTimeoutMs`
- `SocketTimeoutSeconds`
- `ListenerSocketSettings`
- `ClientSocketSettings`
- `DebugMode`

#### `ISaeaConnectionHandler`

Application callback contract:

- `OnServerStarted()`
- `OnServerStopped()`
- `OnConnected(ISaeaSession)`
- `OnReceived(ISaeaSession, byte[])`
- `OnDisconnected(ISaeaSession, SocketError)`

#### `ISaeaSession`

Connection view exposed to handlers:

- metadata and counters
- `Send(byte[])`
- `Send(ReadOnlySpan<byte>)`
- `Close()`

#### `SaeaSessionHandle`

Generation-checked handle over a pooled session slot.

- stale handles throw
- external code never touches the mutable session object directly

### Internal transport and lifecycle objects

#### `PinnedBufferArena`

- Allocates one pinned `byte[]`.
- Splits it into fixed-size `BufferSlice` segments.
- Each session gets two slices:
  - one receive slice
  - one send slice

#### `SaeaIoChannel`

Owns one session’s raw socket I/O:

- one receive `SocketAsyncEventArgs`
- one send `SocketAsyncEventArgs`
- one `SendQueue`
- pending operation counting
- socket attach/start receive/begin shutdown/reset

This type is the transport engine for one session slot.

#### `SendQueue`

Owns outbound queueing for one session.

- copies outbound data into rented buffers
- enforces `MaxQueuedSendBytes`
- keeps the current in-flight buffer separate from queued buffers
- clears queued buffers on shutdown without corrupting in-flight state

#### `SaeaAcceptLoop`

Owns listener startup and accept reposting.

- configures listener socket
- binds and listens with retry
- posts `ConcurrentAccepts` accepts
- drains outstanding accepts on stop

#### `SaeaSession`

Internal mutable connection state.

- identity and remote endpoint
- connected time
- byte counters
- last receive/send ticks
- connected/pooled state machine
- reference to `SaeaIoChannel`

#### `SaeaSessionPool`

Preallocates all session slots and channels.

- stack of available sessions
- generation counter per slot
- recycle only when:
  - session is disconnected
  - channel is inactive
  - pending I/O is zero

#### `SaeaSessionRegistry`

Tracks active handles only.

- register on accept
- unregister on disconnect
- snapshot for stop and timeout scan

No lane assignment is built into this version. The core server should not own application dispatch policy.

#### `IdleConnectionMonitor`

Optional background thread when `SocketTimeoutSeconds > 0`.

- snapshots active sessions
- checks `LastReceiveTicks` and `LastSendTicks`
- triggers a disconnect with `SocketError.TimedOut`

## Receive Path

1. `SaeaAcceptLoop` accepts a socket.
2. `SaeaServer` acquires a pooled session.
3. Accepted socket gets client socket settings.
4. Session generation increments.
5. `SaeaSession.Initialize()` stores endpoint metadata and attaches the channel.
6. `SaeaSessionHandle` is created and registered.
7. `ISaeaConnectionHandler.OnConnected()` is called.
8. `SaeaIoChannel.StartReceiving()` begins the receive chain.
9. On every receive completion:
   - `SaeaIoChannel` reports the transport buffer slice
   - `SaeaServer` copies the bytes into a new `byte[]`
   - `ISaeaConnectionHandler.OnReceived()` gets the copied payload

## Send Path

1. Application calls `session.Send(...)`.
2. `SaeaServer` resolves the session handle.
3. `SaeaIoChannel.EnqueueSend(...)` forwards to `SendQueue`.
4. `SendQueue` rents a buffer, copies the payload, and queues it.
5. `SaeaIoChannel` copies queued chunks into its pinned send buffer before `SendAsync`.
6. On completion:
   - sent byte counters update
   - completed pooled buffers return to `ArrayPool<byte>`
   - the next queued chunk starts immediately

If the send queue exceeds `MaxQueuedSendBytes`, the server disconnects that session.

## Disconnect and Recycle Rules

Disconnect must be idempotent and safe from any thread.

When a disconnect starts:

- the session is marked disconnected once
- the handle is removed from the registry
- the channel closes the socket
- queued outbound buffers are cleared
- `OnDisconnected()` is raised exactly once

Recycling only happens after the channel reports that pending operations reached zero.

## What Is Intentionally Different From `new_architecture.md`

### Not using zero-copy receive

The architecture document keeps a zero-copy receive fast path. This version does not. The API safety win from mandatory copied payloads is more important.

### Not building handler dispatch infrastructure into the core

No `OrderedDispatcher`, no queued event facade, no extra handler layers inside the new server package. Those can exist later as optional adapters. The core should stay focused on transport, lifecycle, and correctness.

### Not using an accept thread

Posted accepts plus reposting is a cleaner SAEA model than a blocking accept management thread.

## What To Extend Next

If this server becomes the primary implementation, the next additions should be:

1. a compatibility adapter from `IConsumer` to `ISaeaConnectionHandler`
2. a metrics/snapshot API for external observability
3. optional dispatcher adapters outside the core server
4. stress and soak tests for queue pressure, rapid connect/disconnect, and restart churn
