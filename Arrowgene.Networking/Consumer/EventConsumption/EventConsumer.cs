using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    /// <summary>
    /// Adapts <see cref="IConsumer"/> callbacks into .NET events.
    /// </summary>
    public class EventConsumer : IConsumer
    {
        /// <summary>
        /// Occurs when a client disconnects.
        /// </summary>
        public event EventHandler<DisconnectedEventArgs> ClientDisconnected;

        /// <summary>
        /// Occurs when a client connects.
        /// </summary>
        public event EventHandler<ConnectedEventArgs> ClientConnected;

        /// <summary>
        /// Occurs when a packet is received.
        /// </summary>
        public event EventHandler<ReceivedPacketEventArgs> ReceivedPacket;

        void IConsumer.OnReceivedData(ClientHandle socket, byte[] data)
        {
            EventHandler<ReceivedPacketEventArgs> receivedPacket = ReceivedPacket;
            if (receivedPacket != null)
            {
                ReceivedPacketEventArgs receivedPacketEventArgs = new ReceivedPacketEventArgs(socket, data);
                receivedPacket(this, receivedPacketEventArgs);
            }
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            EventHandler<DisconnectedEventArgs> clientDisconnected = ClientDisconnected;
            if (clientDisconnected != null)
            {
                DisconnectedEventArgs clientDisconnectedEventArgs =
                    new DisconnectedEventArgs(clientSnapshot);
                clientDisconnected(this, clientDisconnectedEventArgs);
            }
        }

        void IConsumer.OnClientConnected(ClientHandle socket)
        {
            EventHandler<ConnectedEventArgs> clientConnected = ClientConnected;
            if (clientConnected != null)
            {
                ConnectedEventArgs clientConnectedEventArgs = new ConnectedEventArgs(socket);
                clientConnected(this, clientConnectedEventArgs);
            }
        }

        void IConsumer.OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            throw exception;
        }
    }
}
