using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Event payload for a consumer error notification.
    /// </summary>
    public class ErrorEventArgs : EventArgs
    {
        internal ErrorEventArgs(ClientSnapshot clientSnapshot, Exception exception, string message)
        {
            ClientSnapshot = clientSnapshot;
            Exception = exception;
            Message = message;
        }

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
    }
}
