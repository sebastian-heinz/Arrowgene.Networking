using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class DisconnectedEventArgs : EventArgs
    {
        internal DisconnectedEventArgs(ClientSnapshot clientSnapshot)
        {
            ClientSnapshot = clientSnapshot;
        }

        public ClientSnapshot ClientSnapshot { get; }
    }
}
