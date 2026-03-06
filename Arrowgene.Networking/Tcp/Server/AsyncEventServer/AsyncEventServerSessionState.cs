using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

internal sealed class AsyncEventServerSessionState : IDisposable
{
    private const int StatePooled = 0;
    private const int StateActive = 1;
    private const int StateClosing = 2;

    private readonly object _stateLock;
    private readonly AsyncEventServer _server;
    private readonly SocketAsyncEventArgs _receiveEventArgs;
    private readonly SocketAsyncEventArgs _sendEventArgs;
    private readonly AsyncEventServerSendQueue _sendQueue;
    private readonly int _bufferSize;
    private readonly int _sendBufferOffset;
    private Socket? _socket;
    private string _identity;
    private IPAddress _remoteAddress;
    private ushort _remotePort;
    private int _orderingLane;
    private int _generation;
    private long _connectedAtUtcTicks;
    private long _lastReceiveTick;
    private long _lastSendTick;
    private long _bytesReceived;
    private long _bytesSent;
    private int _pendingOperations;
    private int _state;
    private int _disconnectError;

    internal AsyncEventServerSessionState(
        int sessionId,
        AsyncEventServer server,
        byte[] buffer,
        int receiveBufferOffset,
        int sendBufferOffset,
        int bufferSize,
        int maxQueuedSendBytes)
    {
        SessionId = sessionId;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _stateLock = new object();
        _sendQueue = new AsyncEventServerSendQueue(maxQueuedSendBytes);
        _bufferSize = bufferSize;
        _sendBufferOffset = sendBufferOffset;
        _identity = string.Empty;
        _remoteAddress = IPAddress.None;
        ActiveIndex = -1;
        _state = StatePooled;
        _disconnectError = (int)SocketError.Success;

        _receiveEventArgs = new SocketAsyncEventArgs();
        _receiveEventArgs.SetBuffer(buffer, receiveBufferOffset, bufferSize);
        _receiveEventArgs.Completed += OnReceiveCompleted;

        _sendEventArgs = new SocketAsyncEventArgs();
        _sendEventArgs.SetBuffer(buffer, sendBufferOffset, 0);
        _sendEventArgs.Completed += OnSendCompleted;
    }

    internal int SessionId { get; }

    internal int ActiveIndex { get; set; }

    internal string Identity => _identity;

    internal IPAddress RemoteAddress => _remoteAddress;

    internal ushort RemotePort => _remotePort;

    internal int OrderingLane => Volatile.Read(ref _orderingLane);

    internal int Generation => Volatile.Read(ref _generation);

    internal DateTime ConnectedAtUtc => new DateTime(Volatile.Read(ref _connectedAtUtcTicks), DateTimeKind.Utc);

    internal long LastReceiveTick => Volatile.Read(ref _lastReceiveTick);

    internal long LastSendTick => Volatile.Read(ref _lastSendTick);

    internal long BytesReceived => Interlocked.Read(ref _bytesReceived);

    internal long BytesSent => Interlocked.Read(ref _bytesSent);

    internal int PendingOperations => Volatile.Read(ref _pendingOperations);

    internal bool IsConnected => Volatile.Read(ref _state) == StateActive;

    internal bool IsReadyForRecycle => Volatile.Read(ref _state) == StateClosing && PendingOperations == 0;

    internal SocketError DisconnectError => (SocketError)Volatile.Read(ref _disconnectError);

    internal AsyncEventServerConnection CreateConnection()
    {
        return new AsyncEventServerConnection(this, Generation);
    }

