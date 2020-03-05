using System;
using System.Net;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Client
{
    public abstract class TcpClient : ITcpClient
    {
        private readonly IConsumer _consumer;

        protected TcpClient(IConsumer consumer)
        {
            _consumer = consumer;
        }

        public abstract bool IsAlive { get; }

        public event EventHandler<ConnectErrorEventArgs> ConnectError;

        public IPAddress RemoteIpAddress { get; protected set; }
        public ushort Port { get; protected set; }
        public int UnitOfOrder => 0;
        public DateTime LastActive { get; set; }

        public abstract void Send(byte[] payload);

        public void Connect(IPAddress serverIpAddress, ushort serverPort, TimeSpan timeout)
        {
            _consumer.OnStart();
            OnConnect(serverIpAddress, serverPort, timeout);
        }

        public void Connect(string remoteIpAddress, ushort serverPort, TimeSpan timeout)
        {
            Connect(IPAddress.Parse(remoteIpAddress), serverPort, timeout);
        }

        public void Close()
        {
            OnClose();
            _consumer.OnStop();
        }

        public string Identity
        {
            get
            {
                if (RemoteIpAddress != null)
                {
                    return RemoteIpAddress.ToString();
                }

                return GetHashCode().ToString();
            }
        }

        protected abstract void OnConnect(IPAddress serverIpAddress, ushort serverPort, TimeSpan timeout);
        protected abstract void OnClose();

        protected void OnReceivedData(ITcpSocket socket, byte[] data)
        {
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

        protected void OnConnectError(ITcpClient client, string reason, IPAddress serverIpAddress, ushort serverPort,
            TimeSpan timeout)
        {
            EventHandler<ConnectErrorEventArgs> connectError = ConnectError;
            if (connectError != null)
            {
                ConnectErrorEventArgs connectErrorEventArgsEventArgs =
                    new ConnectErrorEventArgs(client, reason, serverIpAddress, serverPort, timeout);
                connectError(this, connectErrorEventArgsEventArgs);
            }
        }
    }
}