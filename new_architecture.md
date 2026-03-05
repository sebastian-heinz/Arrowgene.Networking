# Phase 2: Component Architecture & Composition

A new high-performance TCP socket server built on `SocketAsyncEventArgs`, designed from the ground up with strict responsibility decoupling. Every architectural strength from `AsyncEventServer` is preserved; every structural weakness is resolved.

---

## 1. Architecture Overview

The new design decomposes the monolithic `AsyncEventServer` (1203 lines, 12+ responsibilities) into a **five-layer composition** where each layer owns exactly one concern and communicates with adjacent layers through explicit interfaces.

```
+-------------------------------------------------------------------+
|  Layer 5: Application Integration                                 |
|  IConnectionHandler, IBufferHandler, OrderedDispatcher            |
+-------------------------------------------------------------------+
        |  OnConnected / OnDataReceived / OnDisconnected (callbacks)
        v
+-------------------------------------------------------------------+
|  Layer 4: Server Orchestration                                    |
|  TcpSocketServer (facade), ConnectionSupervisor, ServerConfig     |
+-------------------------------------------------------------------+
        |  Accept / Disconnect / Monitor (coordination)
        v
+-------------------------------------------------------------------+
|  Layer 3: Session Management                                      |
|  Session, SessionHandle, SessionPool, SessionRegistry             |
+-------------------------------------------------------------------+
        |  Initialize / Shutdown / Recycle (lifecycle)
        v
+-------------------------------------------------------------------+
|  Layer 2: Transport (I/O Engine)                                  |
|  TcpListenerEngine, IoChannel, SendQueue                          |
+-------------------------------------------------------------------+
        |  BufferSegment slices / Pool acquire-release
        v
+-------------------------------------------------------------------+
|  Layer 1: Memory Infrastructure                                   |
|  PinnedBufferSlab, BufferSegment, PreallocatedPool<T>             |
+-------------------------------------------------------------------+
```

### Design Principles

| Principle | How It's Applied |
|---|---|
| **Single Responsibility** | Each class has one reason to change. Buffer management doesn't know about sessions; sessions don't know about I/O mechanics. |
| **Composition Over Inheritance** | The `TcpServer` abstract base class is eliminated. `TcpSocketServer` composes its dependencies as constructor parameters. |
| **Zero-Copy by Default** | `IBufferHandler` (buffer, offset, count) is the primary receive path. The copy-then-deliver path is an opt-in adapter, not the default. |
| **Interface-Driven Boundaries** | Every layer boundary is an interface. Components are testable in isolation by mocking adjacent layers. |
| **Immutable Configuration** | `ServerConfig` is validated once at construction and treated as read-only for the server's lifetime. |
| **Fail-Safe Pooling** | Generational handles, pending-operation counting, and state-machine guards are preserved but extracted into dedicated, testable classes. |

---

## 2. Naming Convention Migration

Every legacy component is renamed to better reflect its modern responsibility.

| Legacy Name | New Name | Rationale |
|---|---|---|
| `AsyncEventServer` | `TcpSocketServer` | Public facade; "AsyncEvent" is an implementation detail, not a user concept. |
| `AsyncEventClient` | `Session` | Represents a connection's reusable state; "Client" is misleading for a server-side object. |
| `AsyncEventClientHandle` | `SessionHandle` | Consistent with `Session` rename; still a generational readonly struct. |
| `AsyncEventWriteState` | `SendQueue` | Describes what it does (queues and chunks outbound data), not what it belongs to. |
| `AsyncEventSettings` | `ServerConfig` | "Config" is more conventional than "Settings" for an immutable, validated object. |
| `IConsumer` | `IConnectionHandler` | "Handler" better describes the role; "Consumer" implies pull-based semantics. |
| `IBufferConsumer` | `IBufferHandler` | Consistent with `IConnectionHandler`; this is the zero-copy fast path. |
| `ITcpSocket` | `ISession` | "Session" reflects the connection's role in the server; "TcpSocket" leaks implementation. |
| `TcpServer` (abstract base) | *(eliminated)* | Replaced by composition inside `TcpSocketServer`. |
| `ThreadedBlockingQueueConsumer` | `OrderedDispatcher` | Describes the dispatching behavior, not the threading mechanism. |
| `BlockingQueueConsumer` | `QueuedConnectionHandler` | Clearer: it queues events for external consumption. |
| `EventConsumer` | `EventConnectionHandler` | Bridges to .NET events; naming follows the `Handler` convention. |
| *(inlined in constructor)* | `PinnedBufferSlab` | Extracted: owns the single pinned byte[] allocation. |
| *(inlined in constructor)* | `PreallocatedPool<T>` | Extracted: generic stack-based preallocated pool. |
| *(inlined in methods)* | `TcpListenerEngine` | Extracted: owns the accept loop and listen socket lifecycle. |
| *(inlined in methods)* | `IoChannel` | Extracted: owns the async receive/send loop for one session. |
| *(inlined in methods)* | `SessionPool` | Extracted: manages session object lifecycle (allocate, recycle). |
| *(inlined in methods)* | `SessionRegistry` | Extracted: tracks active sessions and unit-of-order assignment. |
| *(inlined in methods)* | `ConnectionSupervisor` | Extracted: timeout detection and health monitoring. |

---

## 3. Layer 1 — Memory Infrastructure

This layer owns all memory allocation decisions. Nothing above this layer allocates buffers.

---

### 3.1 `BufferSegment` (readonly struct)

A zero-allocation reference to a contiguous region within a shared byte array. This replaces the scattered `(buffer, offset, count)` tuples used throughout the current codebase.

```csharp
namespace Arrowgene.Networking.Memory;

/// <summary>
/// A zero-allocation reference to a contiguous region within a shared buffer.
/// </summary>
public readonly struct BufferSegment : IEquatable<BufferSegment>
{
    /// <summary>The backing byte array (shared, pinned).</summary>
    public readonly byte[] Array;

    /// <summary>Start offset within <see cref="Array"/>.</summary>
    public readonly int Offset;

    /// <summary>Usable length of this segment in bytes.</summary>
    public readonly int Length;

    public BufferSegment(byte[] array, int offset, int length);

    /// <summary>
    /// Returns a <see cref="Span{T}"/> over this segment. Useful for
    /// zero-copy parsing without exposing the raw array.
    /// </summary>
    public Span<byte> AsSpan();

    /// <summary>
    /// Returns a <see cref="ReadOnlySpan{T}"/> over this segment.
    /// </summary>
    public ReadOnlySpan<byte> AsReadOnlySpan();

    public bool Equals(BufferSegment other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public static bool operator ==(BufferSegment left, BufferSegment right);
    public static bool operator !=(BufferSegment left, BufferSegment right);
}
```

**Carried forward from:** The implicit `(readEventArgs.Buffer, readEventArgs.Offset, readEventArgs.BytesTransferred)` tuple in `AsyncEventServer.ProcessReceive` (line 710-711) and the `(writeEventArgs.Buffer, writeEventArgs.Offset, chunkSize)` pattern in `StartSend` (line 788-789).

---

### 3.2 `PinnedBufferSlab` (sealed class)

Extracts requirements **M1-M4** from `architecture.md` into a standalone, testable class. Owns the single pinned `byte[]` allocation and produces `BufferSegment` slices during construction.

```csharp
namespace Arrowgene.Networking.Memory;

/// <summary>
/// Allocates a single contiguous pinned byte array on the POH and
/// partitions it into fixed-size segments at construction time.
/// </summary>
public sealed class PinnedBufferSlab : IDisposable
{
    /// <summary>The raw pinned byte array. Kept alive for the slab's lifetime.</summary>
    public byte[] RawBuffer { get; }

    /// <summary>Total number of segments available.</summary>
    public int SegmentCount { get; }

    /// <summary>Size in bytes of each individual segment.</summary>
    public int SegmentSize { get; }

    /// <summary>
    /// Allocates a pinned byte array of <paramref name="segmentSize"/> * <paramref name="segmentCount"/>
    /// bytes using <c>GC.AllocateArray&lt;byte&gt;(totalSize, pinned: true)</c>.
    /// </summary>
    /// <param name="segmentSize">Byte size of each segment (e.g., buffer size per direction).</param>
    /// <param name="segmentCount">Total number of segments to carve out.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="segmentSize"/> or <paramref name="segmentCount"/> is &lt;= 0.
    /// </exception>
    public PinnedBufferSlab(int segmentSize, int segmentCount);

    /// <summary>
    /// Returns the pre-computed segment at the given index.
    /// </summary>
    /// <param name="index">Zero-based segment index (0 .. SegmentCount-1).</param>
    /// <returns>A <see cref="BufferSegment"/> referencing the correct slice.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="index"/> is out of range.
    /// </exception>
    public BufferSegment GetSegment(int index);

    public void Dispose();
}
```

**Interaction pattern:** `TcpSocketServer` creates one `PinnedBufferSlab` with `segmentCount = maxConnections * 2` (one read + one write segment per session). During session construction, each `Session` receives two `BufferSegment` values — one for its read SAEA, one for its write SAEA.

**Carried forward from:** `AsyncEventServer.cs:72-76` (buffer sizing) and `AsyncEventServer.cs:106-117` (sequential segment assignment).

---

### 3.3 `PreallocatedPool<T>` (sealed class)

Extracts the pool pattern used for both clients (P1-P2) and accept SAEA (P6-P7) into a generic, reusable class.

```csharp
namespace Arrowgene.Networking.Memory;

/// <summary>
/// A thread-safe, fixed-capacity stack-based pool that is fully populated
/// at construction time. Objects are never allocated at runtime.
/// </summary>
/// <typeparam name="T">The pooled item type (must be a reference type).</typeparam>
public sealed class PreallocatedPool<T> where T : class
{
    /// <summary>Number of items currently available in the pool.</summary>
    public int AvailableCount { get; }

    /// <summary>Total capacity (number of items created at construction).</summary>
    public int Capacity { get; }

    /// <summary>
    /// Creates the pool and populates it by calling <paramref name="factory"/>
    /// exactly <paramref name="capacity"/> times.
    /// </summary>
    /// <param name="capacity">Fixed pool size.</param>
    /// <param name="factory">
    /// Factory delegate called with index (0..capacity-1) to create each item.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="capacity"/> is &lt;= 0.
    /// </exception>
    public PreallocatedPool(int capacity, Func<int, T> factory);

    /// <summary>
    /// Attempts to pop an item from the pool.
    /// </summary>
    /// <param name="item">The pooled item, or <c>null</c> if the pool is empty.</param>
    /// <returns><c>true</c> if an item was acquired; <c>false</c> if the pool is exhausted.</returns>
    public bool TryAcquire([NotNullWhen(true)] out T? item);

    /// <summary>
    /// Returns an item to the pool.
    /// </summary>
    /// <param name="item">The item to return.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="item"/> is null.</exception>
    public void Release(T item);

    /// <summary>
    /// Provides read-only access to all items (including those currently in use)
    /// for operations like drain-checking and disposal.
    /// </summary>
    public ReadOnlySpan<T> AllItems { get; }
}
```

