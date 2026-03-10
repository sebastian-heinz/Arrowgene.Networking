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
        /// Gets the live client handle for connection or receive events.
        /// </summary>
        public ClientHandle ClientHandle { get; }

        /// <summary>
        /// Gets the exception that was thrown.
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// Gets additional context about where the error occurred.
        /// </summary>
        public string Message { get; }

        internal ClientErrorEvent(ClientHandle clientHandle, Exception exception, string message)
        {
            ClientHandle = clientHandle;
            Exception = exception;
            Message = message;
        }
    }
}