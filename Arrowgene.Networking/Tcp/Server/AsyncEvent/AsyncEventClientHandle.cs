using System;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

public readonly struct AsyncEventClientHandle(AsyncEventClient client, int runGeneration, int generation)
    : ITcpSocket, IEquatable<AsyncEventClientHandle>
{
    private readonly AsyncEventClient _client = client;

    public readonly int RunGeneration = runGeneration;
    public readonly int Generation = generation;

    private AsyncEventClient Client
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_client.Generation != Generation)
                ThrowStaleException();
            return _client;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetClient(out AsyncEventClient client)
    {
        if (_client.Generation == Generation)
        {
            client = _client;
            return true;
        }

        client = null!;
        return false;
    }
    
    public string Identity => Client.Identity;

    public IPAddress RemoteIpAddress => Client.RemoteIpAddress;

    public ushort Port => Client.Port;

    public int UnitOfOrder => Client.UnitOfOrder;

    public long LastReadTicks => Client.LastReadTicks;

    public long LastWriteTicks => Client.LastWriteTicks;

    public DateTime ConnectedAt => Client.ConnectedAt;

    public ulong BytesReceived => Client.BytesReceived;

    public ulong BytesSend => Client.BytesSend;

    public bool IsAlive => Client.IsAlive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(byte[] data)
    {
        Client.Send(this, data);
    }

    public void Close()
    {
        Client.Close();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(AsyncEventClientHandle other) =>
        _client == other._client &&
        RunGeneration == other.RunGeneration &&
        Generation == other.Generation;

    public override bool Equals(object? obj) => obj is AsyncEventClientHandle other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => HashCode.Combine(_client, RunGeneration, Generation);

    public static bool operator ==(AsyncEventClientHandle left, AsyncEventClientHandle right) => left.Equals(right);
    public static bool operator !=(AsyncEventClientHandle left, AsyncEventClientHandle right) => !left.Equals(right);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStaleException() =>
        throw new ObjectDisposedException("Handle is stale: The object has been recycled.");
}