**Internal implementation:** A `T[]` array stores all items (for `AllItems` / drain enumeration). A `Stack<T>` stores available items. A single `object _lock` guards the stack. This mirrors the exact pattern from `AsyncEventServer._clientPool` (`Stack<AsyncEventClient>` guarded by `_clientLock`) and `_acceptPool` (`Stack<SocketAsyncEventArgs>` guarded by `_acceptPoolLock`), but generalized.

**Carried forward from:** Requirements P1-P2 (client pool), P6-P7 (accept SAEA pool).

---

## 4. Layer 2 — Transport (I/O Engine)

This layer owns all socket I/O mechanics. It speaks in terms of `BufferSegment`, `Socket`, and `SocketAsyncEventArgs`. It has no concept of sessions, consumers, or application logic.

---

### 4.1 `IAcceptHandler` (interface)

The callback contract that `TcpListenerEngine` uses to deliver accepted sockets to the orchestration layer. This decouples the accept loop from everything that happens after a socket is accepted.

```csharp
namespace Arrowgene.Networking.Transport;

/// <summary>
/// Callback interface for the accept engine to deliver accepted sockets
/// to the orchestration layer without knowing what happens next.
/// </summary>
public interface IAcceptHandler
{
    /// <summary>
    /// Called on an IOCP thread (or the accept loop thread for sync completions)
    /// when a new client socket has been accepted.
    /// </summary>
    /// <param name="acceptedSocket">
    /// The newly accepted <see cref="Socket"/>. The handler takes ownership
    /// and is responsible for closing it on rejection or error.
    /// </param>
    void OnSocketAccepted(Socket acceptedSocket);

    /// <summary>
    /// Called when the accept loop encounters a non-recoverable error
    /// (e.g., listen socket closed unexpectedly).
    /// </summary>
    /// <param name="error">The socket error that caused the failure.</param>
    void OnAcceptError(SocketError error);
}
```

---

### 4.2 `TcpListenerEngine` (sealed class)

Extracted from `AsyncEventServer.StartAccept`, `ProcessAccept`, and `ReturnAcceptEventArgs`. Owns the listen socket, the accept SAEA pool, and the semaphore-gated accept loop. Nothing else.

```csharp
namespace Arrowgene.Networking.Transport;

/// <summary>
/// Manages the TCP listen socket and the SAEA-based accept loop.
/// Delivers accepted sockets to an <see cref="IAcceptHandler"/>.
/// </summary>
/// <remarks>
/// This class encapsulates:
/// - Listen socket creation, bind, and listen (with retry logic).
/// - A pool of accept <see cref="SocketAsyncEventArgs"/> (preallocated).
/// - Semaphore-gated concurrent accept throttling.
/// - Run-generation tracking to discard stale IOCP callbacks across start/stop cycles.
///
/// It does NOT know about sessions, buffers, or application logic.
/// </remarks>
public sealed class TcpListenerEngine : IDisposable
{
    /// <summary>
    /// Creates the listener engine with its accept SAEA pool.
    /// </summary>
    /// <param name="config">Server configuration (bind address, port, backlog, retries).</param>
    /// <param name="acceptHandler">Callback target for accepted sockets.</param>
    /// <param name="concurrentAccepts">
    /// Number of concurrent accept operations (default: 10).
    /// Controls pool size and semaphore capacity.
    /// </param>
    public TcpListenerEngine(
        ServerConfig config,
        IAcceptHandler acceptHandler,
        int concurrentAccepts = 10
    );

    /// <summary>Whether the accept loop is currently running.</summary>
    public bool IsListening { get; }

    /// <summary>Current run generation (incremented on each Start/Stop cycle).</summary>
    public int RunGeneration { get; }

    /// <summary>
    /// Binds the listen socket and starts the accept loop on a background thread.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the accept loop.</param>
    /// <returns>
    /// <c>true</c> if the socket was bound and listening successfully;
    /// <c>false</c> if all retries were exhausted.
    /// </returns>
    public bool Start(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the accept loop, closes the listen socket, increments the run
    /// generation, and waits for all outstanding accept SAEA to return to the pool.
    /// </summary>
    /// <param name="drainTimeoutMs">
    /// Maximum time to wait for pending accept operations to complete.
    /// </param>
    public void Stop(int drainTimeoutMs = 10000);

    /// <summary>
    /// Waits until all accept SAEA objects have been returned to the pool.
    /// </summary>
    /// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
    /// <returns><c>true</c> if the pool is fully drained; <c>false</c> on timeout.</returns>
    public bool WaitForDrain(int timeoutMs);

    public void Dispose();
}
```

**Internal mechanics (preserved from legacy):**

1. `Start()` creates the listen socket, applies `SocketSettings.ConfigureSocket()`, binds with retry logic (from `AsyncEventServer.Startup`, lines 312-349).
2. The accept loop runs on a background thread. Each iteration: `_semaphore.Wait(ct)` -> pop SAEA from pool -> `_listenSocket.AcceptAsync(saea)` -> on completion: extract `AcceptSocket`, return SAEA to pool (clearing `AcceptSocket = null`), call `_acceptHandler.OnSocketAccepted(socket)`.
3. Run-generation tracking via `int _runGeneration` on `UserToken` (from requirements P9-P10) rejects stale callbacks.
4. `Stop()` increments `_runGeneration`, closes the listen socket, joins the background thread, calls `WaitForDrain()`.

**Carried forward from:** `AsyncEventServer.cs:351-428` (StartAccept), `AsyncEventServer.cs:436-596` (ProcessAccept — only the validation and socket extraction, not the session assignment), `AsyncEventServer.cs:985-1007` (ReturnAcceptEventArgs).

---

### 4.3 `IIoChannelOwner` (interface)

The callback contract used by `IoChannel` to deliver I/O completion events upward. This decouples raw I/O from session management.

```csharp
namespace Arrowgene.Networking.Transport;

/// <summary>
/// Callback interface for <see cref="IoChannel"/> to notify its owner of
/// I/O events without knowing the session or application layer.
/// </summary>
public interface IIoChannelOwner
{
    /// <summary>
    /// Called when data has been received into the read buffer segment.
    /// </summary>
    /// <param name="channelId">The channel's unique identifier (maps to session).</param>
    /// <param name="data">
    /// A <see cref="BufferSegment"/> referencing the received bytes within the
    /// pinned buffer. Valid only for the duration of this call.
    /// </param>
    void OnDataReceived(int channelId, BufferSegment data);

    /// <summary>
    /// Called when an I/O error or graceful close is detected, meaning
    /// the channel should be shut down.
    /// </summary>
    /// <param name="channelId">The channel's unique identifier.</param>
    /// <param name="error">The socket error, or <see cref="SocketError.Success"/> for graceful close.</param>
    void OnChannelError(int channelId, SocketError error);

    /// <summary>
    /// Called when a send operation completes successfully.
    /// </summary>
    /// <param name="channelId">The channel's unique identifier.</param>
    /// <param name="bytesTransferred">Number of bytes written to the socket.</param>
    void OnSendCompleted(int channelId, int bytesTransferred);
}
```

---

### 4.4 `IoChannel` (sealed class)

Extracted from the receive loop (`StartReceive`/`ProcessReceive`/`Receive_Completed`) and the send loop (`StartSend`/`ProcessSend`/`Send_Completed`) of `AsyncEventServer`. Each `IoChannel` owns one read SAEA and one write SAEA. It manages the async I/O mechanics for exactly one socket.

```csharp
namespace Arrowgene.Networking.Transport;

/// <summary>
/// Manages the SAEA-based async receive and send loops for a single TCP socket.
/// </summary>
/// <remarks>
/// Each IoChannel owns:
/// - One read <see cref="SocketAsyncEventArgs"/> with a dedicated <see cref="BufferSegment"/>.
/// - One write <see cref="SocketAsyncEventArgs"/> with a dedicated <see cref="BufferSegment"/>.
/// - A <see cref="SendQueue"/> for outbound data queuing and chunking.
/// - A pending-operations counter to prevent premature recycling.
///
/// IoChannel has no concept of sessions, handles, or application logic.
/// It notifies its owner via <see cref="IIoChannelOwner"/> callbacks.
/// </remarks>
public sealed class IoChannel : IDisposable
{
    /// <summary>Stable identifier for this channel (matches the session/pool index).</summary>
    public int ChannelId { get; }

    /// <summary>Number of IOCP operations currently in flight.</summary>
    public int PendingOperations { get; }

    /// <summary>Whether this channel has an active socket assigned.</summary>
    public bool IsActive { get; }

    /// <summary>The read buffer segment owned by this channel.</summary>
    public BufferSegment ReadSegment { get; }

    /// <summary>The write buffer segment owned by this channel.</summary>
    public BufferSegment WriteSegment { get; }

    /// <summary>Access to the send queue for direct enqueue operations.</summary>
    public SendQueue SendQueue { get; }

    /// <summary>
    /// Creates the channel with its SAEA instances and buffer segments.
    /// </summary>
    /// <param name="channelId">Stable identifier (pool index).</param>
    /// <param name="readSegment">Pinned buffer region for receives.</param>
    /// <param name="writeSegment">Pinned buffer region for sends.</param>
    /// <param name="owner">Callback target for I/O events.</param>
    /// <param name="maxSendQueueBytes">
    /// Maximum bytes in the outbound queue before back-pressure triggers (default: 16 MB).
    /// </param>
    public IoChannel(
        int channelId,
        BufferSegment readSegment,
        BufferSegment writeSegment,
        IIoChannelOwner owner,
        int maxSendQueueBytes = 16 * 1024 * 1024
    );

    /// <summary>
    /// Binds this channel to a live socket and begins the receive loop.
    /// Called once per connection lifecycle (after accept, before any I/O).
    /// </summary>
    /// <param name="socket">The accepted TCP socket. Channel takes ownership of I/O on it.</param>
    /// <exception cref="InvalidOperationException">Thrown if already active.</exception>
    public void Activate(Socket socket);

    /// <summary>
    /// Enqueues data for sending. If the send chain is idle, starts it.
    /// </summary>
    /// <param name="data">The payload to send.</param>
    /// <returns>
    /// An <see cref="EnqueueResult"/> indicating success, send-chain started,
    /// or queue overflow.
    /// </returns>
    public EnqueueResult EnqueueSend(byte[] data);

    /// <summary>
    /// Initiates a graceful shutdown: prevents new I/O, clears queued sends.
    /// Only the first call takes effect; subsequent calls are no-ops.
    /// </summary>
    /// <returns><c>true</c> if this call initiated the shutdown; <c>false</c> if already shut down.</returns>
    public bool BeginShutdown();

    /// <summary>
    /// Returns <c>true</c> if the channel is inactive (shut down) and has
    /// no pending IOCP operations, meaning it is safe to recycle.
    /// </summary>
    public bool IsReadyForRecycle { get; }

    /// <summary>
    /// Resets all internal state (send queue, pending ops, active flag) for reuse from the pool.
    /// Must only be called when <see cref="IsReadyForRecycle"/> is <c>true</c>.
    /// </summary>
    public void Reset();

    public void Dispose();
}

/// <summary>
/// Result of an <see cref="IoChannel.EnqueueSend"/> operation.
/// </summary>
public enum EnqueueResult
{
    /// <summary>Data was queued; a send was already in progress.</summary>
    Queued,

    /// <summary>Data was queued and the send chain was started.</summary>
    SendStarted,

    /// <summary>The write queue capacity was exceeded.</summary>
    QueueOverflow
}
```

