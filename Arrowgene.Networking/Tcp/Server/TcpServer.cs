using System;
using System.Net;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Server
{
    public abstract class TcpServer : ITcpServer
    {
        private readonly IConsumer _consumer;
        private readonly IBufferConsumer _bufferConsumer;

        protected TcpServer(IPAddress ipAddress, ushort port, IConsumer consumer)
        {
            if (ipAddress == null)
                throw new Exception("IPAddress is null");

            if (port <= 0 || port > 65535)
                throw new Exception($"Port({port}) invalid");

            IpAddress = ipAddress;
            Port = port;
            _consumer = consumer;
            _bufferConsumer = consumer as IBufferConsumer;
        }

        public IPAddress IpAddress { get; }
        public ushort Port { get; }

        protected abstract void ServerStart();
        protected abstract void ServerStop();
        
        public abstract void Send(ITcpSocket socket, byte[] data);

        protected void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            if (_bufferConsumer != null)
            {
                _bufferConsumer.OnReceivedData(socket, data, 0, data.Length);
                return;
            }

            _consumer.OnReceivedData(socket, data);
        }

        protected void OnReceivedData(ITcpSocket socket, byte[] buffer, int offset, int count)
        {
            if (_bufferConsumer != null)
            {
                _bufferConsumer.OnReceivedData(socket, buffer, offset, count);
                return;
            }

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
            _consumer.OnStop();
            ServerStop();
        }
    }
}
