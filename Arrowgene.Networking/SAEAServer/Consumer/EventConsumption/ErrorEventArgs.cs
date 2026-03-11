using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Event payload for a consumer error notification.
    /// </summary>
    public class ErrorEventArgs : EventArgs
    {
        internal ErrorEventArgs(ClientHandle clientHandle, Exception exception, string message)
        {
            ClientHandle = clientHandle;
            Exception = exception;
            Message = message;
        }

        /// <summary>
        /// Gets the client associated with the error.
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
    }
}