**Internal mechanics (preserved from legacy):**

- **Receive loop:** `Activate()` calls `StartReceive()` which increments pending ops, calls `socket.ReceiveAsync(readSaea)`. On completion (sync path: `willRaiseEvent == false` -> process inline; async path: `Completed` event), validates `SocketError` and `BytesTransferred > 0`, then calls `_owner.OnDataReceived(ChannelId, receivedSegment)`, then loops back to `StartReceive()`. Always `DecrementPendingOperations()` in `finally`.
- **Send loop:** `EnqueueSend()` delegates to `SendQueue.Enqueue()`. If a new chain starts, calls `StartSend()` which calls `SendQueue.TryGetChunk()`, copies data via `Buffer.BlockCopy` into the write SAEA buffer, increments pending ops, calls `socket.SendAsync(writeSaea)`. On completion, calls `SendQueue.CompleteChunk()`, loops if more data. Always `DecrementPendingOperations()` in `finally`.
- **Pending operations:** `Interlocked.Increment` before every `ReceiveAsync`/`SendAsync`, `Interlocked.Decrement` in the `finally` block (requirement C7, from `AsyncEventServer.cs:613-646, 791-823`).
- **Synchronous completion fallback:** When `ReceiveAsync`/`SendAsync` returns `false`, process inline on the calling thread (requirement C2, from `AsyncEventServer.cs:634-645, 812-823`).

**Carried forward from:** `AsyncEventServer.cs:598-724` (receive path), `AsyncEventServer.cs:727-888` (send path), `AsyncEventClient.cs:150-158` (pending ops counting).

---

### 4.5 `SendQueue` (sealed class)

Renamed from `AsyncEventWriteState`. Extracted without behavioral changes — the existing design is correct and well-tested.

```csharp
namespace Arrowgene.Networking.Transport;

/// <summary>
/// Manages the outbound data queue for a single connection, chunking large
/// payloads to fit the SAEA buffer size and enforcing back-pressure via
/// a configurable maximum queue depth.
/// </summary>
/// <remarks>
/// Thread-safe: all methods acquire <c>_sendLock</c>.
/// Lifecycle: Owned by <see cref="IoChannel"/>. Reset on pool recycle.
/// </remarks>
public sealed class SendQueue
{
    /// <summary>
    /// Maximum bytes that may be queued across all pending messages.
    /// Exceeding this triggers a queue overflow (back-pressure signal).
    /// </summary>
    public int MaxQueuedBytes { get; }

    /// <summary>Total bytes currently queued (including the in-flight message).</summary>
    public int QueuedBytes { get; }

    /// <summary>Whether a send chain is currently in progress.</summary>
    public bool IsSendInProgress { get; }

    /// <summary>
    /// Creates a new <see cref="SendQueue"/> with the specified capacity.
    /// </summary>
    /// <param name="maxQueuedBytes">
    /// Maximum total bytes allowed in the queue (default: 16 MB).
    /// </param>
    public SendQueue(int maxQueuedBytes = 16 * 1024 * 1024);

    /// <summary>
    /// Enqueues a message for sending.
    /// </summary>
    /// <param name="data">The payload bytes. Must not be empty.</param>
    /// <param name="overflow">
    /// Set to <c>true</c> if the enqueue was rejected due to exceeding
    /// <see cref="MaxQueuedBytes"/>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the caller should start the send chain (the queue was idle);
    /// <c>false</c> if a send is already in progress or the enqueue was rejected.
    /// </returns>
    public bool Enqueue(byte[] data, out bool overflow);

    /// <summary>
    /// Gets the next chunk to copy into the SAEA write buffer.
    /// </summary>
    /// <param name="maxChunkSize">Maximum bytes to return (the SAEA buffer size).</param>
    /// <param name="data">The source byte array.</param>
    /// <param name="offset">Offset within <paramref name="data"/>.</param>
    /// <param name="count">Number of bytes to copy.</param>
    /// <returns><c>true</c> if a chunk is available; <c>false</c> if the queue is empty.</returns>
    public bool TryGetChunk(int maxChunkSize, out byte[] data, out int offset, out int count);

    /// <summary>
    /// Records completion of a send operation and advances the cursor.
    /// </summary>
    /// <param name="bytesTransferred">Bytes successfully sent by the socket.</param>
    /// <returns><c>true</c> if more data remains to send; <c>false</c> if the chain is complete.</returns>
    public bool CompleteChunk(int bytesTransferred);

    /// <summary>
    /// Clears all queued messages, preserving in-flight chunk state so
    /// outstanding IOCP callbacks remain consistent.
    /// </summary>
    public void ClearQueued();

    /// <summary>
    /// Full reset for pool recycling. Clears all state.
    /// </summary>
    public void Reset();
}
```

**Carried forward from:** `AsyncEventWriteState` (all behavior preserved verbatim). Requirements B3-B4 (back-pressure), Z4-Z6 (send-path zero-copy and chunking), B7 (in-flight preservation during disconnect).

---

## 5. Layer 3 — Session Management

This layer owns the lifecycle of reusable connection objects. It provides generational handles for safe external access and manages pool acquire/release semantics.

---

### 5.1 `ISession` (interface)

The public contract for a connected TCP endpoint. Replaces `ITcpSocket` with a more descriptive name and adds `Span`-based send support.

```csharp
namespace Arrowgene.Networking.Session;

/// <summary>
/// Represents a connected TCP endpoint as seen by application code.
/// All property accesses are generation-checked when accessed through
/// a <see cref="SessionHandle"/>.
/// </summary>
public interface ISession
{
    /// <summary>Human-readable identifier, typically "[IP:Port]".</summary>
    string Identity { get; }

    /// <summary>Remote endpoint IP address.</summary>
    IPAddress RemoteIpAddress { get; }

    /// <summary>Remote endpoint port number.</summary>
    ushort Port { get; }

    /// <summary>
    /// The ordering lane assigned to this session for parallel processing.
    /// All events for sessions on the same lane are processed sequentially.
    /// </summary>
    int UnitOfOrder { get; }

    /// <summary>Millisecond tick count of the last successful receive.</summary>
    long LastReadTicks { get; }

    /// <summary>Millisecond tick count of the last successful send.</summary>
    long LastWriteTicks { get; }

    /// <summary>Timestamp when the connection was established.</summary>
    DateTime ConnectedAt { get; }

    /// <summary>Total bytes received on this session.</summary>
    ulong BytesReceived { get; }

    /// <summary>Total bytes sent on this session.</summary>
    ulong BytesSent { get; }

    /// <summary>Whether this session can currently send and receive data.</summary>
    bool IsAlive { get; }

    /// <summary>Enqueues data for sending to the remote endpoint.</summary>
    /// <param name="data">The payload bytes.</param>
    void Send(byte[] data);

    /// <summary>Enqueues data for sending from a read-only span.</summary>
    /// <param name="data">The payload span (copied into the send queue).</param>
    void Send(ReadOnlySpan<byte> data);

    /// <summary>Initiates a graceful disconnect.</summary>
    void Close();
}
```

**Key changes from `ITcpSocket`:** Added `Send(ReadOnlySpan<byte>)` overload for stack-allocated or sliced data without forcing a `byte[]` allocation. Fixed `BytesSend` -> `BytesSent` (grammar).

---

### 5.2 `Session` (sealed class)

Renamed from `AsyncEventClient`. A preallocated, reusable server-side connection object with a four-state lifecycle.

