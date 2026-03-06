using System;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

/// <summary>
/// A generation-checked handle to a live server connection.
/// </summary>
public readonly struct AsyncEventServerConnection : IEquatable<AsyncEventServerConnection>
{
    private readonly AsyncEventServerSessionState? _session;
    private readonly int _generation;

    internal AsyncEventServerConnection(AsyncEventServerSessionState session, int generation)
    {
        _session = session;
        _generation = generation;
    }

    /// <summary>
    /// Gets the human-readable connection identity.
    /// </summary>
    public string Identity => GetRequiredSessionReference().GetIdentity(_generation);

    /// <summary>
    /// Gets the remote IP address.
    /// </summary>
    public IPAddress RemoteAddress => GetRequiredSessionReference().GetRemoteAddress(_generation);

    /// <summary>
    /// Gets the remote TCP port.
    /// </summary>
    public ushort RemotePort => GetRequiredSessionReference().GetRemotePort(_generation);

    /// <summary>
    /// Gets the assigned ordering lane.
    /// </summary>
    public int OrderingLane => GetRequiredSessionReference().GetOrderingLane(_generation);

    /// <summary>
    /// Gets the UTC timestamp when the connection was accepted.
    /// </summary>
    public DateTime ConnectedAtUtc => GetRequiredSessionReference().GetConnectedAtUtc(_generation);

    /// <summary>
    /// Gets the last successful receive tick.
    /// </summary>
    public long LastReceivedTick => GetRequiredSessionReference().GetLastReceiveTick(_generation);

    /// <summary>
    /// Gets the last successful send tick.
    /// </summary>
    public long LastSentTick => GetRequiredSessionReference().GetLastSendTick(_generation);

    /// <summary>
    /// Gets the total received byte count.
    /// </summary>
    public long BytesReceived => GetRequiredSessionReference().GetBytesReceived(_generation);

    /// <summary>
    /// Gets the total sent byte count.
    /// </summary>
    public long BytesSent => GetRequiredSessionReference().GetBytesSent(_generation);

    /// <summary>
    /// Gets whether the connection is still open for new I/O.
    /// </summary>
    public bool IsConnected => GetRequiredSessionReference().GetIsConnected(_generation);

    /// <summary>
    /// Sends a byte array to the remote endpoint.
    /// </summary>
    /// <param name="payload">The payload to queue.</param>
    public void Send(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        GetRequiredSessionReference().Send(_generation, payload);
    }

    /// <summary>
    /// Sends a span to the remote endpoint.
    /// </summary>
    /// <param name="payload">The payload to queue.</param>
    public void Send(ReadOnlySpan<byte> payload)
    {
        GetRequiredSessionReference().Send(_generation, payload);
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    public void Close()
    {
        GetRequiredSessionReference().Close(_generation);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(AsyncEventServerConnection other)
    {
        return ReferenceEquals(_session, other._session) && _generation == other._generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AsyncEventServerConnection other && Equals(other);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return HashCode.Combine(_session, _generation);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        AsyncEventServerSessionState? session = _session;
        if (session is null)
        {
            return "<stale>";
        }

        try
        {
            return session.GetIdentity(_generation);
        }
        catch (ObjectDisposedException)
        {
            return "<stale>";
        }
    }

    /// <summary>
    /// Compares two connection handles.
    /// </summary>
    public static bool operator ==(AsyncEventServerConnection left, AsyncEventServerConnection right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two connection handles.
    /// </summary>
    public static bool operator !=(AsyncEventServerConnection left, AsyncEventServerConnection right)
    {
        return !left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetSession(out AsyncEventServerSessionState? session)
    {
        AsyncEventServerSessionState? current = _session;
        if (current is not null && current.IsHandleValid(_generation))
        {
            session = current;
            return true;
        }

        session = null;
        return false;
    }

    private AsyncEventServerSessionState GetRequiredSessionReference()
    {
        if (_session is not null)
        {
            return _session;
        }

        throw new ObjectDisposedException(nameof(AsyncEventServerConnection), "The connection has been closed or recycled.");
    }
}
