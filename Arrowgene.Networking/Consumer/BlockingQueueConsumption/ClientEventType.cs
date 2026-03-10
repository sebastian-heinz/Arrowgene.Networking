namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Identifies the kind of client event stored in a queue.
    /// </summary>
    public enum ClientEventType
    {
        /// <summary>
        /// A client connected.
        /// </summary>
        Connected,

        /// <summary>
        /// A payload was received from a client.
        /// </summary>
        ReceivedData,

        /// <summary>
        /// A client disconnected.
        /// </summary>
        Disconnected
    }
}
