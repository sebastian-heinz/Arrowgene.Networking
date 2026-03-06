using System;
using System.Net;
using System.Runtime.CompilerServices;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

/// <summary>
/// Generation-checked handle to a pooled <see cref="SaeaSession"/>.
/// </summary>
public readonly struct SaeaSessionHandle : ISaeaSession, IEquatable<SaeaSessionHandle>
{
    private readonly SaeaSession? _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaeaSessionHandle"/> struct.
    /// </summary>
    /// <param name="session">The underlying pooled session.</param>
    /// <param name="generation">The captured session generation.</param>
    internal SaeaSessionHandle(SaeaSession session, int generation)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Generation = generation;
    }

    /// <summary>
    /// Captured session generation.
    /// </summary>
    public int Generation { get; }

    /// <inheritdoc />
    public int SessionId => Session.SessionId;

    /// <inheritdoc />
    public string Identity => Session.Identity;

    /// <inheritdoc />
    public IPAddress RemoteIpAddress => Session.RemoteIpAddress;

    /// <inheritdoc />
    public ushort RemotePort => Session.RemotePort;

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt => Session.ConnectedAt;

    /// <inheritdoc />
    public long LastReceiveTicks => Session.LastReceiveTicks;

    /// <inheritdoc />
    public long LastSendTicks => Session.LastSendTicks;

    /// <inheritdoc />
    public ulong BytesReceived => Session.BytesReceived;

    /// <inheritdoc />
    public ulong BytesSent => Session.BytesSent;

    /// <inheritdoc />
    public bool IsConnected => Session.IsConnected;

    internal bool TryGetSession(out SaeaSession? session)
    {
        if (_session is not null && _session.Generation == Generation)
        {
            session = _session;
            return true;
        }

        session = null;
        return false;
    }

    /// <inheritdoc />
    public void Send(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Session.Server.Send(this, data);
    }

    /// <inheritdoc />
    public void Send(ReadOnlySpan<byte> data)
    {
        Session.Server.Send(this, data);
    }

    /// <inheritdoc />
    public void Close()
    {
        Session.Server.Disconnect(this);
    }

    /// <inheritdoc />
    public bool Equals(SaeaSessionHandle other)
    {
        return ReferenceEquals(_session, other._session) && Generation == other.Generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is SaeaSessionHandle other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(_session, Generation);
    }

    /// <summary>
    /// Compares two handles for session and generation equality.
    /// </summary>
    public static bool operator ==(SaeaSessionHandle left, SaeaSessionHandle right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two handles for session and generation inequality.
    /// </summary>
    public static bool operator !=(SaeaSessionHandle left, SaeaSessionHandle right)
    {
        return !left.Equals(right);
    }

    private SaeaSession Session
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_session is not null && _session.Generation == Generation)
            {
                return _session;
            }

            ThrowStaleHandleException();
            return null!;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStaleHandleException()
    {
        throw new ObjectDisposedException(nameof(SaeaSessionHandle), "The session handle is stale.");
    }
}
