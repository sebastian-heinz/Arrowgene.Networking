using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

public class AsyncEventClient : ITcpSocket
{
    public int ClientId { get; private set; }
    public string Identity { get; private set; }
    public int RunGeneration { get; private set; }
    public int Generation { get; private set; }
    public IPAddress RemoteIpAddress { get; private set; }
    public ushort Port { get; private set; }
    public int UnitOfOrder { get; private set; }
    public Socket Socket { get; private set; }
    public long LastReadTicks => Volatile.Read(ref _lastReadTicks);
    public long LastWriteTicks => Volatile.Read(ref _lastWriteTicks);
    public DateTime ConnectedAt { get; private set; }
    public ulong BytesReceived => unchecked((ulong)Interlocked.Read(ref _bytesReceived));
    public ulong BytesSend => unchecked((ulong)Interlocked.Read(ref _bytesSend));

    public bool IsAlive
    {
        get
        {
            lock (_lock)
            {
                return _isAlive;
            }
        }
    }

    internal SocketAsyncEventArgs ReadEventArgs { get; }
    internal SocketAsyncEventArgs WriteEventArgs { get; }
    internal AsyncEventWriteState WriteState { get; set; }

    private bool _isAlive;
    private bool _isInPool;
    private readonly AsyncEventServer _server;
    private int _pendingOperations;
    private long _lastReadTicks;
    private long _lastWriteTicks;
    private long _bytesReceived;
    private long _bytesSend;

    private readonly object _lock;

    public AsyncEventClient(
        int clientId,
        AsyncEventServer server,
        SocketAsyncEventArgs readEventArgs,
        SocketAsyncEventArgs writeEventArgs
    )
    {
        ClientId = clientId;
        _isAlive = false;
        _isInPool = true;
        Generation = 0;
        _server = server;
        WriteState = new AsyncEventWriteState();
        ReadEventArgs = readEventArgs;
        WriteEventArgs = writeEventArgs;
        _lock = new object();
        _pendingOperations = 0;
    }

    internal int PendingOperations => Volatile.Read(ref _pendingOperations);

    public void Send(byte[] data)
    {
        Send(this, data);
    }

    public void Send<T>(T socket, byte[] data) where T : ITcpSocket
    {
        _server.Send(socket, data);
    }

    internal void Initialize(
        Socket socket,
        int unitOfOrder,
        int runGeneration,
        int clientGeneration,
        AsyncEventClientHandle handle
    )
    {
        lock (_lock)
        {
            if (_isAlive)
            {
                ThrowInvalidStateException();
            }

            if (!_isInPool)
            {
                ThrowInvalidStateException();
            }

            _isAlive = true;
            _isInPool = false;
        }

        Socket = socket;
        UnitOfOrder = unitOfOrder;
        RunGeneration = runGeneration;
        Generation = clientGeneration;

        ReadEventArgs.UserToken = handle;
        WriteEventArgs.UserToken = handle;

        long now = Environment.TickCount64;
        Volatile.Write(ref _lastReadTicks, now);
        Volatile.Write(ref _lastWriteTicks, now);
        ConnectedAt = DateTime.Now;
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _bytesSend, 0);

        RemoteIpAddress = null;
        Port = 0;
        if (Socket.RemoteEndPoint is IPEndPoint ipEndPoint)
        {
            RemoteIpAddress = ipEndPoint.Address;
            Port = (ushort)ipEndPoint.Port;
        }

        Identity = $"[{RemoteIpAddress}:{Port}]";

        WriteState.Reset();
        Interlocked.Exchange(ref _pendingOperations, 0);
    }

    internal bool TryBeginShutdown()
    {
        lock (_lock)
        {
            if (!_isAlive)
            {
                return false;
            }

            Close();
            _isAlive = false;
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
        Interlocked.Add(ref _bytesSend, transferredCount);
    }

    internal bool TryMarkPooled()
    {
        lock (_lock)
        {
            if (_isInPool)
            {
                return false;
            }

            if (_isAlive)
            {
                return false;
            }

            _isInPool = true;
            return true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (!_isAlive)
            {
                return;
            }
        }

        Socket socket = Socket;
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignored
        }

        try
        {
            socket.Close();
        }
        catch
        {
            // ignored
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidStateException() =>
        throw new InvalidOperationException("Initial State is invalid.");
}
