using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Represents one pooled client slot managed by <see cref="Server"/>.
/// </summary>
internal sealed class Client : IDisposable
{
    private readonly object _sync;
    private readonly SendQueue _sendQueue;
    private Socket? _socket;
    private bool _isAlive;
    private bool _isInPool;
    private long _lastReadMs;
    private long _lastWriteMs;
    private long _bytesReceived;
    private long _bytesSent;
    private int _pendingOperations;
    private uint _generation;
    private bool _disconnectCleanupQueued;


    internal Client(
        int clientId,
        SocketAsyncEventArgs receiveEventArgs,
        SocketAsyncEventArgs sendEventArgs,
        int maxQueuedSendBytes
    )
    {
        ClientId = clientId;
        _sync = new object();
        _sendQueue = new SendQueue(maxQueuedSendBytes);
        ReceiveEventArgs = receiveEventArgs;
        SendEventArgs = sendEventArgs;
        ReceiveEventArgs.UserToken = null;
        SendEventArgs.UserToken = null;
        _isInPool = true;
        Identity = "[Unknown Client]";
        RemoteIpAddress = IPAddress.None;
        ConnectedAt = DateTime.MinValue;
        _generation = 0;
    }

    /// <summary>
    /// Gets the stable pooled client slot identifier.
    /// </summary>
    internal int ClientId { get; }

    /// <summary>
    /// Gets the current connection identity.
    /// </summary>
    internal string Identity { get; private set; }

    /// <summary>
    /// Gets the client generation used by <see cref="ClientHandle"/>.
    /// </summary>
    internal uint Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    internal IPAddress RemoteIpAddress { get; private set; }
    internal ushort Port { get; private set; }

    internal int UnitOfOrder { get; private set; }

    /// <summary>
    /// Gets the last successful receive tick.
    /// </summary>
    internal long LastReadMs => Volatile.Read(ref _lastReadMs);

    /// <summary>
    /// Gets the last successful send tick.
    /// </summary>
    internal long LastWriteMs => Volatile.Read(ref _lastWriteMs);

    internal DateTime ConnectedAt { get; private set; }

    internal ulong BytesReceived => unchecked((ulong)Interlocked.Read(ref _bytesReceived));

    /// <summary>
    /// Gets the total bytes sent.
    /// </summary>
    internal ulong BytesSent => unchecked((ulong)Interlocked.Read(ref _bytesSent));


    internal bool IsAlive
    {
        get
        {
            lock (_sync)
            {
                return _isAlive;
            }
        }
    }

    internal SocketAsyncEventArgs ReceiveEventArgs { get; }

    internal SocketAsyncEventArgs SendEventArgs { get; }

    internal int PendingOperations => Volatile.Read(ref _pendingOperations);

    public ClientSnapshot Snapshot()
    {
        ClientSnapshot snapshot;
        lock (_sync)
        {
            snapshot = new ClientSnapshot(
                ClientId,
                _generation,
                Identity,
                RemoteIpAddress,
                Port,
                _isAlive,
                ConnectedAt,
                Volatile.Read(ref _lastReadMs),
                Volatile.Read(ref _lastWriteMs),
                unchecked((ulong)Interlocked.Read(ref _bytesReceived)),
                unchecked((ulong)Interlocked.Read(ref _bytesSent)),
                Volatile.Read(ref _pendingOperations),
                UnitOfOrder
            );
        }

        return snapshot;
    }

