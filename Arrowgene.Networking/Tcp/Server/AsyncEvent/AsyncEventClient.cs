using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventClient : ITcpSocket
    {
        private readonly SemaphoreSlim _maxSimultaneousSends;
        
        public string Identity { get; }
        public IPAddress RemoteIpAddress { get; }
        public ushort Port { get; }
        public int UnitOfOrder { get; }

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

        public Socket Socket { get; }
        public SocketAsyncEventArgs ReadEventArgs { get; private set; }
        public DateTime LastRead { get; set; }
        public DateTime LastWrite { get; set; }
        public DateTime ConnectedAt { get; set; }

        private bool _isAlive;
        private readonly AsyncEventServer _server;
        private readonly object _lock;

        public AsyncEventClient(Socket socket, SocketAsyncEventArgs readEventArgs, AsyncEventServer server, int uoo,
            int maxSimultaneousSends)
        {
            _lock = new object();
            _maxSimultaneousSends = new SemaphoreSlim(maxSimultaneousSends, maxSimultaneousSends);
            _isAlive = true;
            Socket = socket;
            ReadEventArgs = readEventArgs;
            _server = server;
            UnitOfOrder = uoo;
            
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
        }

        public void Send(byte[] data)
        {
            _server.Send(this, data);
        }

        public void ReleaseSend()
        {
            _maxSimultaneousSends.Release();
        }

        public void WaitSend()
        {
            _maxSimultaneousSends.Wait();
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
            
            _server.NotifyDisconnected(this);
            ReadEventArgs = null;
        }
    }
}
