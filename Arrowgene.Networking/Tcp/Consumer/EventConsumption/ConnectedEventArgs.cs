using System;

namespace Arrowgene.Networking.Tcp.Consumer.EventConsumption
{
    public class ConnectedEventArgs : EventArgs
    {
        public ConnectedEventArgs(ITcpSocket socket)
        {
            Socket = socket;
        }

        public ITcpSocket Socket { get; }
    }
}