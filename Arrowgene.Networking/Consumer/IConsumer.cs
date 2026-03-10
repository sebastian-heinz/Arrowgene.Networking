using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer
{
    /// <summary>
    /// Receives server lifecycle and payload callbacks for connected clients.
    /// </summary>
    public interface IConsumer
    {
        /// <summary>
        /// Handles a received payload for a connected client.
        /// </summary>
        /// <param name="clientHandle">The client that produced the payload.</param>
        /// <param name="data">A copied payload buffer for the received data.</param>
        void OnReceivedData(ClientHandle clientHandle, byte[] data);

        /// <summary>
        /// Handles a client disconnection notification.
        /// </summary>
        /// <param name="clientSnapshot">The final snapshot of the disconnected client.</param>
        void OnClientDisconnected(ClientSnapshot clientSnapshot);

        /// <summary>
        /// Handles a new client connection notification.
        /// </summary>
        /// <param name="clientHandle">The connected client.</param>
        void OnClientConnected(ClientHandle clientHandle);

        /// <summary>
        /// Handles an exception raised while invoking consumer code.
        /// </summary>
        /// <param name="clientHandle">The client associated with the error.</param>
        /// <param name="exception">The exception that was thrown.</param>
        /// <param name="message">Additional context about where the error occurred.</param>
        void OnError(ClientHandle clientHandle, Exception exception, string message);
    }
}
