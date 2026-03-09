using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class DisconnectedEventArgs : EventArgs
    {
        public DisconnectedEventArgs(ClientHandle clientHandle, ClientSnapshot clientSnapshot)
        {
            ClientHandle = clientHandle;
            ClientSnapshot = clientSnapshot;
        }

        public ClientHandle ClientHandle { get; }
        public ClientSnapshot ClientSnapshot { get; }
    }
}