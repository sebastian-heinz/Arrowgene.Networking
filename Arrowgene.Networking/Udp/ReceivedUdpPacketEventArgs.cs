// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System;
using System.Net;

namespace Arrowgene.Networking.Udp
{
    public class ReceivedUdpPacketEventArgs : EventArgs
    {
        public ReceivedUdpPacketEventArgs(int size, byte[] received, IPEndPoint remoteIpEndPoint)
        {
            Size = size;
            Received = received;
            RemoteIpEndPoint = remoteIpEndPoint;
        }

        public IPEndPoint RemoteIpEndPoint { get; }
        public byte[] Received { get; }
        public int Size { get; }
    }
}