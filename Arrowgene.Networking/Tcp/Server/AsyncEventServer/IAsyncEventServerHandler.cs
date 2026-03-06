using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.AsyncEventServer;

/// <summary>
/// Handles lifecycle and payload callbacks for <see cref="AsyncEventServer"/>.
/// </summary>
public interface IAsyncEventServerHandler
{
    /// <summary>
    /// Called after the server has started listening.
    /// </summary>
    void OnServerStarted();

    /// <summary>
    /// Called after the server has fully stopped.
    /// </summary>
    void OnServerStopped();

    /// <summary>
    /// Called when a new connection has been accepted and registered.
    /// </summary>
    /// <param name="connection">The accepted connection.</param>
    void OnConnected(AsyncEventServerConnection connection);

    /// <summary>
    /// Called with an isolated copy of the received payload.
    /// </summary>
    /// <param name="connection">The connection that produced the payload.</param>
    /// <param name="payload">A dedicated payload copy owned by the handler.</param>
    void OnReceived(AsyncEventServerConnection connection, byte[] payload);

    /// <summary>
    /// Called once when a connection has been disconnected.
    /// </summary>
    /// <param name="connection">The disconnected connection.</param>
    /// <param name="error">The socket error that caused the disconnect.</param>
    void OnDisconnected(AsyncEventServerConnection connection, SocketError error);
}
