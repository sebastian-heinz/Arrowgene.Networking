using System;

namespace Arrowgene.Networking.SAEAServer.Consumer.EventConsumption
{
    /// <summary>
    /// Adapts <see cref="IConsumer"/> callbacks into .NET events.
    /// </summary>
    public class EventConsumer : IConsumer
    {
        /// <summary>
        /// Occurs when a client disconnects.
        /// </summary>
        public event EventHandler<DisconnectedEventArgs>? ClientDisconnected;

        /// <summary>
        /// Occurs when a client connects.
        /// </summary>
        public event EventHandler<ConnectedEventArgs>? ClientConnected;

        /// <summary>
        /// Occurs when a packet is received.
        /// </summary>
        public event EventHandler<ReceivedPacketEventArgs>? ReceivedPacket;

        /// <summary>
        /// Occurs when a consumer error is raised.
        /// </summary>
        public event EventHandler<ErrorEventArgs>? Error;


        void IConsumer.OnReceivedData(ClientHandle socket, byte[] data)
        {
            EventHandler<ReceivedPacketEventArgs>? receivedPacket = ReceivedPacket;
            if (receivedPacket != null)
            {
                ReceivedPacketEventArgs receivedPacketEventArgs = new ReceivedPacketEventArgs(socket, data);
                receivedPacket(this, receivedPacketEventArgs);
            }
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            EventHandler<DisconnectedEventArgs>? clientDisconnected = ClientDisconnected;
            if (clientDisconnected != null)
            {
                DisconnectedEventArgs clientDisconnectedEventArgs =
                    new DisconnectedEventArgs(clientSnapshot);
                clientDisconnected(this, clientDisconnectedEventArgs);
            }
        }

        void IConsumer.OnClientConnected(ClientHandle socket)
        {
            EventHandler<ConnectedEventArgs>? clientConnected = ClientConnected;
            if (clientConnected != null)
            {
                ConnectedEventArgs clientConnectedEventArgs = new ConnectedEventArgs(socket);
                clientConnected(this, clientConnectedEventArgs);
            }
        }

        void IConsumer.OnError(ClientSnapshot clientSnapshot, Exception exception, string message)
        {
            EventHandler<ErrorEventArgs>? error = Error;
            if (error != null)
            {
                ErrorEventArgs errorEventArgs = new ErrorEventArgs(clientSnapshot, exception, message);
                error(this, errorEventArgs);
            }
        }
    }
}
