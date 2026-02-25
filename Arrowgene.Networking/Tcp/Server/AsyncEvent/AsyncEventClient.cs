using System;
using System.Net;
using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventClient : ITcpSocket
    {
        public string Identity { get; private set; }
        public IPAddress RemoteIpAddress { get; private set; }
        public ushort Port { get; private set; }
        public int UnitOfOrder { get; private set; }
        public Socket Socket { get; private set; }
        public SocketAsyncEventArgs ReadEventArgs { get;  }
        public SocketAsyncEventArgs WriteEventArgs { get; }
        public DateTime LastRead { get; internal set; }
        public DateTime LastWrite { get; internal set; }
        public DateTime ConnectedAt { get; private set; }
        public ulong BytesReceived { get; internal set; }
        public ulong BytesSend { get; internal set; }
        private bool _isAlive;
        private readonly AsyncEventServer _server;
        private readonly object _lock;
        
        internal AsyncEventWriteState WriteState { get; set; }
        
        public bool IsAlive
        {
            get
            {
                lock (_lock)
                {
                    return _isAlive;
                }
            }
        }

        public AsyncEventClient(
            SocketAsyncEventArgs readEventArgs,
            SocketAsyncEventArgs writeEventArgs,
            AsyncEventServer server)
        {
            _lock = new object();
            _isAlive = false;
            _server = server;
            ReadEventArgs = readEventArgs;
            WriteEventArgs = writeEventArgs;

            ReadEventArgs.UserToken = this;
            WriteEventArgs.UserToken = this;
        }

        internal void Open(
            Socket socket,
            int unitOfOrder
        )
        {
            Socket = socket;
            UnitOfOrder = unitOfOrder;
            
            DateTime now = DateTime.Now;
            LastRead = now;
            LastWrite = now;
            ConnectedAt = now;
            
            if (Socket.RemoteEndPoint is IPEndPoint ipEndPoint)
            {
                RemoteIpAddress = ipEndPoint.Address;
                Port = (ushort) ipEndPoint.Port;
            }

            Identity = $"[{RemoteIpAddress}:{Port}]";
            
            lock (_lock)
            {
                _isAlive = true;
            }
        }
        
        public void Send(byte[] data)
        {
            _server.Send(this, data);
        }
        
        public void Close()
        {
            lock (_lock)
            {
                if (!_isAlive)
                {
                    return;
                }

                _isAlive = false;
            }

            try
            {
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // ignored
            }
            
            try
            {
                Socket.Close();
            }
            catch
            {
                // ignored
            }
        }
    }
}
