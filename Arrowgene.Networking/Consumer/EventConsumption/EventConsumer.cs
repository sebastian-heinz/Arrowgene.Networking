using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.EventConsumption
{
    public class EventConsumer : IConsumer
    {
        /// <summary>
        /// Occures when a client disconnected.
        /// </summary>
        public event EventHandler<DisconnectedEventArgs> ClientDisconnected;

        /// <summary>
        /// Occures when a client connected.
        /// </summary>
        public event EventHandler<ConnectedEventArgs> ClientConnected;

        /// <summary>
        /// Occures when a packet is received.
        /// </summary>
        public event EventHandler<ReceivedPacketEventArgs> ReceivedPacket;

        public void OnReceivedData(ClientHandle socket, byte[] data)
        {
            EventHandler<ReceivedPacketEventArgs> receivedPacket = ReceivedPacket;
            if (receivedPacket != null)
            {
                ReceivedPacketEventArgs receivedPacketEventArgs = new ReceivedPacketEventArgs(socket, data);
                receivedPacket(this, receivedPacketEventArgs);
            }
        }

        public void OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            EventHandler<DisconnectedEventArgs> clientDisconnected = ClientDisconnected;
            if (clientDisconnected != null)
            {
                DisconnectedEventArgs clientDisconnectedEventArgs =
                    new DisconnectedEventArgs(clientSnapshot);
                clientDisconnected(this, clientDisconnectedEventArgs);
            }
        }

        public void OnClientConnected(ClientHandle socket)
        {
            EventHandler<ConnectedEventArgs> clientConnected = ClientConnected;
            if (clientConnected != null)
            {
                ConnectedEventArgs clientConnectedEventArgs = new ConnectedEventArgs(socket);
                clientConnected(this, clientConnectedEventArgs);
            }
        }

        public void OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            throw exception;
        }
    }
}