```csharp
namespace Arrowgene.Networking.Session;

/// <summary>
/// A preallocated, reusable server-side connection object.
/// Managed by <see cref="SessionPool"/>; never created directly by application code.
/// </summary>
/// <remarks>
/// Lifecycle states (guarded by internal lock):
/// <code>
///   [Pooled] --Initialize()--> [Active] --BeginShutdown()--> [Draining] --MarkPooled()--> [Pooled]
/// </code>
/// </remarks>
public sealed class Session : ISession
{
    // ── Identity ──

    /// <summary>Stable index within the preallocated array (0..maxConnections-1).</summary>
    public int SessionId { get; }

    /// <inheritdoc/>
    public string Identity { get; }

    // ── Generation (ABA prevention) ──

    /// <summary>
    /// Incremented each time this session object is reused for a new connection.
    /// Compared by <see cref="SessionHandle"/> to detect stale references.
    /// </summary>
    public int Generation { get; }

    /// <summary>The server's run generation at the time this session was initialized.</summary>
    public int RunGeneration { get; }

    // ── Connection metadata (set during Initialize, read-only until recycle) ──

    /// <inheritdoc/>
    public IPAddress RemoteIpAddress { get; }

    /// <inheritdoc/>
    public ushort Port { get; }

    /// <inheritdoc/>
    public int UnitOfOrder { get; }

    /// <inheritdoc/>
    public long LastReadTicks { get; }   // Volatile.Read internally

    /// <inheritdoc/>
    public long LastWriteTicks { get; }  // Volatile.Read internally

    /// <inheritdoc/>
    public DateTime ConnectedAt { get; }

    /// <inheritdoc/>
    public ulong BytesReceived { get; }  // Interlocked.Read internally

    /// <inheritdoc/>
    public ulong BytesSent { get; }      // Interlocked.Read internally

    /// <inheritdoc/>
    public bool IsAlive { get; }         // lock-guarded

    // ── Internal (visible to SessionPool and TcpSocketServer) ──

    /// <summary>The I/O channel bound to this session.</summary>
    internal IoChannel Channel { get; }

    /// <summary>
    /// Creates the session with its associated I/O channel.
    /// Called once during pool preallocation.
    /// </summary>
    internal Session(int sessionId, IoChannel channel, TcpSocketServer server);

    /// <summary>
    /// Transitions from [Pooled] to [Active]. Binds the accepted socket,
    /// sets connection metadata, resets counters, and activates the I/O channel.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not in [Pooled] state.</exception>
    internal void Initialize(
        Socket socket,
        int unitOfOrder,
        int runGeneration,
        int sessionGeneration,
        SessionHandle handle
    );

    /// <summary>
    /// Atomic transition from [Active] to [Draining].
    /// Shuts down the socket; only the first call returns <c>true</c>.
    /// </summary>
    /// <returns><c>true</c> if this call initiated the shutdown.</returns>
    internal bool TryBeginShutdown();

    /// <summary>
    /// Atomic transition from [Draining] to [Pooled].
    /// Only succeeds if not alive and not already pooled.
    /// </summary>
    /// <returns><c>true</c> if successfully marked as pooled.</returns>
    internal bool TryMarkPooled();

    /// <summary>Records a receive: updates LastReadTicks and BytesReceived.</summary>
    internal void RecordReceive(int byteCount);

    /// <summary>Records a send: updates LastWriteTicks and BytesSent.</summary>
    internal void RecordSend(int byteCount);

    // ── ISession implementation ──

    /// <inheritdoc/>
    public void Send(byte[] data);

    /// <inheritdoc/>
    public void Send(ReadOnlySpan<byte> data);

    /// <inheritdoc/>
    public void Close();
}
```

**Carried forward from:** `AsyncEventClient` — state machine (P3), generation tracking (P9), atomic `Volatile.Write`/`Interlocked.Add` for timestamps and counters (C5-C6).

**Key structural change:** The `Session` no longer holds `SocketAsyncEventArgs` directly. Those belong to `IoChannel`, which the session accesses through its `Channel` property. This separates "what is a connection" from "how does I/O work." The `Session.Send()` method delegates to `_server.Send()` via a stored reference, keeping the public API clean while routing internally.

---

### 5.3 `SessionHandle` (readonly struct)

Renamed from `AsyncEventClientHandle`. Same generational-handle pattern, now referencing `Session` and implementing `ISession`.

```csharp
namespace Arrowgene.Networking.Session;

/// <summary>
/// A lightweight, generation-checked handle to a pooled <see cref="Session"/>.
/// This is the type that application code (handlers/dispatchers) receives.
/// Prevents use-after-recycle bugs: if the underlying session has been
/// recycled and assigned to a new connection, all property accesses throw
/// <see cref="ObjectDisposedException"/>.
/// </summary>
public readonly struct SessionHandle : ISession, IEquatable<SessionHandle>
{
    private readonly Session _session;

    /// <summary>The generation captured at handle creation time.</summary>
    public readonly int Generation;

    /// <summary>
    /// Creates a handle bound to the given session and generation.
    /// </summary>
    public SessionHandle(Session session, int generation);

    // ── Validation ──

    /// <summary>
    /// Non-throwing validation: returns <c>true</c> and the underlying session
    /// if the generations still match.
    /// </summary>
    /// <param name="session">The underlying session, or <c>null</c> if stale.</param>
    /// <returns><c>true</c> if the handle is still valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSession([NotNullWhen(true)] out Session? session);

    // ── ISession delegations (all generation-checked, throw on stale) ──

    public string Identity { get; }
    public IPAddress RemoteIpAddress { get; }
    public ushort Port { get; }
    public int UnitOfOrder { get; }
    public long LastReadTicks { get; }
    public long LastWriteTicks { get; }
    public DateTime ConnectedAt { get; }
    public ulong BytesReceived { get; }
    public ulong BytesSent { get; }
    public bool IsAlive { get; }
    public void Send(byte[] data);
    public void Send(ReadOnlySpan<byte> data);
    public void Close();

    // ── Equality (reference + generation) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(SessionHandle other);
    public override bool Equals(object? obj);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode();
    public static bool operator ==(SessionHandle left, SessionHandle right);
    public static bool operator !=(SessionHandle left, SessionHandle right);
}
```

**Carried forward from:** `AsyncEventClientHandle` — entire pattern preserved verbatim. Requirements P9-P11.

---

### 5.4 `SessionPool` (sealed class)

Extracted from the scattered pool logic in `AsyncEventServer` (constructor, `ProcessAccept`, `TryRecycleClient`). Encapsulates the full session acquire-initialize-recycle flow.

```csharp
namespace Arrowgene.Networking.Session;

/// <summary>
/// Manages the lifecycle of preallocated <see cref="Session"/> objects.
/// Handles acquire, initialize, recycle, generation tracking, and drain-waiting.
/// </summary>
public sealed class SessionPool
{
    /// <summary>Number of sessions currently available in the pool.</summary>
    public int AvailableCount { get; }

    /// <summary>Maximum number of sessions (pool capacity).</summary>
    public int Capacity { get; }

    /// <summary>
    /// Creates and preallocates all session objects.
    /// </summary>
    /// <param name="capacity">Maximum number of concurrent connections.</param>
    /// <param name="channelFactory">
    /// Factory that creates an <see cref="IoChannel"/> for each session index.
    /// </param>
    /// <param name="server">
    /// Back-reference to the server for Send delegation.
    /// </param>
    public SessionPool(int capacity, Func<int, IoChannel> channelFactory, TcpSocketServer server);

    /// <summary>
    /// Attempts to acquire a session from the pool.
    /// </summary>
    /// <param name="session">The acquired session, or <c>null</c> if the pool is exhausted.</param>
    /// <returns><c>true</c> if a session was acquired.</returns>
    public bool TryAcquire([NotNullWhen(true)] out Session? session);

    /// <summary>
    /// Attempts to recycle a session back to the pool. Only succeeds if:
    /// 1. The session is not alive (shutdown completed).
    /// 2. The session's I/O channel has no pending IOCP operations.
    /// 3. The session has not already been pooled.
    /// Resets the I/O channel on successful recycle.
    /// </summary>
    /// <param name="session">The session to recycle.</param>
    /// <returns><c>true</c> if the session was returned to the pool.</returns>
    public bool TryRecycle(Session session);

    /// <summary>
    /// Increments the generation counter for a session, invalidating any
    /// existing <see cref="SessionHandle"/> for that session.
    /// </summary>
    /// <param name="sessionId">The session's stable identifier.</param>
    /// <returns>The new generation value.</returns>
    public int IncrementGeneration(int sessionId);

    /// <summary>
    /// Increments all generation counters (used on server start to invalidate
    /// stale handles from a previous run).
    /// </summary>
    public void IncrementAllGenerations();

    /// <summary>
    /// Waits for all sessions to be recycled and all pending operations to drain.
    /// </summary>
    /// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
    /// <returns><c>true</c> if fully drained; <c>false</c> on timeout.</returns>
    public bool WaitForDrain(int timeoutMs);

    /// <summary>
    /// Read-only access to all session objects for enumeration and disposal.
    /// </summary>
    public ReadOnlySpan<Session> AllSessions { get; }
}
```

**Carried forward from:** `AsyncEventServer._clientPool`, `_clientGenerations[]`, `_allClients[]`, `TryRecycleClient` (lines 960-983), `WaitForClientOperationsDrain` and `WaitForClientPoolDrain` (lines 1128-1175).

---

### 5.5 `SessionRegistry` (sealed class)

Extracted from the `_clientHandles` list and `_unitOfOrders` counter array in `AsyncEventServer`. Tracks which sessions are currently active and manages unit-of-order assignment.

```csharp
namespace Arrowgene.Networking.Session;

/// <summary>
/// Tracks active sessions and manages unit-of-order lane assignment.
/// Thread-safe: all methods are synchronized.
/// </summary>
public sealed class SessionRegistry
{
    /// <summary>Number of currently active sessions.</summary>
    public int ActiveCount { get; }

    /// <summary>
    /// Creates the registry with the specified number of ordering lanes.
    /// </summary>
    /// <param name="maxUnitOfOrder">
    /// Number of parallel ordering lanes (from <see cref="ServerConfig.MaxUnitOfOrder"/>).
    /// </param>
    public SessionRegistry(int maxUnitOfOrder);

    /// <summary>
    /// Registers a session as active and assigns it to the least-loaded
    /// unit-of-order lane (linear scan of lane counts, pick minimum).
    /// </summary>
    /// <param name="handle">The session handle to register.</param>
    /// <returns>The assigned unit-of-order value.</returns>
    public int Register(SessionHandle handle);

    /// <summary>
    /// Removes a session from the active set and decrements its
    /// unit-of-order lane counter.
    /// </summary>
    /// <param name="handle">The session handle to remove.</param>
    /// <param name="unitOfOrder">The unit-of-order lane to decrement.</param>
    /// <returns><c>true</c> if the handle was found and removed.</returns>
    public bool Unregister(SessionHandle handle, int unitOfOrder);

    /// <summary>
    /// Returns a snapshot of all currently active session handles.
    /// The snapshot is a copy; safe to iterate without holding the lock.
    /// </summary>
    /// <param name="buffer">
    /// A caller-provided list to fill (avoids allocation on each call).
    /// The list is cleared before filling.
    /// </param>
    public void SnapshotActiveSessions(List<SessionHandle> buffer);

    /// <summary>
    /// Resets all unit-of-order counters to zero (used on server start).
    /// </summary>
    public void ResetCounters();
}
```

**Carried forward from:** `AsyncEventServer._clientHandles` (List), `_unitOfOrders[]` (least-loaded assignment at lines 534-546), lock scope under `_clientLock`.

---

## 6. Layer 4 — Server Orchestration

This layer composes all lower layers and implements the full server lifecycle. It is the only layer that knows about all other layers.

---

### 6.1 `ServerConfig` (sealed class)

Renamed from `AsyncEventSettings`. Made immutable (all properties are `init`-only or set in constructor). Validation happens once at construction.

