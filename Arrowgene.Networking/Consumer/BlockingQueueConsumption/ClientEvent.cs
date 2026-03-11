using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public class ClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType { get; }

        /// <summary>
        /// Gets the received payload for <see cref="ClientEventType.ReceivedData"/> events.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Gets the live client handle for connection or receive events.
        /// </summary>
        public ClientHandle? ClientHandle { get; }

        /// <summary>
        /// Gets the disconnected client snapshot for <see cref="ClientEventType.Disconnected"/> events.
        /// </summary>
        public ClientSnapshot? ClientSnapshot { get; }

        internal ClientEvent(
            ClientHandle? clientHandle,
            ClientSnapshot? clientSnapshot,
            ClientEventType clientEventType,
            byte[] data = null
        )
        {
            ClientHandle = clientHandle;
            ClientSnapshot = clientSnapshot;
            ClientEventType = clientEventType;
            Data = data;
        }
    }
}
