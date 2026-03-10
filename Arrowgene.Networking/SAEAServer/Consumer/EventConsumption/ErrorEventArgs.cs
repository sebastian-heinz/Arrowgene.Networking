using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    public class ErrorEventArgs : EventArgs
    {
        internal ErrorEventArgs(ClientHandle clientHandle, Exception exception, string message)
        {
            ClientHandle = clientHandle;
            Exception = exception;
            Message = message;
        }


        ClientHandle ClientHandle { get; }
        Exception Exception { get; }
        string Message { get; }
    }
}