```csharp
namespace Arrowgene.Networking.Configuration;

/// <summary>
/// Immutable, validated configuration for <see cref="TcpSocketServer"/>.
/// </summary>
[DataContract]
public sealed class ServerConfig : ICloneable
{
    /// <summary>Server name used as a log prefix. Default: "".</summary>
    [DataMember] public string Identity { get; init; }

    /// <summary>Maximum number of concurrent client connections (determines pool sizes). Default: 100.</summary>
    [DataMember] public int MaxConnections { get; init; }

    /// <summary>Buffer size in bytes per direction per connection (read + write). Default: 2000.</summary>
    [DataMember] public int BufferSize { get; init; }

    /// <summary>Number of bind retries on startup. Default: 10.</summary>
    [DataMember] public int BindRetries { get; init; }

    /// <summary>Number of parallel ordering lanes for consumer dispatch. Default: 1.</summary>
    [DataMember] public int MaxUnitOfOrder { get; init; }

    /// <summary>
    /// Idle socket timeout in seconds. Connections with no activity for this
    /// duration are automatically disconnected. Use -1 to disable. Default: -1.
    /// </summary>
    [DataMember] public int SocketTimeoutSeconds { get; init; }

    /// <summary>
    /// Maximum bytes allowed in a single session's outbound queue before
    /// the connection is forcibly closed. Default: 16 MB.
    /// </summary>
    [DataMember] public int MaxSendQueueBytes { get; init; }

    /// <summary>
    /// Number of concurrent accept operations. Controls the accept SAEA
    /// pool size and semaphore capacity. Default: 10.
    /// </summary>
    [DataMember] public int ConcurrentAccepts { get; init; }

    /// <summary>Low-level socket configuration for the listener socket.</summary>
    [DataMember] public SocketSettings ListenerSocketSettings { get; init; }

    /// <summary>Low-level socket configuration for accepted client sockets.</summary>
    [DataMember] public SocketSettings ClientSocketSettings { get; init; }

    /// <summary>Enables verbose diagnostic logging. Default: false.</summary>
    [DataMember] public bool DebugMode { get; init; }

    /// <summary>
    /// Default configuration.
    /// </summary>
    public ServerConfig();

    /// <summary>Deep-copy constructor.</summary>
    public ServerConfig(ServerConfig other);

    /// <summary>
    /// Validates all settings and throws <see cref="ArgumentException"/>
    /// if any value is out of range.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown on invalid configuration.</exception>
    public void Validate();

    public object Clone();
}
```

**Key changes from `AsyncEventSettings`:**
- `MaxSendQueueBytes` is now configurable (was hardcoded as `MaxQueuedBytes = 16 MB` in `AsyncEventWriteState`).
- `ConcurrentAccepts` is now configurable (was hardcoded as `NumAccepts = 10` in `AsyncEventServer`).
- `Retries` renamed to `BindRetries` for clarity.
- Added separate `ListenerSocketSettings` and `ClientSocketSettings` (resolves the `TODO` at `AsyncEventServer.cs:580-581`).
- All properties use `init` accessors for immutability after construction.

---

### 6.2 `ConnectionSupervisor` (sealed class)

Extracted from `AsyncEventServer.CheckSocketTimeout` (lines 1009-1057). Runs a dedicated monitoring thread that detects idle sessions.

```csharp
namespace Arrowgene.Networking.Server;

/// <summary>
/// Monitors active sessions for idle timeouts and notifies the server
/// when a session should be disconnected.
/// </summary>
public sealed class ConnectionSupervisor : IDisposable
{
    /// <summary>Whether the supervisor is currently running.</summary>
    public bool IsRunning { get; }

    /// <summary>
    /// Creates the supervisor.
    /// </summary>
    /// <param name="timeout">Idle timeout duration. Sessions inactive beyond this are reported.</param>
    /// <param name="registry">The session registry to poll for active sessions.</param>
    /// <param name="onIdleSession">
    /// Callback invoked for each session that exceeds the idle timeout.
    /// Called on the supervisor's background thread.
    /// </param>
    public ConnectionSupervisor(
        TimeSpan timeout,
        SessionRegistry registry,
        Action<SessionHandle> onIdleSession
    );

    /// <summary>
    /// Starts the monitoring thread.
    /// </summary>
    /// <param name="cancellationToken">Token to stop monitoring.</param>
    public void Start(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the monitoring thread and waits for it to exit.
    /// </summary>
    /// <param name="joinTimeoutMs">Maximum time to wait for the thread to exit.</param>
    public void Stop(int joinTimeoutMs = 10000);

    public void Dispose();
}
```

**Internal mechanics:** Polls at `Math.Clamp(timeout.TotalMilliseconds, 1000, 30000)` ms intervals, snapshots active sessions via `SessionRegistry.SnapshotActiveSessions()`, compares `max(LastReadTicks, LastWriteTicks)` against `Environment.TickCount64`. Calls `onIdleSession` for each timed-out session.

**Carried forward from:** `AsyncEventServer.cs:1009-1057` (requirement B5).

---

### 6.3 `TcpSocketServer` (sealed class)

The main public facade. Renamed from `AsyncEventServer`. Composed of all lower-layer components instead of inheriting from `TcpServer`.

```csharp
namespace Arrowgene.Networking.Server;

/// <summary>
/// High-performance TCP socket server built on <see cref="SocketAsyncEventArgs"/>.
/// Composes pinned memory, object pooling, an accept engine, session management,
/// and timeout supervision into a single coherent server lifecycle.
/// </summary>
/// <remarks>
/// This class replaces the legacy <c>AsyncEventServer</c> / <c>TcpServer</c>
/// inheritance chain with a composition-based design. Each responsibility is
/// delegated to a dedicated component:
///
/// <list type="bullet">
///   <item><see cref="PinnedBufferSlab"/> — pinned memory allocation</item>
///   <item><see cref="SessionPool"/> — session object lifecycle</item>
///   <item><see cref="SessionRegistry"/> — active session tracking</item>
///   <item><see cref="TcpListenerEngine"/> — accept loop</item>
///   <item><see cref="ConnectionSupervisor"/> — idle timeout monitoring</item>
/// </list>
/// </remarks>
public sealed class TcpSocketServer : IAcceptHandler, IIoChannelOwner, IDisposable
{
    // ── Composed subsystems (created in constructor) ──
    // PinnedBufferSlab        _bufferSlab
    // SessionPool             _sessionPool
    // SessionRegistry         _registry
    // TcpListenerEngine       _listenerEngine
    // ConnectionSupervisor?   _supervisor        (null if timeout disabled)
    // IConnectionHandler      _handler
    // ServerConfig            _config            (defensive copy)

    // ── Lifecycle state ──
    // CancellationTokenSource _cancellation
    // volatile bool           _isRunning
    // volatile bool           _isDisposed
    // object                  _lifecycleLock

    // ── Construction ──

    /// <summary>
    /// Creates the server with all resources preallocated.
    /// </summary>
    /// <param name="ipAddress">IP address to bind the listening socket to.</param>
    /// <param name="port">TCP port to listen on.</param>
    /// <param name="handler">Application-level connection handler.</param>
    /// <param name="config">Server configuration (defensively copied).</param>
    /// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown if config validation fails.</exception>
    public TcpSocketServer(
        IPAddress ipAddress,
        ushort port,
        IConnectionHandler handler,
        ServerConfig config
    );

    /// <summary>Convenience overload using default configuration.</summary>
    public TcpSocketServer(
        IPAddress ipAddress,
        ushort port,
        IConnectionHandler handler
    );

    // ── Public API ──

    /// <summary>The IP address the server is bound to.</summary>
    public IPAddress IpAddress { get; }

    /// <summary>The TCP port the server is listening on.</summary>
    public ushort Port { get; }

    /// <summary>Whether the server is currently running.</summary>
    public bool IsRunning { get; }

    /// <summary>Number of currently active sessions.</summary>
    public int ActiveSessionCount { get; }

    /// <summary>
    /// Starts the server: notifies the handler, binds the listen socket,
    /// starts the accept loop, and starts timeout monitoring (if configured).
    /// </summary>
    public void Start();

    /// <summary>
    /// Stops the server: disconnects all active sessions, drains pending I/O,
    /// stops the accept loop and timeout monitor, notifies the handler.
    /// </summary>
    public void Stop();

    /// <summary>
    /// Sends data to a session identified by its handle.
    /// </summary>
    /// <param name="handle">The target session handle.</param>
    /// <param name="data">The payload bytes.</param>
    public void Send(SessionHandle handle, byte[] data);

    /// <summary>
    /// Sends data to a session identified by its handle (span overload).
    /// </summary>
    /// <param name="handle">The target session handle.</param>
    /// <param name="data">The payload span (copied into the send queue).</param>
    public void Send(SessionHandle handle, ReadOnlySpan<byte> data);

    /// <summary>
    /// Initiates disconnection of a session.
    /// </summary>
    /// <param name="handle">The session to disconnect.</param>
    public void Disconnect(SessionHandle handle);

    // ── IAcceptHandler (called by TcpListenerEngine on IOCP/accept thread) ──

    void IAcceptHandler.OnSocketAccepted(Socket acceptedSocket);
    void IAcceptHandler.OnAcceptError(SocketError error);

    // ── IIoChannelOwner (called by IoChannel on IOCP threads) ──

    void IIoChannelOwner.OnDataReceived(int channelId, BufferSegment data);
    void IIoChannelOwner.OnChannelError(int channelId, SocketError error);
    void IIoChannelOwner.OnSendCompleted(int channelId, int bytesTransferred);

    // ── IDisposable ──

    public void Dispose();
}
```

---

## 7. Layer 5 — Application Integration

This layer defines the contracts that application code implements.

---

### 7.1 `IConnectionHandler` (interface)

Renamed from `IConsumer`. The handler receives `ISession` (via `SessionHandle`) instead of the ambiguous `ITcpSocket`.

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>
/// Primary application-level handler for TCP connection events.
/// Implement this interface to receive connection lifecycle and
/// data notifications from <see cref="TcpSocketServer"/>.
/// </summary>
/// <remarks>
/// Method calls are made from IOCP ThreadPool threads. Implementations
/// must be thread-safe. For ordered processing, use <see cref="OrderedDispatcher"/>
/// which routes events by <see cref="ISession.UnitOfOrder"/>.
///
/// To avoid per-receive heap allocations, implement <see cref="IBufferHandler"/>
/// in addition to this interface.
/// </remarks>
public interface IConnectionHandler
{
    /// <summary>Called when the server starts listening.</summary>
    void OnServerStarted();