    internal string GetIdentity(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _identity;
        }
    }

    internal IPAddress GetRemoteAddress(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _remoteAddress;
        }
    }

    internal ushort GetRemotePort(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _remotePort;
        }
    }

    internal int GetOrderingLane(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _orderingLane;
        }
    }

    internal DateTime GetConnectedAtUtc(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return new DateTime(_connectedAtUtcTicks, DateTimeKind.Utc);
        }
    }

    internal long GetLastReceiveTick(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _lastReceiveTick;
        }
    }

    internal long GetLastSendTick(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _lastSendTick;
        }
    }

    internal long GetBytesReceived(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _bytesReceived;
        }
    }

    internal long GetBytesSent(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _bytesSent;
        }
    }

    internal bool GetIsConnected(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            return _state == StateActive;
        }
    }

    internal void Initialize(Socket socket, int orderingLane, int generation)
    {
        if (socket is null)
        {
            throw new ArgumentNullException(nameof(socket));
        }

        lock (_stateLock)
        {
            if (_state != StatePooled)
            {
                throw new InvalidOperationException("The session is not available for activation.");
            }

            _socket = socket;
            _orderingLane = orderingLane;
            _generation = generation;

            IPEndPoint? remoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
            if (remoteEndPoint is null)
            {
                _remoteAddress = IPAddress.None;
                _remotePort = 0;
                _identity = "[unknown]";
            }
            else
            {
                _remoteAddress = remoteEndPoint.Address;
                _remotePort = checked((ushort)remoteEndPoint.Port);
                _identity = "[" + remoteEndPoint.Address + ":" + remoteEndPoint.Port + "]";
            }

            long currentTick = Environment.TickCount64;
            _connectedAtUtcTicks = DateTime.UtcNow.Ticks;
            _lastReceiveTick = currentTick;
            _lastSendTick = currentTick;
            _bytesReceived = 0;
            _bytesSent = 0;
            _pendingOperations = 0;
            _disconnectError = (int)SocketError.Success;
            _sendQueue.Reset();
            _state = StateActive;
        }
    }

    internal void StartIo()
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return;
        }

        StartReceiveLoop();
    }

    internal void Send(int expectedGeneration, byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        Send(expectedGeneration, payload.AsSpan());
    }

    internal void Send(int expectedGeneration, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        bool startSend;
        bool overflow;

        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            if (_state != StateActive)
            {
                throw new ObjectDisposedException(nameof(AsyncEventServerConnection), "The connection is no longer open for sending.");
            }

            bool queued = _sendQueue.EnqueueCopy(payload, out startSend, out overflow);
            if (!queued)
            {
                if (overflow)
                {
                    _server.Disconnect(this, SocketError.NoBufferSpaceAvailable);
                }

                return;
            }

            if (startSend)
            {
                StartSendLoop();
            }
        }
    }

    internal void Close(int expectedGeneration)
    {
        lock (_stateLock)
        {
            ValidateHandle(expectedGeneration);
            _server.Disconnect(this, SocketError.OperationAborted);
        }
    }

    internal bool IsHandleValid(int generation)
    {
        return Volatile.Read(ref _generation) == generation && Volatile.Read(ref _state) != StatePooled;
    }

    internal bool TryBeginShutdown(SocketError error)
    {
        lock (_stateLock)
        {
            if (_state != StateActive)
            {
                return false;
            }

            _state = StateClosing;
            _disconnectError = (int)error;
            return true;
        }
    }

    internal void DropQueuedSends()
    {
        _sendQueue.DropBacklog();
    }

    internal void ShutdownSocket()
    {
        Socket? socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }

        try
        {
            socket.Close();
        }
        catch
        {
        }
    }

    internal bool TryMarkPooled()
    {
        lock (_stateLock)
        {
            if (_state != StateClosing || _pendingOperations != 0)
            {
                return false;
            }

            _state = StatePooled;
            return true;
        }
    }

    internal void ResetForPool()
    {
        _socket = null;
        _identity = string.Empty;
        _remoteAddress = IPAddress.None;
        _remotePort = 0;
        _orderingLane = 0;
        _connectedAtUtcTicks = 0;
        _lastReceiveTick = 0;
        _lastSendTick = 0;
        _bytesReceived = 0;
        _bytesSent = 0;
        _disconnectError = (int)SocketError.Success;
        _pendingOperations = 0;
        ActiveIndex = -1;
        _sendQueue.Reset();
        _sendEventArgs.SetBuffer(_sendBufferOffset, 0);
    }

    public void Dispose()
    {
        ShutdownSocket();
        _receiveEventArgs.Dispose();
        _sendEventArgs.Dispose();
    }

    private void StartReceiveLoop()
    {
        while (true)
        {
            Socket? socket = _socket;
            if (socket is null || Volatile.Read(ref _state) != StateActive)
            {
                return;
            }

            Interlocked.Increment(ref _pendingOperations);

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.ReceiveAsync(_receiveEventArgs);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, SocketError.OperationAborted);
                return;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, SocketError.NotConnected);
                return;
            }
            catch (SocketException exception)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, exception.SocketErrorCode);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueReceiving = false;
            try
            {
                continueReceiving = ProcessReceiveCompletion(_receiveEventArgs);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingOperations);
            }

            if (!continueReceiving)
            {
                _server.TryRecycleSession(this);
                return;
            }
        }
    }

    private void StartSendLoop()
    {
        while (true)
        {
            Socket? socket = _socket;
            if (socket is null)
            {
                return;
            }

            byte[]? sendBuffer = _sendEventArgs.Buffer;
            if (sendBuffer is null)
            {
                _server.Disconnect(this, SocketError.NoBufferSpaceAvailable);
                return;
            }

            if (!_sendQueue.TryCopyNextChunk(sendBuffer, _sendBufferOffset, _bufferSize, out int chunkSize))
            {
                return;
            }

            Interlocked.Increment(ref _pendingOperations);

            bool willRaiseEvent;
            try
            {
                _sendEventArgs.SetBuffer(_sendBufferOffset, chunkSize);
                willRaiseEvent = socket.SendAsync(_sendEventArgs);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, SocketError.OperationAborted);
                return;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, SocketError.NotConnected);
                return;
            }
            catch (SocketException exception)
            {
                Interlocked.Decrement(ref _pendingOperations);
                _server.Disconnect(this, exception.SocketErrorCode);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueSending = false;
            try
            {
                continueSending = ProcessSendCompletion(_sendEventArgs);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingOperations);
            }

            if (!continueSending)
            {
                _server.TryRecycleSession(this);
                return;
            }
        }
    }

    private bool ProcessReceiveCompletion(SocketAsyncEventArgs eventArgs)
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return false;
        }

        SocketError socketError = eventArgs.SocketError;
        if (socketError != SocketError.Success)
        {
            _server.Disconnect(this, socketError);
            return false;
        }

        int bytesTransferred = eventArgs.BytesTransferred;
        if (bytesTransferred <= 0)
        {
            _server.Disconnect(this, SocketError.Success);
            return false;
        }

        byte[]? sourceBuffer = eventArgs.Buffer;
        if (sourceBuffer is null)
        {
            _server.Disconnect(this, SocketError.NoBufferSpaceAvailable);
            return false;
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(bytesTransferred);
        Buffer.BlockCopy(sourceBuffer, eventArgs.Offset, payload, 0, bytesTransferred);
        RecordReceive(bytesTransferred);
        _server.OnPayloadReceived(this, payload);
        return Volatile.Read(ref _state) == StateActive;
    }

    private bool ProcessSendCompletion(SocketAsyncEventArgs eventArgs)
    {
        SocketError socketError = eventArgs.SocketError;
        if (socketError != SocketError.Success)
        {
            _server.Disconnect(this, socketError);
            return false;
        }

        int bytesTransferred = eventArgs.BytesTransferred;
        if (bytesTransferred <= 0)
        {
            _server.Disconnect(this, SocketError.Success);
            return false;
        }

        RecordSend(bytesTransferred);
        bool hasMore = _sendQueue.CompleteSend(bytesTransferred);
        return hasMore && Volatile.Read(ref _state) == StateActive;
    }

    private void RecordReceive(int bytesTransferred)
    {
        Volatile.Write(ref _lastReceiveTick, Environment.TickCount64);
        Interlocked.Add(ref _bytesReceived, bytesTransferred);
    }

    private void RecordSend(int bytesTransferred)
    {
        Volatile.Write(ref _lastSendTick, Environment.TickCount64);
        Interlocked.Add(ref _bytesSent, bytesTransferred);
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        bool continueReceiving = false;
        try
        {
            continueReceiving = ProcessReceiveCompletion(eventArgs);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOperations);
        }

        if (continueReceiving)
        {
            StartReceiveLoop();
            return;
        }

        _server.TryRecycleSession(this);
    }

    private void OnSendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        bool continueSending = false;
        try
        {
            continueSending = ProcessSendCompletion(eventArgs);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOperations);
        }

        if (continueSending)
        {
            StartSendLoop();
            return;
        }

        _server.TryRecycleSession(this);
    }

    private void ValidateHandle(int expectedGeneration)
    {
        if (_generation != expectedGeneration || _state == StatePooled)
        {
            throw new ObjectDisposedException(nameof(AsyncEventServerConnection), "The connection has been closed or recycled.");
        }
    }
}
