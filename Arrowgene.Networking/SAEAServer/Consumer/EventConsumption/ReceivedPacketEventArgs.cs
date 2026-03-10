using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Event payload for a received packet notification.
    /// </summary>
    public class ReceivedPacketEventArgs : EventArgs
    {
        internal ReceivedPacketEventArgs(ClientHandle socket, byte[] data)
        {
            Socket = socket;
            Data = data;
        }

        /// <summary>
        /// Gets the client that produced the packet.
        /// </summary>
        public ClientHandle Socket { get; }

        /// <summary>
        /// Gets the copied packet payload.
        /// </summary>
        public byte[] Data { get; }
    }
}
