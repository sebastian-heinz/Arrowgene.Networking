using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// A generation-checked handle for a pooled client connection.
/// </summary>
public readonly struct ClientHandle : IEquatable<ClientHandle>
{
    private readonly Client _client;

    internal ClientHandle(Client client, uint generation, ushort clientId)
    {
        _client = client;
        Generation = generation;
        ClientId = clientId;
    }

    /// <summary>
    /// Gets the captured generation used to detect stale references.
    /// </summary>
    public uint Generation { get; }

    public ushort ClientId { get; }

    public long UniqueId => UniqueIdManager.Pack(ClientId, Generation);

    private Client Client
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Client? c = _client;
            if (c is null || c.Generation != Generation)
            {
                ThrowStaleException();
            }

            return c;
        }
    }

    /// <summary>
    /// Captures an immutable snapshot of the current client state.
    /// </summary>
    /// <returns>A <see cref="ClientSnapshot"/> representing the client's state at this moment.</returns>
    public bool TrySnapshot(out ClientSnapshot snapshot)
    {
        if (!TryGetClient(out Client client))
        {
            snapshot = default;
            return false;
        }

        snapshot = client.Snapshot();
        return true;
    }

    /// <summary>
    /// Tries to retrieve the underlying client if the handle is still valid.
    /// </summary>
    /// <param name="client">The active client instance.</param>
    /// <returns><c>true</c> if the handle is still valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetClient(out Client client)
    {
        if (_client is { } c && c.Generation == Generation)
        {
            client = c;
            return true;
        }

        client = null!;
        return false;
    }

    /// <summary>
    /// Gets the client identity used in logs.
    /// </summary>
    public string Identity => Client.Identity;

    /// <summary>
    /// Gets the remote IP address of the client.
    /// </summary>
    public IPAddress RemoteIpAddress => Client.RemoteIpAddress;

    /// <summary>
    /// Gets the remote TCP port of the client.
    /// </summary>
    public ushort Port => Client.Port;

    /// <summary>
    /// Gets the ordering lane assigned to the client.
    /// </summary>
    public int UnitOfOrder => Client.UnitOfOrder;

    /// <summary>
    /// Gets the tick count of the last successful receive operation.
    /// </summary>
    public long LastReadMs => Client.LastReadMs;

    /// <summary>
    /// Gets the tick count of the last successful send operation.
    /// </summary>
    public long LastWriteMs => Client.LastWriteMs;

    /// <summary>
    /// Gets the UTC timestamp when the client connected.
    /// </summary>
    public DateTime ConnectedAt => Client.ConnectedAt;

    /// <summary>
    /// Gets the total bytes received from the client.
    /// </summary>
    public ulong BytesReceived => Client.BytesReceived;

    /// <summary>
    /// Gets the total bytes sent to the client.
    /// </summary>
    public ulong BytesSent => Client.BytesSent;

    /// <summary>
    /// Gets the total bytes sent to the client.
    /// </summary>
    /// <remarks>This property is retained as a compatibility alias for <see cref="BytesSent"/>.</remarks>
    public ulong BytesSend => Client.BytesSent;

    /// <summary>
    /// Gets a value indicating whether the client is still connected and active.
    /// </summary>
    public bool IsAlive => Client.IsAlive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(byte[] data)
    {
        Client? client = _client;
        if (client is null)
        {
            return;
        }

        client.Send(this, data);
    }

    /// <summary>
    /// Disconnects the client.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Disconnect()
    {
        Client? client = _client;
        if (client is null)
        {
            return;
        }

        client.Disconnect(this);
    }

    /// <summary>
    /// Compares this handle with another handle.
    /// </summary>
    /// <param name="other">The handle to compare.</param>
    /// <returns><c>true</c> if both handles refer to the same client generation.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ClientHandle other)
    {
        return _client == other._client && Generation == other.Generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is ClientHandle other && Equals(other);
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
    public static bool operator ==(ClientHandle left, ClientHandle right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two handles.
    /// </summary>
    public static bool operator !=(ClientHandle left, ClientHandle right)
    {
        return !left.Equals(right);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStaleException()
    {
        throw new ObjectDisposedException(nameof(ClientHandle),
            "Handle is stale because the client has been recycled.");
    }
}