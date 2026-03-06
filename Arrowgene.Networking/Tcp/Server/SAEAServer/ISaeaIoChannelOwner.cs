using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal interface ISaeaIoChannelOwner
{
    void OnReceiveCompleted(int sessionId, BufferSlice buffer);

    void OnSendCompleted(int sessionId, int bytesTransferred);

    void OnChannelClosed(int sessionId, SocketError socketError);

    void OnChannelReadyForRecycle(int sessionId);
}
