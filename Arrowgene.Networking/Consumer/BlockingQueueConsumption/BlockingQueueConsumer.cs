using System;
using System.Collections.Concurrent;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    public class BlockingQueueConsumer : IConsumer
    {
        public readonly BlockingCollection<ClientEvent> ClientEvents;

        public BlockingQueueConsumer()
        {
            ClientEvents = new BlockingCollection<ClientEvent>();
        }

        void IConsumer.OnReceivedData(ClientHandle socket, byte[] data)
        {
            ClientEvents.Add(new ClientEvent(socket, null, ClientEventType.ReceivedData, data));
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            ClientEvents.Add(new ClientEvent(null, clientSnapshot, ClientEventType.Disconnected));
        }

        void IConsumer.OnClientConnected(ClientHandle clientHandle)
        {
            ClientEvents.Add(new ClientEvent(clientHandle, null, ClientEventType.Connected));
        }

        void IConsumer.OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            throw exception;
        }
    }
}
