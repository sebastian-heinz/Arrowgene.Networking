namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public class ClientDisconnectedEvent : IClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType => ClientEventType.Disconnected;

        /// <summary>
        /// Gets the disconnected client snapshot for <see cref="ClientEventType.Disconnected"/> events.
        /// </summary>
        public ClientSnapshot ClientSnapshot { get; }

        internal ClientDisconnectedEvent(ClientSnapshot clientSnapshot)
        {
            ClientSnapshot = clientSnapshot;
        }
    }
}