using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

/// <summary>
/// Handles connection lifecycle and payload callbacks emitted by <see cref="SaeaServer"/>.
/// </summary>
/// <remarks>
/// Callbacks are executed on accept or I/O completion threads. Implementations must be thread-safe.
/// Payloads are always copied before delivery so handlers own the received <see cref="byte"/> array.
/// </remarks>
public interface ISaeaConnectionHandler
{
    /// <summary>
    /// Called after the server has started listening.
    /// </summary>
    void OnServerStarted();

    /// <summary>
    /// Called after the server has stopped and all active sessions have been disconnected.
    /// </summary>
    void OnServerStopped();

    /// <summary>
    /// Called when a new client connection has been accepted.
    /// </summary>
    /// <param name="session">The connected session handle.</param>
    void OnConnected(ISaeaSession session);

    /// <summary>
    /// Called when a copied payload has been received from a client.
    /// </summary>
    /// <param name="session">The session that produced the payload.</param>
    /// <param name="data">An isolated payload buffer owned by the handler.</param>
    void OnReceived(ISaeaSession session, byte[] data);

    /// <summary>
    /// Called when a client has been disconnected.
    /// </summary>
    /// <param name="session">The disconnected session handle.</param>
    /// <param name="socketError">
    /// The socket error that ended the connection, or <see cref="SocketError.Success"/> for graceful shutdown.
    /// </param>
    void OnDisconnected(ISaeaSession session, SocketError socketError);
}
