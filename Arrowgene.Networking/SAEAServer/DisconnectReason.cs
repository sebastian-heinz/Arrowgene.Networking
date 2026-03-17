namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Identifies the reason recorded when a client disconnect is finalized.
/// </summary>
public enum DisconnectReason
{
    /// <summary>
    /// No specific reason was recorded.
    /// </summary>
    None = 0,

    /// <summary>
    /// The remote peer closed the connection.
    /// </summary>
    RemoteClosed = 1,

    /// <summary>
    /// The server failed after a socket was accepted but before the connection became active.
    /// </summary>
    AcceptFailure = 2,

    /// <summary>
    /// The receive path failed before more data could be processed.
    /// </summary>
    ReceiveFailure = 3,

    /// <summary>
    /// The receive completion callback failed unexpectedly.
    /// </summary>
    ReceiveCompletedFailure = 4,

    /// <summary>
    /// The send path failed before queued data could be fully flushed.
    /// </summary>
    SendFailure = 5,

    /// <summary>
    /// The send completion callback failed unexpectedly.
    /// </summary>
    SendCompletedFailure = 6,

    /// <summary>
    /// The client exceeded the configured queued outbound byte limit.
    /// </summary>
    SendQueueOverflow = 7,

    /// <summary>
    /// The client exceeded the configured idle timeout.
    /// </summary>
    Timeout = 8,

    /// <summary>
    /// The server disconnected the client during shutdown.
    /// </summary>
    Shutdown = 9,

    /// <summary>
    /// The client handle was stale or no longer mapped to an active connection.
    /// </summary>
    StaleHandle = 10
}
