using System;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

/// <summary>
/// A generation-checked handle for an <see cref="AsyncEventClient"/>.
/// </summary>
public readonly struct AsyncEventClientHandle(AsyncEventClient client, int generation)
    : ITcpSocket, IEquatable<AsyncEventClientHandle>
{
    private readonly AsyncEventClient? _client = client;

    /// <summary>
    /// Gets the captured generation used to detect stale references.
    /// </summary>
    public readonly int Generation = generation;

    private AsyncEventClient Client
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            AsyncEventClient? client = _client;
            if (client is null || !client.IsHandleValid(Generation))
            {
                ThrowStaleException();
            }

            return client!;
        }
    }

    /// <summary>
    /// Tries to retrieve the underlying client if the handle is still valid.
    /// </summary>
    /// <param name="client">The active client instance.</param>
    /// <returns><c>true</c> if the handle is still valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetClient(out AsyncEventClient client)
    {
        AsyncEventClient? current = _client;
        if (current is not null && current.IsHandleValid(Generation))
        {
            client = current;
            return true;
        }

        client = null!;
        return false;
    }

    /// <inheritdoc />
    public string Identity => Client.Identity;

    /// <inheritdoc />
    public IPAddress RemoteIpAddress => Client.RemoteIpAddress;

    /// <inheritdoc />
    public ushort Port => Client.Port;

    /// <inheritdoc />
    public int UnitOfOrder => Client.UnitOfOrder;

    /// <inheritdoc />
    public long LastReadTicks => Client.LastReadTicks;

    /// <inheritdoc />
    public long LastWriteTicks => Client.LastWriteTicks;

    /// <inheritdoc />
    public DateTime ConnectedAt => Client.ConnectedAt;

    /// <inheritdoc />
    public ulong BytesReceived => Client.BytesReceived;

    /// <summary>
    /// Gets the total bytes sent by the client.
    /// </summary>
    public ulong BytesSent => Client.BytesSent;

    /// <inheritdoc />
    public ulong BytesSend => Client.BytesSent;

    /// <inheritdoc />
    public bool IsAlive => Client.IsAlive;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(byte[] data)
    {
        Client.Send(this, data);
    }

    /// <inheritdoc />
    public void Close()
    {
        Client.Close();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(AsyncEventClientHandle other)
    {
        return _client == other._client && Generation == other.Generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AsyncEventClientHandle other && Equals(other);
    }

    /// <inheritdoc />
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
        throw new ObjectDisposedException(nameof(AsyncEventClientHandle), "Handle is stale because the client has been recycled.");
    }
}
