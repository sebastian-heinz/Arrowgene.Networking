using System;
using Arrowgene.Networking.Server;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class ConnectedEventArgs : EventArgs
    {
        public ConnectedEventArgs(AsyncEventClientHandle socket)
        {
            Socket = socket;
        }

        public AsyncEventClientHandle Socket { get; }
    }
}