using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class ReceivedPacketEventArgs : EventArgs
    {
        public ReceivedPacketEventArgs(ClientHandle socket, byte[] data)
        {
            Socket = socket;
            Data = data;
        }

        public ClientHandle Socket { get; }

        public byte[] Data { get; }
    }
}