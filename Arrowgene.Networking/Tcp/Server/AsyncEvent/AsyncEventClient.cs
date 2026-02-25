using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventClient : ITcpSocket
    {
        public string Identity { get; private set; }
        public IPAddress RemoteIpAddress { get; private set; }
        public ushort Port { get; private set; }
        public int UnitOfOrder { get; private set; }
        public Socket Socket { get; private set; }
        public DateTime LastRead { get; internal set; }
        public DateTime LastWrite { get; internal set; }
        public DateTime ConnectedAt { get; private set; }
        public ulong BytesReceived { get; internal set; }
        public ulong BytesSend { get; internal set; }
        
        internal SocketAsyncEventArgs ReadEventArgs { get;  }
        internal SocketAsyncEventArgs WriteEventArgs { get; }
        internal AsyncEventWriteState WriteState { get; set; }

        private int _isAlive;
        private int _disconnectInProgress;
        private readonly AsyncEventServer _server;

        public bool IsAlive
        {
            get => Volatile.Read(ref _isAlive) == 1;
        }

        public AsyncEventClient(
            SocketAsyncEventArgs readEventArgs,
            SocketAsyncEventArgs writeEventArgs,
            AsyncEventServer server)
        {
            _isAlive = 0;
            _disconnectInProgress = 1;
            _server = server;
            WriteState = new AsyncEventWriteState();
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
            BytesReceived = 0;
            BytesSend = 0;

            RemoteIpAddress = null;
            Port = 0;
            if (Socket.RemoteEndPoint is IPEndPoint ipEndPoint)
            {
                RemoteIpAddress = ipEndPoint.Address;
                Port = (ushort) ipEndPoint.Port;
            }

            Identity = $"[{RemoteIpAddress}:{Port}]";

            WriteState.Reset();

            Volatile.Write(ref _disconnectInProgress, 0);
            Volatile.Write(ref _isAlive, 1);
        }
        
        public void Send(byte[] data)
        {
            _server.Send(this, data);
        }
        
        public void Close()
        {
            if (Interlocked.Exchange(ref _isAlive, 0) == 0)
            {
                return;
            }

            WriteState.Reset();

            Socket socket = Socket;
            Socket = null;
            if (socket == null)
            {
                return;
            }

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

        internal bool TryBeginDisconnect()
        {
            return Interlocked.CompareExchange(ref _disconnectInProgress, 1, 0) == 0;
        }
    }
}