    /// <summary>Called when the server stops listening.</summary>
    void OnServerStopped();

    /// <summary>
    /// Called when a new client connection is accepted and a session is assigned.
    /// </summary>
    /// <param name="session">
    /// The session handle for the new connection.
    /// Valid for the lifetime of the connection.
    /// </param>
    void OnConnected(ISession session);

    /// <summary>
    /// Called when data has been received from a client.
    /// </summary>
    /// <param name="session">The session that received data.</param>
    /// <param name="data">
    /// A newly allocated byte array containing the received bytes.
    /// The array is owned by the handler; the server does not retain a reference.
    /// </param>
    void OnDataReceived(ISession session, byte[] data);

    /// <summary>
    /// Called when a client has been disconnected (gracefully or due to error).
    /// The session handle becomes stale after this call returns.
    /// </summary>
    /// <param name="session">The session that was disconnected.</param>
    void OnDisconnected(ISession session);
}
```

**Key changes from `IConsumer`:**
- `OnStart`/`OnStop` renamed to `OnServerStarted`/`OnServerStopped`.
- `OnReceivedData` renamed to `OnDataReceived` (.NET naming convention).
- `OnClientConnected`/`OnClientDisconnected` renamed to `OnConnected`/`OnDisconnected`.
- Parameter type is `ISession` instead of `ITcpSocket`.

---

### 7.2 `IBufferHandler` (interface)

Renamed from `IBufferConsumer`. Now actively used as the primary zero-copy fast path. This resolves gap **Z3** from `architecture.md`.

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>
/// Optional fast-path handler that receives data directly from the shared
/// pinned buffer, avoiding a per-receive <c>byte[]</c> heap allocation.
/// </summary>
/// <remarks>
/// When a handler implements both <see cref="IConnectionHandler"/> and
/// <see cref="IBufferHandler"/>, the server calls <see cref="OnDataReceived"/>
/// on this interface instead of <see cref="IConnectionHandler.OnDataReceived"/>.
///
/// <strong>CRITICAL:</strong> The <see cref="BufferSegment"/> is only valid for the
/// duration of the call. The underlying memory is part of the shared pinned
/// buffer and will be overwritten by the next receive. If you need to retain
/// the data, copy it before returning.
/// </remarks>
public interface IBufferHandler
{
    /// <summary>
    /// Called when data has been received, providing a zero-copy view into
    /// the shared receive buffer.
    /// </summary>
    /// <param name="session">The session that received data.</param>
    /// <param name="data">
    /// A <see cref="BufferSegment"/> referencing the received bytes.
    /// Valid only for the duration of this call.
    /// </param>
    void OnDataReceived(ISession session, BufferSegment data);
}
```

**Dispatch logic in `TcpSocketServer`:**

```csharp
// Inside TcpSocketServer's IIoChannelOwner.OnDataReceived implementation:
void IIoChannelOwner.OnDataReceived(int channelId, BufferSegment data)
{
    Session session = _sessionPool.AllSessions[channelId];
    SessionHandle handle = new SessionHandle(session, session.Generation);
    session.RecordReceive(data.Length);

    try
    {
        if (_handler is IBufferHandler bufferHandler)
        {
            // Zero-copy path: handler processes data in-place from the pinned buffer.
            bufferHandler.OnDataReceived(handle, data);
        }
        else
        {
            // Copy path: allocate + copy for handlers that don't implement IBufferHandler.
            byte[] copy = new byte[data.Length];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Length);
            _handler.OnDataReceived(handle, copy);
        }
    }
    catch (Exception ex)
    {
        // Swallow consumer exceptions to protect the I/O loop (same as current behavior).
        Logger.Exception(ex);
    }
}
```

---

### 7.3 `OrderedDispatcher` (abstract class)

Renamed from `ThreadedBlockingQueueConsumer`. Implements `IConnectionHandler` and routes events by `UnitOfOrder` to per-lane worker threads.

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>
/// Dispatches connection events to per-lane worker threads, preserving
/// event ordering within each lane while enabling parallel processing
/// across lanes.
/// </summary>
/// <remarks>
/// Subclass this and implement the <c>Handle*</c> methods to receive
/// events on dedicated worker threads with ordering guarantees.
///
/// Lane assignment: Each session is assigned a lane on connection via
/// <see cref="ISession.UnitOfOrder"/>. All events for sessions on the
/// same lane are delivered to the same worker thread in order.
/// </remarks>
public abstract class OrderedDispatcher : IConnectionHandler, IDisposable
{
    /// <summary>Number of worker lanes.</summary>
    public int LaneCount { get; }

    /// <summary>
    /// Creates the dispatcher with the specified number of lanes.
    /// </summary>
    /// <param name="laneCount">
    /// Number of parallel ordering lanes
    /// (should match <see cref="ServerConfig.MaxUnitOfOrder"/>).
    /// </param>
    /// <param name="identity">Name prefix for worker threads (for diagnostics).</param>
    protected OrderedDispatcher(int laneCount, string identity = "OrderedDispatcher");

    // ── Abstract methods (called on worker threads with ordering guarantees) ──

    /// <summary>Called when a new connection is established.</summary>
    protected abstract void HandleConnected(ISession session);

    /// <summary>Called when data is received from a client.</summary>
    protected abstract void HandleDataReceived(ISession session, byte[] data);

    /// <summary>Called when a client disconnects.</summary>
    protected abstract void HandleDisconnected(ISession session);

    // ── IConnectionHandler (routes events to per-lane BlockingCollection queues) ──

    void IConnectionHandler.OnServerStarted();   // creates queues + threads
    void IConnectionHandler.OnServerStopped();    // cancels + joins threads
    void IConnectionHandler.OnConnected(ISession session);
    void IConnectionHandler.OnDataReceived(ISession session, byte[] data);
    void IConnectionHandler.OnDisconnected(ISession session);

    public void Dispose();
}
```

**Carried forward from:** `ThreadedBlockingQueueConsumer` — per-UnitOfOrder `BlockingCollection<T>` + dedicated `Thread` per lane (requirements C8-C9).

---

### 7.4 `QueuedConnectionHandler` (sealed class)

Renamed from `BlockingQueueConsumer`. Collects all events into a single blocking queue for pull-based consumption.

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>
/// Collects all connection events into a single <see cref="BlockingCollection{T}"/>
/// for pull-based consumption by application code.
/// </summary>
public sealed class QueuedConnectionHandler : IConnectionHandler
{
    /// <summary>
    /// The event queue. Call <c>Take()</c> on a dedicated thread to consume events.
    /// </summary>
    public BlockingCollection<ConnectionEvent> Events { get; }

    void IConnectionHandler.OnServerStarted();
    void IConnectionHandler.OnServerStopped();
    void IConnectionHandler.OnConnected(ISession session);
    void IConnectionHandler.OnDataReceived(ISession session, byte[] data);
    void IConnectionHandler.OnDisconnected(ISession session);
}
```

---

### 7.5 `EventConnectionHandler` (sealed class)

Renamed from `EventConsumer`. Bridges to .NET events.

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>
/// Bridges <see cref="IConnectionHandler"/> to standard .NET events.
/// Subscribe to events instead of implementing an interface.
/// </summary>
public sealed class EventConnectionHandler : IConnectionHandler
{
    public event EventHandler<SessionConnectedEventArgs>? Connected;
    public event EventHandler<SessionDisconnectedEventArgs>? Disconnected;
    public event EventHandler<DataReceivedEventArgs>? DataReceived;

    void IConnectionHandler.OnServerStarted();
    void IConnectionHandler.OnServerStopped();
    void IConnectionHandler.OnConnected(ISession session);
    void IConnectionHandler.OnDataReceived(ISession session, byte[] data);
    void IConnectionHandler.OnDisconnected(ISession session);
}
```

---

### 7.6 Supporting Types

```csharp
namespace Arrowgene.Networking.Handler;

/// <summary>Type of connection event.</summary>
public enum ConnectionEventType { Connected, DataReceived, Disconnected }

/// <summary>Queued event data carrier.</summary>
public sealed class ConnectionEvent
{
    public ConnectionEventType EventType { get; }
    public ISession Session { get; }
    public byte[]? Data { get; }
    public ConnectionEvent(ISession session, ConnectionEventType eventType, byte[]? data = null);
}

public sealed class SessionConnectedEventArgs(ISession session) : EventArgs
{
    public ISession Session { get; } = session;
}

public sealed class SessionDisconnectedEventArgs(ISession session) : EventArgs
{
    public ISession Session { get; } = session;
}

