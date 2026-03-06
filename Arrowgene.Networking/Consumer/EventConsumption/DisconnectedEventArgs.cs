using System;
using Arrowgene.Networking.Server;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class DisconnectedEventArgs : EventArgs
    {
        public DisconnectedEventArgs(AsyncEventClientHandle socket)
        {
            Socket = socket;
        }

        public AsyncEventClientHandle Socket { get; }
    }
}