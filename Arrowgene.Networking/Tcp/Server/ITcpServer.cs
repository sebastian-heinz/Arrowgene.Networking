using System.Net;

namespace Arrowgene.Networking.Tcp.Server
{
    public interface ITcpServer
    {
        IPAddress IpAddress { get; }
        ushort Port { get; }
        void Start();
        void Stop();
        void Send(ITcpSocket socket, byte[] data);
    }
}