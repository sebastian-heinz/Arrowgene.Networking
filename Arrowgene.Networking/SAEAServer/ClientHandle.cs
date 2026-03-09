using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// A generation-checked handle for an <see cref="Server.Client"/>.
/// </summary>
public readonly struct ClientHandle : IEquatable<ClientHandle>
{
    private readonly Client _client;
    private readonly Server _server;

    internal ClientHandle(Server server, Client client)
    {
        _server = server;
        _client = client;
        Generation = _client.Generation;
    }

    /// <summary>
    /// Gets the captured generation used to detect stale references.
    /// </summary>
    public uint Generation { get; }

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
    /// Tries to retrieve the underlying client if the handle is still valid.
    /// </summary>
    /// <param name="client">The active client instance.</param>
    /// <returns><c>true</c> if the handle is still valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetClient(out Client client)
    {
        Client? c = _client;
        if (c is null || c.Generation != Generation)
        {
            client = null!;
            return false;
        }

        client = c;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool EqualsClient(Client other)
    {
        return other == _client;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool EqualsClientGeneration(Client client, uint generation)
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
    public bool Equals(ClientHandle other)
    {
        return _client == other._client && Generation == other.Generation;
    }

    public override bool Equals(object? obj)
    {
        return obj is ClientHandle other && Equals(other);
    }

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