public sealed class DataReceivedEventArgs(ISession session, byte[] data) : EventArgs
{
    public ISession Session { get; } = session;
    public byte[] Data { get; } = data;
}
```

---

## 8. Complete Inventory

### Interfaces (6)

| Interface | Layer | Namespace | Replaces |
|---|---|---|---|
| `ISession` | 3 | `Session` | `ITcpSocket` |
| `IAcceptHandler` | 2 | `Transport` | *(new — extracted callback)* |
| `IIoChannelOwner` | 2 | `Transport` | *(new — extracted callback)* |
| `IConnectionHandler` | 5 | `Handler` | `IConsumer` |
| `IBufferHandler` | 5 | `Handler` | `IBufferConsumer` |
| `ICloneable` | — | `System` | *(existing, used by ServerConfig)* |

### Classes, Structs, and Enums (22)

| Type | Kind | Layer | Namespace | Replaces |
|---|---|---|---|---|
| `BufferSegment` | readonly struct | 1 | `Memory` | *(new — replaces ad-hoc tuples)* |
| `PinnedBufferSlab` | sealed class | 1 | `Memory` | *(inlined in AsyncEventServer ctor)* |
| `PreallocatedPool<T>` | sealed class | 1 | `Memory` | *(inlined Stack + lock patterns)* |
| `TcpListenerEngine` | sealed class | 2 | `Transport` | *(inlined accept loop)* |
| `IoChannel` | sealed class | 2 | `Transport` | *(inlined recv/send loops + SAEA ownership)* |
| `SendQueue` | sealed class | 2 | `Transport` | `AsyncEventWriteState` |
| `EnqueueResult` | enum | 2 | `Transport` | *(new — replaces bool + out param)* |
| `Session` | sealed class | 3 | `Session` | `AsyncEventClient` |
| `SessionHandle` | readonly struct | 3 | `Session` | `AsyncEventClientHandle` |
| `SessionPool` | sealed class | 3 | `Session` | *(inlined pool logic)* |
| `SessionRegistry` | sealed class | 3 | `Session` | *(inlined handle list + UoO)* |
| `ServerConfig` | sealed class | 4 | `Configuration` | `AsyncEventSettings` |
| `TcpSocketServer` | sealed class | 4 | `Server` | `AsyncEventServer` + `TcpServer` (abstract) |
| `ConnectionSupervisor` | sealed class | 4 | `Server` | *(inlined CheckSocketTimeout)* |
| `IConnectionHandler` | interface | 5 | `Handler` | `IConsumer` |
| `IBufferHandler` | interface | 5 | `Handler` | `IBufferConsumer` |
| `OrderedDispatcher` | abstract class | 5 | `Handler` | `ThreadedBlockingQueueConsumer` |
| `QueuedConnectionHandler` | sealed class | 5 | `Handler` | `BlockingQueueConsumer` |
| `EventConnectionHandler` | sealed class | 5 | `Handler` | `EventConsumer` |
| `ConnectionEvent` | sealed class | 5 | `Handler` | `ClientEvent` |
| `ConnectionEventType` | enum | 5 | `Handler` | `ClientEventType` |
| `SessionConnectedEventArgs` | sealed class | 5 | `Handler` | `ConnectedEventArgs` |
| `SessionDisconnectedEventArgs` | sealed class | 5 | `Handler` | `DisconnectedEventArgs` |
| `DataReceivedEventArgs` | sealed class | 5 | `Handler` | `ReceivedPacketEventArgs` |

### Retained Utility Classes (unchanged)

| Type | Namespace | Notes |
|---|---|---|
| `SocketSettings` | `Arrowgene.Networking` | Unchanged — still configures raw sockets |
| `SocketOption` | `Arrowgene.Networking` | Unchanged — socket option triple |
| `NetworkPoint` | `Arrowgene.Networking` | Unchanged — IP + port pair |
| `Service` | `Arrowgene.Networking` | Unchanged — internal utilities |

---

## 9. Namespace and Project Structure

```
Arrowgene.Networking/
├── Memory/
│   ├── BufferSegment.cs
│   ├── PinnedBufferSlab.cs
│   └── PreallocatedPool.cs
│
├── Transport/
│   ├── IAcceptHandler.cs
│   ├── IIoChannelOwner.cs
│   ├── TcpListenerEngine.cs
│   ├── IoChannel.cs
│   ├── SendQueue.cs
│   └── EnqueueResult.cs
│
├── Session/
│   ├── ISession.cs
│   ├── Session.cs
│   ├── SessionHandle.cs
│   ├── SessionPool.cs
│   └── SessionRegistry.cs
│
├── Server/
│   ├── TcpSocketServer.cs
│   └── ConnectionSupervisor.cs
│
├── Configuration/
│   └── ServerConfig.cs
│
├── Handler/
│   ├── IConnectionHandler.cs
│   ├── IBufferHandler.cs
│   ├── OrderedDispatcher.cs
│   ├── QueuedConnectionHandler.cs
│   ├── EventConnectionHandler.cs
│   ├── ConnectionEvent.cs
│   ├── ConnectionEventType.cs
│   ├── SessionConnectedEventArgs.cs
│   ├── SessionDisconnectedEventArgs.cs
│   └── DataReceivedEventArgs.cs
│
├── Common/
│   └── SyncList.cs
│
├── SocketSettings.cs
├── SocketOption.cs
├── NetworkPoint.cs
└── Service.cs

Arrowgene.Networking.Test/
├── Memory/
│   ├── BufferSegmentTests.cs
│   ├── PinnedBufferSlabTests.cs
│   └── PreallocatedPoolTests.cs
│
├── Transport/
│   ├── TcpListenerEngineTests.cs
│   ├── IoChannelTests.cs
│   └── SendQueueTests.cs
│
├── Session/
│   ├── SessionTests.cs
│   ├── SessionHandleTests.cs
│   ├── SessionPoolTests.cs
│   └── SessionRegistryTests.cs
│
├── Server/
│   ├── TcpSocketServerTests.cs
│   └── ConnectionSupervisorTests.cs
│
└── Handler/
    └── OrderedDispatcherTests.cs
```

---

## 10. Inter-Component Interaction Flows

### 10.1 Callback Chains

```
TcpSocketServer
  │
  │ ──creates──► TcpListenerEngine(config, this as IAcceptHandler)
  │                    │
  │                    │ ──callback──► IAcceptHandler.OnSocketAccepted(socket)
  │                    │ ──callback──► IAcceptHandler.OnAcceptError(error)
  │
  │ ──creates──► IoChannel[](channelId, readSeg, writeSeg, this as IIoChannelOwner)
  │                    │
  │                    │ ──callback──► IIoChannelOwner.OnDataReceived(channelId, segment)
  │                    │ ──callback──► IIoChannelOwner.OnChannelError(channelId, error)
  │                    │ ──callback──► IIoChannelOwner.OnSendCompleted(channelId, bytes)
  │
  │ ──creates──► ConnectionSupervisor(timeout, registry, this.Disconnect)
  │                    │
  │                    │ ──callback──► Action<SessionHandle> onIdleSession
  │
  │ ──calls──► IConnectionHandler / IBufferHandler
  │                 .OnServerStarted()
  │                 .OnConnected(session)
  │                 .OnDataReceived(session, data/segment)
  │                 .OnDisconnected(session)
  │                 .OnServerStopped()
```

### 10.2 Accept → Receive → Handler (method signatures)

```
TcpListenerEngine:
  _semaphore.Wait(ct)
  acceptSaea = _pool.TryPop()
  _listenSocket.AcceptAsync(acceptSaea)
    │
    v  (sync or IOCP callback)
  Validate: _isListening, runGeneration match, SocketError.Success
  acceptSaea.AcceptSocket = null   // return to pool
  _pool.Push(acceptSaea)
  _semaphore.Release()
  _acceptHandler.OnSocketAccepted(acceptedSocket)
        │
        v
  TcpSocketServer.OnSocketAccepted(Socket acceptedSocket):
    _config.ClientSocketSettings.ConfigureSocket(acceptedSocket)
    _sessionPool.TryAcquire(out Session session)
      └── (pool exhausted → Service.CloseSocket(acceptedSocket), return)
    int unitOfOrder = _registry.Register(handle)
    int generation = _sessionPool.IncrementGeneration(session.SessionId)
    SessionHandle handle = new SessionHandle(session, generation)
    session.Initialize(acceptedSocket, unitOfOrder, _runGeneration, generation, handle)
    _handler.OnConnected(handle)
    session.Channel.Activate(acceptedSocket)
          │
          v
  IoChannel.Activate(Socket socket):
    _socket = socket
    _isActive = true
    StartReceive()
          │
          v
  IoChannel.StartReceive():
    Interlocked.Increment(ref _pendingOperations)
    bool willRaiseEvent = _socket.ReceiveAsync(_readSaea)
    if (!willRaiseEvent) ProcessReceive()  // sync path
          │
          v  (sync or IOCP callback)
  IoChannel.ProcessReceive():
    validate: IsActive, SocketError.Success, BytesTransferred > 0
    BufferSegment received = new(_readSaea.Buffer, _readSaea.Offset, _readSaea.BytesTransferred)
    _owner.OnDataReceived(ChannelId, received)
      │
      v
    TcpSocketServer.IIoChannelOwner.OnDataReceived(channelId, segment):
      session.RecordReceive(segment.Length)
      if (_handler is IBufferHandler bh)
        bh.OnDataReceived(handle, segment)     // ZERO-COPY
      else
        byte[] copy = new byte[segment.Length]
        Buffer.BlockCopy(...)
        _handler.OnDataReceived(handle, copy)  // COPY
      │
      v  (loops back)
    StartReceive()
  finally:
    Interlocked.Decrement(ref _pendingOperations)
```

### 10.3 Send Flow (method signatures)

```
Application code: handle.Send(data)
  │
  └── Session.Send(byte[] data)
        └── _server.Send(handle, data)
              │
              v
  TcpSocketServer.Send(SessionHandle handle, byte[] data):
    handle.TryGetSession(out Session session) → bool
    session.Channel.EnqueueSend(data) → EnqueueResult
      │
      ├── EnqueueResult.QueueOverflow → Disconnect(handle)
      ├── EnqueueResult.Queued → return (send already in progress)
      └── EnqueueResult.SendStarted → (send chain begins below)
              │
              v
  IoChannel.StartSend():
    _sendQueue.TryGetChunk(_writeSegment.Length, out data, out offset, out count) → bool
    Buffer.BlockCopy(data, offset, _writeSaea.Buffer, _writeSaea.Offset, count)
    _writeSaea.SetBuffer(_writeSaea.Offset, count)
    Interlocked.Increment(ref _pendingOperations)
    bool willRaiseEvent = _socket.SendAsync(_writeSaea)
    if (!willRaiseEvent) ProcessSend()  // sync path
          │
          v  (sync or IOCP callback)
  IoChannel.ProcessSend():
    validate: IsActive, SocketError.Success, BytesTransferred > 0
    _owner.OnSendCompleted(ChannelId, _writeSaea.BytesTransferred)
      │
      v
    TcpSocketServer.IIoChannelOwner.OnSendCompleted(channelId, bytes):
      session.RecordSend(bytes)
      _sendQueue.CompleteChunk(bytes) → bool moreData
      if (moreData) StartSend()  // continue chain
  finally:
    Interlocked.Decrement(ref _pendingOperations)
```

### 10.4 Disconnect Flow (method signatures)

```
TcpSocketServer.Disconnect(SessionHandle handle):
  handle.TryGetSession(out Session session) → bool
  session.TryBeginShutdown() → bool
    └── (returns false if already shutting down → TryRecycle only)
  session.Channel.SendQueue.ClearQueued()
  session.Channel.BeginShutdown()     // prevents new I/O
  _registry.Unregister(handle, session.UnitOfOrder)
  Log: duration, BytesReceived, BytesSent, remaining active count
  _handler.OnDisconnected(handle)
  _sessionPool.TryRecycle(session)
    │
    ├── session.Channel.PendingOperations > 0
    │     └── return false (IOCP callbacks will retry TryRecycle)
    │
    └── session.TryMarkPooled() → true
          session.Channel.Reset()
          _pool.Push(session)   // available for next connection
