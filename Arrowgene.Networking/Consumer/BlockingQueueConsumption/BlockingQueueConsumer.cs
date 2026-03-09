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

        public void OnReceivedData(ClientHandle socket, byte[] data)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.ReceivedData, data));
        }

        public void OnClientDisconnected(ClientHandle socket)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.Disconnected));
        }

        public void OnClientConnected(ClientHandle socket)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.Connected));
        }

        public void OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            throw exception;
        }
    }
}