using System;

namespace Arrowgene.Networking.Tcp.Consumer.EventConsumption
{
    public class ReceivedPacketEventArgs : EventArgs
    {
        public ReceivedPacketEventArgs(ITcpSocket socket, byte[] data)
        {
            Socket = socket;
            Data = data;
        }

        public ITcpSocket Socket { get; }

        public byte[] Data { get; }
    }
}