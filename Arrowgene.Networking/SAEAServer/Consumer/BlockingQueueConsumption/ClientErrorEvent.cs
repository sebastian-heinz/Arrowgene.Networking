using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Represents a queued client event produced by a consumer callback.
    /// </summary>
    public class ClientErrorEvent : IClientEvent
    {
        /// <summary>
        /// Gets the kind of queued client event.
        /// </summary>
        public ClientEventType ClientEventType => ClientEventType.Error;

        /// <summary>
        /// Gets the immutable client snapshot associated with the error.
        /// </summary>
        public ClientSnapshot ClientSnapshot { get; }

        /// <summary>
        /// Gets the exception that was thrown.
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// Gets additional context about where the error occurred.
        /// </summary>
        public string Message { get; }

        internal ClientErrorEvent(ClientSnapshot clientSnapshot, Exception exception, string message)
        {
            ClientSnapshot = clientSnapshot;
            Exception = exception;
            Message = message;
        }
    }
}
