using Arrowgene.Networking.Tcp;

namespace Arrowgene.Networking.Tcp.Consumer
{
    /// <summary>
    /// Optional consumer fast-path that can process data directly from shared receive buffers.
    /// </summary>
    public interface IBufferConsumer
    {
        void OnReceivedData(ITcpSocket socket, byte[] buffer, int offset, int count);
    }
}
