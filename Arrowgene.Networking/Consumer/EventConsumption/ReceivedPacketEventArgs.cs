using System;
using Arrowgene.Networking.Server;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class ReceivedPacketEventArgs : EventArgs
    {
        public ReceivedPacketEventArgs(AsyncEventClientHandle socket, byte[] data)
        {
            Socket = socket;
            Data = data;
        }

        public AsyncEventClientHandle Socket { get; }

        public byte[] Data { get; }
    }
}