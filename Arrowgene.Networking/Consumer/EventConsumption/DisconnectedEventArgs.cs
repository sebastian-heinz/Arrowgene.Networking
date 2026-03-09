using System;
using Arrowgene.Networking.Server;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class DisconnectedEventArgs : EventArgs
    {
        public DisconnectedEventArgs(ClientHandle socket)
        {
            Socket = socket;
        }

        public ClientHandle Socket { get; }
    }
}