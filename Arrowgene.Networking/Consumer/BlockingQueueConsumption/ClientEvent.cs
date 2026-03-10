using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    public class ClientEvent
    {
        public ClientEventType ClientEventType { get; }
        public byte[] Data { get; }
        public ClientHandle? ClientHandle { get; }
        public ClientSnapshot? ClientSnapshot { get; }

        internal ClientEvent(
            ClientHandle? clientHandle,
            ClientSnapshot? clientSnapshot,
            ClientEventType clientEventType,
            byte[] data = null
        )
        {
            ClientHandle = clientHandle;
            ClientSnapshot = clientSnapshot;
            ClientEventType = clientEventType;
            Data = data;
        }
    }
}
