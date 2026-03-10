using System;
using System.Collections.Concurrent;

namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Queues consumer callbacks into a single blocking collection for external processing.
    /// </summary>
    public class BlockingQueueConsumer : IConsumer
    {
        /// <summary>
        /// Gets the queue that receives client events in callback order.
        /// </summary>
        public readonly BlockingCollection<IClientEvent> ClientEvents;

        /// <summary>
        /// Initializes an empty blocking queue consumer.
        /// </summary>
        public BlockingQueueConsumer()
        {
            ClientEvents = new BlockingCollection<IClientEvent>();
        }

        void IConsumer.OnReceivedData(ClientHandle clientHandle, byte[] data)
        {
            ClientEvents.Add(new ClientDataEvent(clientHandle, data));
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            ClientEvents.Add(new ClientDisconnectedEvent(clientSnapshot));
        }

        void IConsumer.OnClientConnected(ClientHandle clientHandle)
        {
            ClientEvents.Add(new ClientConnectedEvent(clientHandle));
        }

        void IConsumer.OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            ClientEvents.Add(new ClientErrorEvent(clientHandle, exception, message));
        }
    }
}