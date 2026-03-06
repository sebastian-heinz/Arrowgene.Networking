using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

/// <summary>
/// Represents one pooled client slot managed by <see cref="AsyncEventServer"/>.
/// </summary>
public sealed class AsyncEventClient : ITcpSocket, IDisposable
{
    private readonly object _stateLock;
    private readonly AsyncEventServer _server;
    private readonly AsyncEventSendQueue _sendQueue;
    private readonly int _sendBufferSize;
    private Socket? _connectionSocket;
    private bool _isAlive;
    private bool _isInPool;
    private long _lastReadTicks;
    private long _lastWriteTicks;
    private long _bytesReceived;
    private long _bytesSent;
    private int _pendingOperations;

    /// <summary>
    /// Creates a pooled client slot.
    /// </summary>
    /// <param name="clientId">The stable client slot identifier.</param>
    /// <param name="server">The owning server.</param>
    /// <param name="receiveEventArgs">The dedicated receive event args.</param>
    /// <param name="sendEventArgs">The dedicated send event args.</param>
    /// <param name="maxQueuedSendBytes">The maximum queued outbound bytes.</param>
    public AsyncEventClient(
        int clientId,
        AsyncEventServer server,
        SocketAsyncEventArgs receiveEventArgs,
        SocketAsyncEventArgs sendEventArgs,
        int maxQueuedSendBytes)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        ClientId = clientId;
        _server = server;
        _stateLock = new object();
        _sendQueue = new AsyncEventSendQueue(maxQueuedSendBytes);
        ReceiveEventArgs = receiveEventArgs ?? throw new ArgumentNullException(nameof(receiveEventArgs));
        SendEventArgs = sendEventArgs ?? throw new ArgumentNullException(nameof(sendEventArgs));
        _sendBufferSize = SendEventArgs.Count;
        ReceiveEventArgs.UserToken = this;
        SendEventArgs.UserToken = this;
        _isInPool = true;
        Identity = "[Unknown Client]";
        RemoteIpAddress = IPAddress.None;
        ConnectedAt = DateTime.MinValue;
    }

    /// <summary>
    /// Gets the stable pooled client slot identifier.
    /// </summary>
    public int ClientId { get; }

    /// <summary>
    /// Gets the current connection identity.
    /// </summary>
    public string Identity { get; private set; }

    /// <summary>
    /// Gets the server run generation that activated this client.
    /// </summary>
    public int RunGeneration { get; private set; }

    /// <summary>
    /// Gets the client generation used by <see cref="AsyncEventClientHandle"/>.
    /// </summary>
    public int Generation { get; private set; }

    /// <inheritdoc />
    public IPAddress RemoteIpAddress { get; private set; }

    /// <inheritdoc />
    public ushort Port { get; private set; }

    /// <inheritdoc />
    public int UnitOfOrder { get; private set; }

    /// <summary>
    /// Gets the last successful receive tick.
    /// </summary>
    public long LastReadTicks => Volatile.Read(ref _lastReadTicks);

    /// <summary>
    /// Gets the last successful send tick.
    /// </summary>
    public long LastWriteTicks => Volatile.Read(ref _lastWriteTicks);

    /// <inheritdoc />
    public DateTime ConnectedAt { get; private set; }

    /// <inheritdoc />
    public ulong BytesReceived => unchecked((ulong)Interlocked.Read(ref _bytesReceived));

    /// <summary>
    /// Gets the total bytes sent.
    /// </summary>
    public ulong BytesSent => unchecked((ulong)Interlocked.Read(ref _bytesSent));

    /// <inheritdoc />
    public ulong BytesSend => BytesSent;

    /// <inheritdoc />
    public bool IsAlive
    {
        get
        {
            lock (_stateLock)
            {
                return _isAlive;
            }
        }
    }

    internal SocketAsyncEventArgs ReceiveEventArgs { get; }

    internal SocketAsyncEventArgs SendEventArgs { get; }

    internal int PendingOperations => Volatile.Read(ref _pendingOperations);

    internal Socket? ConnectionSocket
    {
        get
        {
            lock (_stateLock)
            {
                return _connectionSocket;
            }
        }
    }

    /// <inheritdoc />
    public void Send(byte[] data)
    {
        Send(this, data);
    }

    /// <summary>
    /// Sends data through the owning server using a stale-safe handle.
    /// </summary>
    /// <typeparam name="T">The socket handle type.</typeparam>
    /// <param name="socket">The socket handle.</param>
    /// <param name="data">The payload to send.</param>
    public void Send<T>(T socket, byte[] data) where T : ITcpSocket
    {
        _server.Send(socket, data);
    }

    internal void Activate(Socket socket, int unitOfOrder, int runGeneration, int generation)
    {
        if (socket is null)
        {
            throw new ArgumentNullException(nameof(socket));
        }

        _sendQueue.Reset();

        lock (_stateLock)
        {
            if (_isAlive || !_isInPool)
            {
                ThrowInvalidStateException();
            }

            long now = Environment.TickCount64;
            Volatile.Write(ref _lastReadTicks, now);
            Volatile.Write(ref _lastWriteTicks, now);
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _pendingOperations, 0);

            _connectionSocket = socket;
            UnitOfOrder = unitOfOrder;
            RunGeneration = runGeneration;
            Generation = generation;
            ConnectedAt = DateTime.UtcNow;

            if (socket.RemoteEndPoint is IPEndPoint remoteEndPoint)
            {
                RemoteIpAddress = remoteEndPoint.Address;
                Port = checked((ushort)remoteEndPoint.Port);
                Identity = $"[{remoteEndPoint.Address}:{remoteEndPoint.Port}]";
            }
            else
            {
                RemoteIpAddress = IPAddress.None;
                Port = 0;
                Identity = "[Unknown Client]";
            }

            _isInPool = false;
            _isAlive = true;
        }
    }

    internal AsyncEventClientHandle CreateHandle()
    {
        return new AsyncEventClientHandle(this, Generation);
    }

    internal bool IsHandleValid(int generation)
    {
        lock (_stateLock)
        {
            return !_isInPool && Generation == generation;
        }
    }

    internal bool TryBeginDisconnect()
    {
        Socket? socketToClose;

        lock (_stateLock)
        {
            if (!_isAlive)
            {
                return false;
            }

            _isAlive = false;
            socketToClose = _connectionSocket;
            _connectionSocket = null;
        }

        Service.CloseSocket(socketToClose);
        return true;
    }

    internal bool TryPrepareSendChunk(out int chunkSize)
    {
        byte[]? sendBuffer = SendEventArgs.Buffer;
        if (sendBuffer is null)
        {
            chunkSize = 0;
            return false;
        }

        return _sendQueue.TryCopyNextChunk(sendBuffer, SendEventArgs.Offset, _sendBufferSize, out chunkSize);
    }

    internal bool TryBeginSocketOperation(out Socket socket)
    {
        lock (_stateLock)
        {
            if (!_isAlive || _isInPool || _connectionSocket is not Socket connectionSocket)
            {
                socket = null!;
                return false;
            }

            Interlocked.Increment(ref _pendingOperations);
            socket = connectionSocket;
            return true;
        }
    }

    internal bool QueueSend(byte[] data, out bool startSend, out bool queueOverflow)
    {
        lock (_stateLock)
        {
            if (!_isAlive)
            {
                startSend = false;
                queueOverflow = false;
                return false;
            }

            return _sendQueue.TryEnqueueCopy(data, out startSend, out queueOverflow);
        }
    }

    internal bool CompleteSend(int transferredCount)
    {
        return _sendQueue.CompleteSend(transferredCount);
    }

    internal void ClearQueuedSends()
    {
        _sendQueue.ClearPendingMessages();
    }

    internal void ResetForPool()
    {
        lock (_stateLock)
        {
            _connectionSocket = null;
            _isAlive = false;
            Identity = "[Unknown Client]";
            RemoteIpAddress = IPAddress.None;
            Port = 0;
            UnitOfOrder = 0;
            ConnectedAt = DateTime.MinValue;
            RunGeneration = 0;
        }

        Volatile.Write(ref _lastReadTicks, 0);
        Volatile.Write(ref _lastWriteTicks, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _pendingOperations, 0);
        _sendQueue.Reset();
        SendEventArgs.SetBuffer(SendEventArgs.Offset, _sendBufferSize);
    }

    internal bool TryReturnToPool()
    {
        lock (_stateLock)
        {
            if (_isInPool || _isAlive || Volatile.Read(ref _pendingOperations) > 0)
            {
                return false;
            }

            _isInPool = true;
            return true;
        }
    }

    internal void IncrementPendingOperations()
    {
        Interlocked.Increment(ref _pendingOperations);
    }

    internal void DecrementPendingOperations()
    {
        Interlocked.Decrement(ref _pendingOperations);
    }

    internal void RecordReceive(int transferredCount)
    {
        if (transferredCount <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastReadTicks, Environment.TickCount64);
        Interlocked.Add(ref _bytesReceived, transferredCount);
    }

    internal void RecordSend(int transferredCount)
    {
        if (transferredCount <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastWriteTicks, Environment.TickCount64);
        Interlocked.Add(ref _bytesSent, transferredCount);
    }

    /// <inheritdoc />
    public void Close()
    {
        _server.Disconnect(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.CloseSocket(ConnectionSocket);
        ReceiveEventArgs.Dispose();
        SendEventArgs.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidStateException()
    {
        throw new InvalidOperationException("The pooled client is in an invalid activation state.");
    }
}
