using System;

namespace Arrowgene.Networking.Tcp.Consumer.EventConsumption
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


        public void OnStart()
        {
        }

        public void OnStarted()
        {

        }

        public void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            EventHandler<ReceivedPacketEventArgs> receivedPacket = ReceivedPacket;
            if (receivedPacket != null)
            {
                ReceivedPacketEventArgs receivedPacketEventArgs = new ReceivedPacketEventArgs(socket, data);
                receivedPacket(this, receivedPacketEventArgs);
            }
        }

        public void OnClientDisconnected(ITcpSocket socket)
        {
            EventHandler<DisconnectedEventArgs> clientDisconnected = ClientDisconnected;
            if (clientDisconnected != null)
            {
                DisconnectedEventArgs clientDisconnectedEventArgs = new DisconnectedEventArgs(socket);
                clientDisconnected(this, clientDisconnectedEventArgs);
            }
        }

        public void OnClientConnected(ITcpSocket socket)
        {
            EventHandler<ConnectedEventArgs> clientConnected = ClientConnected;
            if (clientConnected != null)
            {
                ConnectedEventArgs clientConnectedEventArgs = new ConnectedEventArgs(socket);
                clientConnected(this, clientConnectedEventArgs);
            }
        }

        public void OnStop()
        {
        }

        public void OnStopped()
        {
       
        }
    }
}