```

### 10.5 Server Lifecycle (Start / Stop)

```
TcpSocketServer.Start():
  lock (_lifecycleLock):
    validate: !_isDisposed, !_isRunning, pools fully drained
    _sessionPool.IncrementAllGenerations()     // invalidate stale handles
    _registry.ResetCounters()                  // zero unit-of-order counters
    _cancellation = new CancellationTokenSource()
    _handler.OnServerStarted()
    _listenerEngine.Start(_cancellation.Token)
      └── binds socket, starts accept loop on background thread
    if (timeout > 0):
      _supervisor.Start(_cancellation.Token)
        └── starts monitoring on background thread
    _isRunning = true

TcpSocketServer.Stop():
  lock (_lifecycleLock):
    validate: _isRunning
    _isRunning = false
    _cancellation.Cancel()
    _listenerEngine.Stop(drainTimeoutMs)
      └── closes listen socket, increments run generation, joins thread
    if (_supervisor != null):
      _supervisor.Stop(joinTimeoutMs)
        └── joins monitoring thread
    List<SessionHandle> snapshot = ...
    _registry.SnapshotActiveSessions(snapshot)
    foreach (handle in snapshot):
      Disconnect(handle)
    _sessionPool.WaitForDrain(timeoutMs)
    _listenerEngine.WaitForDrain(timeoutMs)
    _handler.OnServerStopped()
    _cancellation.Dispose()
```

---

## 11. Lock Domains

Each component owns exactly one lock domain. No component ever acquires another component's lock. This eliminates the cross-concern lock nesting from the legacy design.

| Component | Lock Field | Protects |
|---|---|---|
| `PreallocatedPool<T>` | `_lock` | Internal stack (push/pop) |
| `TcpListenerEngine` | `_acceptLock` | Accept SAEA pool + semaphore operations |
| `SendQueue` | `_sendLock` | Write queue, in-flight chunk state, send-in-progress flag |
| `SessionPool` | `_poolLock` | Session stack, generation array |
| `SessionRegistry` | `_registryLock` | Active handles list, unit-of-order counters |
| `Session` | `_stateLock` | `_isAlive` + `_isPooled` state machine flags |
| `TcpSocketServer` | `_lifecycleLock` | `_isRunning` / `_isDisposed` transitions |

**Contrast with legacy:** In `AsyncEventServer`, `_clientLock` simultaneously protected the pool stack, the active handles list, and the unit-of-order counters. Now these are three separate locks in three separate classes.

---

## 12. Atomic Operations (preserved from legacy)

| Location | Operation | Mechanism |
|---|---|---|
| `Session.RecordReceive` | Update `_lastReadTicks`, `_bytesReceived` | `Volatile.Write`, `Interlocked.Add` |
| `Session.RecordSend` | Update `_lastWriteTicks`, `_bytesSent` | `Volatile.Write`, `Interlocked.Add` |
| `IoChannel` | Track `_pendingOperations` | `Interlocked.Increment` / `Decrement` |
| `SessionPool` | Increment `_generations[sessionId]` | `Interlocked.Increment` |
| `TcpListenerEngine` | Increment `_runGeneration` | `Interlocked.Increment` |
| `TcpSocketServer._isRunning` | Server state flag | `volatile bool` |

---

## 13. Requirements Traceability

Every requirement from `architecture.md` is mapped to its new component:

| Req | Requirement | New Component | Notes |
|---|---|---|---|
| M1 | Pinned byte array allocation | `PinnedBufferSlab` | `GC.AllocateArray<byte>(size, true)` |
| M2 | Size buffer for all connections | `PinnedBufferSlab` | `segmentSize * segmentCount` |
| M3 | Non-overlapping buffer regions | `PinnedBufferSlab.GetSegment(index)` | Sequential offsets at construction |
| M4 | Keep buffer reference alive | `PinnedBufferSlab.RawBuffer` field | Prevents GC collection |
| P1 | Preallocate all session objects | `SessionPool` constructor | Factory creates all sessions upfront |
| P2 | Pop on accept, push on recycle | `SessionPool.TryAcquire` / `TryRecycle` | |
| P3 | State machine guards | `Session._isAlive` + `_isPooled` | Four-state lifecycle |
| P4 | Track pending async operations | `IoChannel.PendingOperations` | `Interlocked` counter |
| P5 | Reset write state on recycle | `IoChannel.Reset()` → `SendQueue.Reset()` | |
| P6 | Preallocate accept SAEA | `TcpListenerEngine` via `PreallocatedPool<SocketAsyncEventArgs>` | |
| P7 | Semaphore-gated accepts | `TcpListenerEngine` internal `SemaphoreSlim` | |
| P8 | Clear AcceptSocket on return | `TcpListenerEngine` return logic | `saea.AcceptSocket = null` |
| P9 | Per-session generation counter | `SessionPool._generations[]` | `Interlocked.Increment` |
| P10 | Per-server run generation | `TcpListenerEngine._runGeneration` | Incremented on start/stop |
| P11 | Lightweight handle struct | `SessionHandle` | `readonly struct` with generation check |
| Z1 | Pass buffer/offset/count | `IIoChannelOwner.OnDataReceived(channelId, BufferSegment)` | |
| Z2 | Zero-copy consumer interface | `IBufferHandler.OnDataReceived(session, BufferSegment)` | |
| Z3 | **Gap resolved** | `TcpSocketServer` checks `is IBufferHandler` | No copy when handler implements it |
| Z4 | Defer copy to send time | `SendQueue.Enqueue` stores reference | |
| Z5 | Copy into pinned buffer when sending | `IoChannel.StartSend` via `Buffer.BlockCopy` | |
| Z6 | Chunk large payloads | `SendQueue.TryGetChunk` / `CompleteChunk` | |
| C1 | SAEA async I/O | `IoChannel` / `TcpListenerEngine` | `AcceptAsync`, `ReceiveAsync`, `SendAsync` |
| C2 | Synchronous completion fallback | `IoChannel` / `TcpListenerEngine` | `if (!willRaiseEvent) ProcessInline()` |
| C3 | Fine-grained locks per concern | Each component owns its own lock | See Lock Domains table |
| C4 | Lock scope minimization | Each component locks only for mutations | Never across I/O operations |
| C5 | Interlocked for counters | `Session`, `IoChannel`, `SessionPool` | |
| C6 | Volatile for timestamps and flags | `Session`, `TcpSocketServer` | |
| C7 | Bracket async I/O with pending ops | `IoChannel` | Increment before, decrement in finally |
| C8 | UnitOfOrder assignment | `SessionRegistry.Register` | Least-loaded lane scan |
| C9 | Per-UnitOfOrder threading | `OrderedDispatcher` | One `BlockingCollection` + `Thread` per lane |
| B1 | Hard connection limit | `SessionPool.TryAcquire` fails when exhausted | |
| B2 | Accept throttling | `TcpListenerEngine` internal semaphore | |
| B3 | Per-session write queue cap | `SendQueue.MaxQueuedBytes` (now configurable via `ServerConfig`) | |
| B4 | Disconnect on write overflow | `TcpSocketServer.Send` checks `EnqueueResult.QueueOverflow` | |
| B5 | Socket timeout detection | `ConnectionSupervisor` | Dedicated monitoring thread |
| B6 | Orderly drain on shutdown | `SessionPool.WaitForDrain` + `TcpListenerEngine.WaitForDrain` | |
| B7 | In-flight send preservation | `SendQueue.ClearQueued` | Preserves outstanding chunk state |

---

## 14. Resolved Architectural Gaps

### Gap 1: Zero-Copy Receive Not Wired Up (Z3)

**Legacy:** `IBufferConsumer` existed but `TcpServer.OnReceivedData(buffer, offset, count)` at line 37-41 always copied into a new `byte[]` before calling `IConsumer.OnReceivedData`.

**New design:** `TcpSocketServer` implements `IIoChannelOwner.OnDataReceived` with a runtime `is IBufferHandler` check. When the handler implements `IBufferHandler`, data is delivered as a `BufferSegment` directly referencing the pinned buffer — zero heap allocations on the receive path.

### Gap 2: No Extracted Buffer/Pool Abstractions

**Legacy:** All buffer management (M1-M4) and pooling logic (P1-P8) were inlined in `AsyncEventServer`'s constructor and methods, making the class ~1200 lines.

**New design:** Three dedicated classes:
- `PinnedBufferSlab` — buffer allocation and segment partitioning
- `PreallocatedPool<T>` — generic pool
- `SessionPool` — session-specific lifecycle with generation tracking

Each is independently testable with no dependencies on I/O or networking.

### Gap 3: Monolithic Server Class

**Legacy:** `AsyncEventServer` mixed accept logic, receive logic, send logic, timeout monitoring, pool management, consumer dispatch, drain waiting, and logging into a single 1203-line class.

**New design:** Seven focused classes replace it:

| Responsibility | Class | Est. Lines |
|---|---|---|
| Accept loop | `TcpListenerEngine` | ~200 |
| Async receive + send | `IoChannel` | ~280 |
| Write queue/chunking | `SendQueue` | ~150 |
| Session lifecycle | `SessionPool` | ~120 |
| Active session tracking | `SessionRegistry` | ~80 |
| Timeout monitoring | `ConnectionSupervisor` | ~80 |
| Orchestration facade | `TcpSocketServer` | ~300 |

### Gap 4: Consumer Dispatch Always Copies

**Legacy:** `TcpServer.OnReceivedData(socket, buffer, offset, count)` at line 37-41 unconditionally allocated `new byte[count]` + `Buffer.BlockCopy`.

**New design:** The copy only happens when the handler does NOT implement `IBufferHandler`. The zero-copy path is the default for handlers that opt in.

### Gap 5: Inheritance-Driven Composition

**Legacy:** `AsyncEventServer` inherited from `TcpServer` (abstract), which owned the `IConsumer` reference and the `Start()`/`Stop()` template methods. This forced a rigid inheritance chain.

**New design:** `TcpSocketServer` is a `sealed class` with no base class. The handler is a constructor parameter. Each component can be instantiated and tested independently by mocking its callback interface (`IAcceptHandler`, `IIoChannelOwner`).

### Gap 6: No Listener vs. Client Socket Configuration

**Legacy:** `SocketSettings` was applied only to the listener socket. Accepted client sockets got only a hardcoded `KeepAlive` (see TODO at `AsyncEventServer.cs:580-581`).

**New design:** `ServerConfig` has separate `ListenerSocketSettings` and `ClientSocketSettings`. The listener engine applies `ListenerSocketSettings` to the listen socket. `TcpSocketServer.OnSocketAccepted` applies `ClientSocketSettings` to each accepted socket.
