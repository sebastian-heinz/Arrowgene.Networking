using System;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Networking;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SaeaIoChannel : IDisposable
{
    private const int StatePooled = 0;
    private const int StateActive = 1;
    private const int StateClosing = 2;

    private readonly object _stateLock;
    private readonly SocketAsyncEventArgs _receiveEventArgs;
    private readonly SocketAsyncEventArgs _sendEventArgs;
    private readonly ISaeaIoChannelOwner _owner;
    private readonly BufferSlice _receiveBuffer;
    private readonly BufferSlice _sendBuffer;

    private Socket? _socket;
    private int _pendingOperations;
    private int _state;

    internal SaeaIoChannel(
        int sessionId,
        BufferSlice receiveBuffer,
        BufferSlice sendBuffer,
        ISaeaIoChannelOwner owner,
        int maxQueuedSendBytes)
    {
        if (maxQueuedSendBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedSendBytes));
        }

        SessionId = sessionId;
        _receiveBuffer = receiveBuffer;
        _sendBuffer = sendBuffer;
        _owner = owner;
        _stateLock = new object();
        SendQueue = new SendQueue(maxQueuedSendBytes);

        _receiveEventArgs = new SocketAsyncEventArgs();
        _receiveEventArgs.SetBuffer(receiveBuffer.Array, receiveBuffer.Offset, receiveBuffer.Length);
        _receiveEventArgs.Completed += ReceiveEventArgsOnCompleted;

        _sendEventArgs = new SocketAsyncEventArgs();
        _sendEventArgs.SetBuffer(sendBuffer.Array, sendBuffer.Offset, 0);
        _sendEventArgs.Completed += SendEventArgsOnCompleted;
        _state = StatePooled;
    }

    internal int SessionId { get; }

    internal SendQueue SendQueue { get; }

    internal int PendingOperations => Volatile.Read(ref _pendingOperations);

    internal bool IsReadyForRecycle => Volatile.Read(ref _state) != StateActive && PendingOperations == 0;

    internal void Attach(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        lock (_stateLock)
        {
            if (_state != StatePooled)
            {
                throw new InvalidOperationException("The channel is already active.");
            }

            _socket = socket;
            _state = StateActive;
        }
    }

    internal void StartReceiving()
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return;
        }

        StartReceive();
    }

    internal SendQueueEnqueueResult EnqueueSend(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return SendQueueEnqueueResult.Rejected;
        }

        SendQueueEnqueueResult result = SendQueue.Enqueue(data);
        if (result == SendQueueEnqueueResult.Started)
        {
            StartSend();
        }

        return result;
    }

    internal bool BeginShutdown()
    {
        Socket? socketToClose;
        lock (_stateLock)
        {
            if (_state != StateActive)
            {
                return false;
            }

            _state = StateClosing;
            socketToClose = _socket;
            _socket = null;
            SendQueue.ClearQueued();
        }

        Service.CloseSocket(socketToClose);
        return true;
    }

    internal void Reset()
    {
        if (!IsReadyForRecycle)
        {
            throw new InvalidOperationException("The channel still has active operations.");
        }

        lock (_stateLock)
        {
            if (_state == StateActive)
            {
                throw new InvalidOperationException("The channel is still active.");
            }

            _socket = null;
            _state = StatePooled;
            SendQueue.Reset();
            _receiveEventArgs.SetBuffer(_receiveBuffer.Array, _receiveBuffer.Offset, _receiveBuffer.Length);
            _sendEventArgs.SetBuffer(_sendBuffer.Array, _sendBuffer.Offset, 0);
        }
    }

    public void Dispose()
    {
        BeginShutdown();
        SendQueue.Reset();
        _receiveEventArgs.Dispose();
        _sendEventArgs.Dispose();
    }

    private void StartReceive()
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return;
        }

        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        IncrementPendingOperations();
        try
        {
            bool pending = socket.ReceiveAsync(_receiveEventArgs);
            if (!pending)
            {
                ProcessReceive(_receiveEventArgs);
            }
        }
        catch (ObjectDisposedException)
        {
            NotifyChannelClosed(SocketError.OperationAborted);
            CompletePendingOperation();
        }
        catch (SocketException exception)
        {
            NotifyChannelClosed(exception.SocketErrorCode);
            CompletePendingOperation();
        }
    }

    private void ProcessReceive(SocketAsyncEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.SocketError != SocketError.Success)
            {
                NotifyChannelClosed(eventArgs.SocketError);
                return;
            }

            if (eventArgs.BytesTransferred <= 0)
            {
                NotifyChannelClosed(SocketError.Success);
                return;
            }

            if (Volatile.Read(ref _state) != StateActive)
            {
                return;
            }

            BufferSlice receivedBuffer = new BufferSlice(
                _receiveBuffer.Array,
                _receiveBuffer.Offset,
                eventArgs.BytesTransferred);

            _owner.OnReceiveCompleted(SessionId, receivedBuffer);

            if (Volatile.Read(ref _state) == StateActive)
            {
                StartReceive();
            }
        }
        finally
        {
            CompletePendingOperation();
        }
    }

    private void StartSend()
    {
        if (Volatile.Read(ref _state) != StateActive)
        {
            return;
        }

        if (!SendQueue.TryGetChunk(_sendBuffer.Length, out byte[]? sourceBuffer, out int sourceOffset, out int count) || sourceBuffer is null)
        {
            return;
        }

        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        Buffer.BlockCopy(sourceBuffer, sourceOffset, _sendBuffer.Array, _sendBuffer.Offset, count);
        _sendEventArgs.SetBuffer(_sendBuffer.Offset, count);

        IncrementPendingOperations();
        try
        {
            bool pending = socket.SendAsync(_sendEventArgs);
            if (!pending)
            {
                ProcessSend(_sendEventArgs);
            }
        }
        catch (ObjectDisposedException)
        {
            NotifyChannelClosed(SocketError.OperationAborted);
            CompletePendingOperation();
        }
        catch (SocketException exception)
        {
            NotifyChannelClosed(exception.SocketErrorCode);
            CompletePendingOperation();
        }
    }

    private void ProcessSend(SocketAsyncEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.SocketError != SocketError.Success)
            {
                NotifyChannelClosed(eventArgs.SocketError);
                return;
            }

            if (eventArgs.BytesTransferred <= 0)
            {
                NotifyChannelClosed(SocketError.Success);
                return;
            }

            _owner.OnSendCompleted(SessionId, eventArgs.BytesTransferred);

            bool hasMoreData = SendQueue.CompleteChunk(eventArgs.BytesTransferred);
            if (hasMoreData && Volatile.Read(ref _state) == StateActive)
            {
                StartSend();
            }
        }
        finally
        {
            CompletePendingOperation();
        }
    }

    private void ReceiveEventArgsOnCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        ProcessReceive(eventArgs);
    }

    private void SendEventArgsOnCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        ProcessSend(eventArgs);
    }

    private void NotifyChannelClosed(SocketError socketError)
    {
        _owner.OnChannelClosed(SessionId, socketError);
    }

    private void IncrementPendingOperations()
    {
        Interlocked.Increment(ref _pendingOperations);
    }

    private void CompletePendingOperation()
    {
        int remainingOperations = Interlocked.Decrement(ref _pendingOperations);
        if (remainingOperations < 0)
        {
            Interlocked.Exchange(ref _pendingOperations, 0);
            remainingOperations = 0;
        }

        if (remainingOperations == 0 && IsReadyForRecycle)
        {
            _owner.OnChannelReadyForRecycle(SessionId);
        }
    }
}