    internal void Activate(Socket socket, int unitOfOrder)
    {
        lock (_sync)
        {
            if (_isAlive || !_isInPool)
            {
                throw new InvalidOperationException("The pooled client is in an invalid activation state.");
            }

            if (socket.RemoteEndPoint is not IPEndPoint remoteEndPoint)
            {
                throw new InvalidOperationException("RemoteEndPoint is not an IPEndPoint.");
            }

            _sendQueue.Reset();

            _socket = socket;
            UnitOfOrder = unitOfOrder;

            long now = Environment.TickCount64;
            Volatile.Write(ref _lastReadMs, now);
            Volatile.Write(ref _lastWriteMs, now);
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _pendingOperations, 0);

            _generation = unchecked(_generation + 1);
            ConnectedAt = DateTime.UtcNow;

            RemoteIpAddress = remoteEndPoint.Address;
            Port = checked((ushort)remoteEndPoint.Port);
            Identity = $"[{remoteEndPoint.Address}:{remoteEndPoint.Port}]";

            _isInPool = false;
            _isAlive = true;
            _disconnectCleanupQueued = false;
        }
    }

    internal void Close()
    {
        Socket? socketToClose;
        lock (_sync)
        {
            if (!_isAlive)
            {
                return;
            }

            _isAlive = false;
            socketToClose = _socket;
            _socket = null;
        }

        Service.CloseSocket(socketToClose);
    }

    internal bool CanReturnToPool()
    {
        lock (_sync)
        {
            bool hasPendingOperations = Volatile.Read(ref _pendingOperations) > 0;

            if (_isInPool || _isAlive || hasPendingOperations)
            {
                return false;
            }

            _isInPool = true;
            return true;
        }
    }

    internal bool TryMarkDisconnectCleanupQueued()
    {
        lock (_sync)
        {
            if (_disconnectCleanupQueued || _isInPool || _isAlive)
            {
                return false;
            }

            _disconnectCleanupQueued = true;
            return true;
        }
    }

    internal bool TryBeginSocketOperation(uint generation, out Socket socket)
    {
        lock (_sync)
        {
            if (_generation != generation || !_isAlive || _isInPool || _socket is not { } connectionSocket)
            {
                socket = null!;
                return false;
            }

            IncrementPendingOperations();
            socket = connectionSocket;
            return true;
        }
    }

    internal bool TryPrepareSendChunk(uint generation, int maxChunkSize, out int chunkSize)
    {
        lock (_sync)
        {
            if (_generation != generation || !_isAlive || _isInPool)
            {
                chunkSize = 0;
                return false;
            }

            byte[]? sendBuffer = SendEventArgs.Buffer;
            if (sendBuffer is null)
            {
                chunkSize = 0;
                return false;
            }

            bool send = _sendQueue.CopyNextChunk(sendBuffer, SendEventArgs.Offset, maxChunkSize, out chunkSize);
            if (send)
            {
                SendEventArgs.SetBuffer(SendEventArgs.Offset, chunkSize);
            }

            return send;
        }
    }


    internal bool QueueSend(uint generation, byte[] data, out bool startSend, out bool queueOverflow)
    {
        lock (_sync)
        {
            if (_generation != generation || !_isAlive || _isInPool)
            {
                startSend = false;
                queueOverflow = false;
                return false;
            }

            return _sendQueue.Enqueue(data, out startSend, out queueOverflow);
        }
    }

    internal bool CompleteSend(int transferredCount)
    {
        return _sendQueue.CompleteSend(transferredCount);
    }

    internal void ResetForPool()
    {
        lock (_sync)
        {
            _generation = unchecked(_generation + 1);
            _socket = null;
            _isAlive = false;
            Identity = "[Unknown Client]";
            RemoteIpAddress = IPAddress.None;
            Port = 0;
            UnitOfOrder = 0;
            ConnectedAt = DateTime.MinValue;

            Volatile.Write(ref _lastReadMs, 0);
            Volatile.Write(ref _lastWriteMs, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _pendingOperations, 0);
            _disconnectCleanupQueued = false;
            _sendQueue.Reset();
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

        Volatile.Write(ref _lastReadMs, Environment.TickCount64);
        Interlocked.Add(ref _bytesReceived, transferredCount);
    }

    internal void RecordSend(int transferredCount)
    {
        if (transferredCount <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastWriteMs, Environment.TickCount64);
        Interlocked.Add(ref _bytesSent, transferredCount);
    }

    public void Dispose()
    {
        Service.CloseSocket(_socket);
        ReceiveEventArgs.Dispose();
        SendEventArgs.Dispose();
    }
}