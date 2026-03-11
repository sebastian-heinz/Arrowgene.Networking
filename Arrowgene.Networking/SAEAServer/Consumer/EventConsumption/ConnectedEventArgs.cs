using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Event payload for a client connection notification.
    /// </summary>
    public class ConnectedEventArgs : EventArgs
    {
        internal ConnectedEventArgs(ClientHandle socket)
        {
            Socket = socket;
        }

        /// <summary>
        /// Gets the connected client.
        /// </summary>
        public ClientHandle Socket { get; }
    }
}
