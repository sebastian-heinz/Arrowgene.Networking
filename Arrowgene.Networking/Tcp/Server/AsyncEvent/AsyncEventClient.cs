using System;
using System.Net;
using System.Net.Sockets;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventClient : ITcpSocket
    {
        public int ClientId { get; private set; }
        public string Identity { get; private set; }
        public int RunGeneration { get; private set; }
        public int Generation { get; private set; }
        public IPAddress RemoteIpAddress { get; private set; }
        public ushort Port { get; private set; }
        public int UnitOfOrder { get; private set; }
        public Socket Socket { get; private set; }
        public long LastReadTicks { get; internal set; }
        public long LastWriteTicks { get; internal set; }
        public DateTime ConnectedAt { get; private set; }
        public ulong BytesReceived { get; internal set; }
        public ulong BytesSend { get; internal set; }

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

        internal SocketAsyncEventArgs ReadEventArgs { get; }
        internal SocketAsyncEventArgs WriteEventArgs { get; }
        internal AsyncEventWriteState WriteState { get; set; }

        private bool _isAlive;
        private bool _isShuttingDown;
        private readonly AsyncEventServer _server;

        private readonly object _lock;

        public AsyncEventClient(
            int clientId,
            int runGeneration,
            AsyncEventServer server,
            SocketAsyncEventArgs readEventArgs,
            SocketAsyncEventArgs writeEventArgs
        )
        {
            ClientId = clientId;
            RunGeneration = runGeneration;
            _isAlive = false;
            Generation = 0;
            _server = server;
            WriteState = new AsyncEventWriteState();
            ReadEventArgs = readEventArgs;
            WriteEventArgs = writeEventArgs;
            _lock = new object();
        }

        public void Send(byte[] data)
        {
            Send(this, data);
        }

        public void Send<T>(T socket, byte[] data) where T : ITcpSocket
        {
            _server.Send(socket, data);
        }

        internal void Initialize(
            Socket socket,
            int unitOfOrder,
            int runGeneration,
            int clientGeneration,
            AsyncEventClientHandle handle
        )
        {
            lock (_lock)
            {
                if (_isAlive)
                {
                    throw new InvalidOperationException("Client is alive");
                }

                if (!_isShuttingDown)
                {
                    throw new InvalidOperationException("Client is shutting down");
                }

                _isAlive = true;
                _isShuttingDown = false;
            }

            Socket = socket;
            UnitOfOrder = unitOfOrder;
            RunGeneration = runGeneration;
            Generation = clientGeneration;

            ReadEventArgs.UserToken = handle;
            WriteEventArgs.UserToken = handle;

            long now = Environment.TickCount64;
            LastReadTicks = now;
            LastWriteTicks = now;
            ConnectedAt = DateTime.Now;
            BytesReceived = 0;
            BytesSend = 0;

            RemoteIpAddress = null;
            Port = 0;
            if (Socket.RemoteEndPoint is IPEndPoint ipEndPoint)
            {
                RemoteIpAddress = ipEndPoint.Address;
                Port = (ushort)ipEndPoint.Port;
            }

            Identity = $"[{RemoteIpAddress}:{Port}]";

            WriteState.Reset();
        }

        internal bool TryBeginShutdown()
        {
            lock (_lock)
            {
                if (_isShuttingDown)
                {
                    return false;
                }

                _isShuttingDown = true;
                return true;
            }
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

            Socket socket = Socket;
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // ignored
            }

            try
            {
                socket.Close();
            }
            catch
            {
                // ignored
            }
        }
    }
}