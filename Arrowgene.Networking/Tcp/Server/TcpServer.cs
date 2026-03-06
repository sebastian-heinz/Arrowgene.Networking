using System;
using System.Net;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Server
{
    public abstract class TcpServer : ITcpServer
    {
        private readonly IConsumer _consumer;

        protected TcpServer(IPAddress ipAddress, ushort port, IConsumer consumer)
        {
            if (ipAddress == null)
                throw new Exception("IPAddress is null");

            if (port <= 0 || port > 65535)
                throw new Exception($"Port({port}) invalid");

            IpAddress = ipAddress;
            Port = port;
            _consumer = consumer;
        }

        public IPAddress IpAddress { get; }
        public ushort Port { get; }

        protected abstract void ServerStart();
        protected abstract void ServerStop();

        public abstract void Send<T>(T socket, byte[] data) where T : ITcpSocket;

        protected void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            _consumer.OnReceivedData(socket, data);
        }

        protected void OnReceivedData(ITcpSocket socket, byte[] buffer, int offset, int count)
        {
            byte[] data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            _consumer.OnReceivedData(socket, data);
        }

        protected void OnClientDisconnected(ITcpSocket socket)
        {
            _consumer.OnClientDisconnected(socket);
        }

        protected void OnClientConnected(ITcpSocket socket)
        {
            _consumer.OnClientConnected(socket);
        }

        public void Start()
        {
            _consumer.OnStart();
            ServerStart();
        }

        public void Stop()
        {
            ServerStop();
            _consumer.OnStop();
        }
    }
}
