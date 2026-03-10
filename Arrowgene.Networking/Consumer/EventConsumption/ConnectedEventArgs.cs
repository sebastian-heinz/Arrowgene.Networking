using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class ConnectedEventArgs : EventArgs
    {
        internal ConnectedEventArgs(ClientHandle socket)
        {
            Socket = socket;
        }

        public ClientHandle Socket { get; }
    }
}
