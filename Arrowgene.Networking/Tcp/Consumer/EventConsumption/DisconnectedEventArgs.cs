using System;

namespace Arrowgene.Networking.Tcp.Consumer.EventConsumption
{
    public class DisconnectedEventArgs : EventArgs
    {
        public DisconnectedEventArgs(ITcpSocket socket)
        {
            Socket = socket;
        }

        public ITcpSocket Socket { get; }
    }
}