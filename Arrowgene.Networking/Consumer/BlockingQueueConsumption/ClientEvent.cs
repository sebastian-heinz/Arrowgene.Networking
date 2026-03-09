using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    public class ClientEvent
    {
        public ClientEventType ClientEventType { get; }
        public byte[] Data { get; }
        public ClientHandle Socket { get; }

        public ClientEvent(ClientHandle socket, ClientEventType clientEventType, byte[] data = null)
        {
            Socket = socket;
            ClientEventType = clientEventType;
            Data = data;
        }
    }
}