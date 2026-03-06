using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SaeaSession
{
    private readonly object _stateLock;

    private bool _isConnected;
    private bool _isPooled;
    private string _identity;
    private IPAddress _remoteIpAddress;
    private ushort _remotePort;
    private DateTimeOffset _connectedAt;
    private long _lastReceiveTicks;
    private long _lastSendTicks;
    private long _bytesReceived;
    private long _bytesSent;

    internal SaeaSession(int sessionId, SaeaServer server, SaeaIoChannel channel)
    {
        SessionId = sessionId;
        Server = server;
        Channel = channel;
        _stateLock = new object();
        _isConnected = false;
        _isPooled = true;
        _identity = string.Empty;
        _remoteIpAddress = IPAddress.None;
        _connectedAt = DateTimeOffset.MinValue;
    }

    internal int SessionId { get; }

    internal SaeaServer Server { get; }

    internal SaeaIoChannel Channel { get; }

    internal int Generation { get; private set; }

    internal string Identity => _identity;

    internal IPAddress RemoteIpAddress => _remoteIpAddress;

    internal ushort RemotePort => _remotePort;

    internal DateTimeOffset ConnectedAt => _connectedAt;

    internal long LastReceiveTicks => Volatile.Read(ref _lastReceiveTicks);

    internal long LastSendTicks => Volatile.Read(ref _lastSendTicks);

    internal ulong BytesReceived => unchecked((ulong)Interlocked.Read(ref _bytesReceived));

    internal ulong BytesSent => unchecked((ulong)Interlocked.Read(ref _bytesSent));

    internal bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _isConnected;
            }
        }
    }

    internal void Initialize(Socket socket, int generation)
    {
        ArgumentNullException.ThrowIfNull(socket);

        lock (_stateLock)
        {
            if (_isConnected || !_isPooled)
            {
                throw new InvalidOperationException("The session is not in a pooled state.");
            }

            _isConnected = true;
            _isPooled = false;
            Generation = generation;
        }

        if (socket.RemoteEndPoint is IPEndPoint remoteEndPoint)
        {
            _remoteIpAddress = remoteEndPoint.Address;
            _remotePort = (ushort)remoteEndPoint.Port;
        }
        else
        {
            _remoteIpAddress = IPAddress.None;
            _remotePort = 0;
        }

        _identity = $"[{_remoteIpAddress}:{_remotePort}]";
        _connectedAt = DateTimeOffset.UtcNow;

        long now = Environment.TickCount64;
        Volatile.Write(ref _lastReceiveTicks, now);
        Volatile.Write(ref _lastSendTicks, now);
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _bytesSent, 0);

        Channel.Attach(socket);
    }

    internal bool TryBeginShutdown()
    {
        lock (_stateLock)
        {
            if (!_isConnected)
            {
                return false;
            }

            _isConnected = false;
            return true;
        }
    }

    internal bool TryMarkPooled()
    {
        lock (_stateLock)
        {
            if (_isConnected || _isPooled)
            {
                return false;
            }

            _isPooled = true;
            _identity = string.Empty;
            _remoteIpAddress = IPAddress.None;
            _remotePort = 0;
            _connectedAt = DateTimeOffset.MinValue;
            return true;
        }
    }

    internal void RecordReceive(int bytesTransferred)
    {
        if (bytesTransferred <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastReceiveTicks, Environment.TickCount64);
        Interlocked.Add(ref _bytesReceived, bytesTransferred);
    }

    internal void RecordSend(int bytesTransferred)
    {
        if (bytesTransferred <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastSendTicks, Environment.TickCount64);
        Interlocked.Add(ref _bytesSent, bytesTransferred);
    }
}
