namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public interface IClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType { get; }
    }
}