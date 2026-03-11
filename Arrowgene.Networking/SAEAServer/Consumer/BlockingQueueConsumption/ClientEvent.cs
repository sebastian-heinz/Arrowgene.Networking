namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public class ClientConnectedEvent : IClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType => ClientEventType.Connected;

        /// <summary>
        /// Gets the live client handle for connection or receive events.
        /// </summary>
        public ClientHandle ClientHandle { get; }

        internal ClientConnectedEvent(ClientHandle clientHandle)
        {
            ClientHandle = clientHandle;
        }
    }
}