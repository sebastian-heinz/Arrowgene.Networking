namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public class ClientDataEvent : IClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType => ClientEventType.ReceivedData;

        /// <summary>
        /// Gets the received payload for <see cref="ClientEventType.ReceivedData"/> events.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Gets the live client handle for connection or receive events.
        /// </summary>
        public ClientHandle ClientHandle { get; }

        internal long EnqueuedTimestamp { get; }

        internal ClientDataEvent(ClientHandle clientHandle, byte[] data, long enqueuedTimestamp = 0L)
        {
            ClientHandle = clientHandle;
            Data = data;
            EnqueuedTimestamp = enqueuedTimestamp;
        }
    }
}
