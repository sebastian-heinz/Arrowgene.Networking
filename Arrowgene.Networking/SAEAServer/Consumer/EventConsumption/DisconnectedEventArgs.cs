using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Event payload for a client disconnection notification.
    /// </summary>
    public class DisconnectedEventArgs : EventArgs
    {
        internal DisconnectedEventArgs(ClientSnapshot clientSnapshot)
        {
            ClientSnapshot = clientSnapshot;
        }

        /// <summary>
        /// Gets the final snapshot captured for the disconnected client.
        /// </summary>
        public ClientSnapshot ClientSnapshot { get; }
    }
}
