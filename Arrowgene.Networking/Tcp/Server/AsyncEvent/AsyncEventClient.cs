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
    public long LastReadTicks { get; internal set; }
    public long LastWriteTicks { get; internal set; }
    public DateTime ConnectedAt { get; private set; }
    public ulong BytesReceived { get; internal set; }
    public ulong BytesSend { get; internal set; }

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
        LastReadTicks = now;
        LastWriteTicks = now;
        ConnectedAt = DateTime.Now;
        BytesReceived = 0;
        BytesSend = 0;

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