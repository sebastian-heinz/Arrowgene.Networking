using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal interface ISaeaAcceptHandler
{
    void OnSocketAccepted(Socket socket);
}
