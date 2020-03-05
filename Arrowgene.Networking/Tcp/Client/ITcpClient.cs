// ReSharper disable EventNeverSubscribedTo.Global

using System;
using System.Net;

namespace Arrowgene.Networking.Tcp.Client
{
    public interface ITcpClient : ITcpSocket
    {
        void Connect(IPAddress serverIpAddress, ushort serverPort, TimeSpan timeout);
    }
}