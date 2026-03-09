using System;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.Server;

/// <summary>
/// A generation-checked handle for an <see cref="AsyncEventClient"/>.
/// </summary>
public readonly struct AsyncEventClientHandle : IEquatable<AsyncEventClientHandle>
{
    private readonly AsyncEventClient _client;
    private readonly AsyncEventServer _server;

    internal AsyncEventClientHandle(AsyncEventServer server, AsyncEventClient client)
    {
        _server = server;
        _client = client;
        Generation = _client.Generation;
    }

    /// <summary>
    /// Gets the captured generation used to detect stale references.
    /// </summary>
    public uint Generation { get; }

    private AsyncEventClient Client
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_client.Generation != Generation)
            {
                ThrowStaleException();
            }

            return _client;
        }
    }

    /// <summary>
    /// Tries to retrieve the underlying client if the handle is still valid.
    /// </summary>
    /// <param name="client">The active client instance.</param>
    /// <returns><c>true</c> if the handle is still valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetClient(out AsyncEventClient client)
    {
        if (_client.Generation != Generation)
        {
            client = null!;
            return false;
        }

        client = _client;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool EqualsClient(AsyncEventClient other)
    {
        return other == _client;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool EqualsClientGeneration(AsyncEventClient client, uint generation)
    {
        return _client == client && Generation == generation;
    }

    public string Identity => Client.Identity;

    public IPAddress RemoteIpAddress => Client.RemoteIpAddress;

    public ushort Port => Client.Port;

    public int UnitOfOrder => Client.UnitOfOrder;

    public long LastReadMs => Client.LastReadMs;

    public long LastWriteMs => Client.LastWriteMs;

    public DateTime ConnectedAt => Client.ConnectedAt;

    public ulong BytesReceived => Client.BytesReceived;

    /// <summary>
    /// Gets the total bytes sent by the client.
    /// </summary>
    public ulong BytesSent => Client.BytesSent;

    public ulong BytesSend => Client.BytesSent;

    public bool IsAlive => Client.IsAlive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(byte[] data)
    {
        _server.Send(this, data);
    }

    public void Close()
    {
        _server.Disconnect(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(AsyncEventClientHandle other)
    {
        return _client == other._client && Generation == other.Generation;
    }

    public override bool Equals(object? obj)
    {
        return obj is AsyncEventClientHandle other && Equals(other);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return HashCode.Combine(_client, Generation);
    }

    /// <summary>
    /// Compares two handles.
    /// </summary>
    public static bool operator ==(AsyncEventClientHandle left, AsyncEventClientHandle right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two handles.
    /// </summary>
    public static bool operator !=(AsyncEventClientHandle left, AsyncEventClientHandle right)
    {
        return !left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStaleException()
    {
        throw new ObjectDisposedException(nameof(AsyncEventClientHandle),
            "Handle is stale because the client has been recycled.");
    }
}