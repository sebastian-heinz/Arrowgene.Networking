using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventClient : ITcpSocket
    {
        public int ClientId { get; private set; }

        public string Identity { get; private set; }

        internal int RunGeneration { get; private set; }
        internal int Generation { get; private set; }

        public IPAddress RemoteIpAddress { get; private set; }
        public ushort Port { get; private set; }
        public int UnitOfOrder { get; private set; }
        public Socket Socket { get; private set; }
        public DateTime LastRead { get; internal set; }
        public DateTime LastWrite { get; internal set; }
        public DateTime ConnectedAt { get; private set; }
        public ulong BytesReceived { get; internal set; }
        public ulong BytesSend { get; internal set; }
        public bool IsAlive => _isAlive;

        internal SocketAsyncEventArgs ReadEventArgs { get; }
        internal SocketAsyncEventArgs WriteEventArgs { get; }
        internal AsyncEventWriteState WriteState { get; set; }

        private volatile bool _isAlive;
        private int _disconnectInProgress;
        private int _pendingIoOperations;
        private int _returnedToPool;
        private readonly AsyncEventServer _server;


        public AsyncEventClient(
            int clientId,
            int runGeneration,
            AsyncEventServer server,
            SocketAsyncEventArgs readEventArgs,
            SocketAsyncEventArgs writeEventArgs)
        {
            ClientId = clientId;
            RunGeneration = runGeneration;
            _isAlive = true;
            _disconnectInProgress = 1;
            _pendingIoOperations = 0;
            _returnedToPool = 1;
            Generation = 0;
            _server = server;
            WriteState = new AsyncEventWriteState();
            ReadEventArgs = readEventArgs;
            WriteEventArgs = writeEventArgs;

            ReadEventArgs.UserToken = this;
            WriteEventArgs.UserToken = this;
        }

        internal void Initialize(
            Socket socket,
            int unitOfOrder,
            int runGeneration,
            int clientGeneration
        )
        {
            Socket = socket;
            UnitOfOrder = unitOfOrder;
            RunGeneration = runGeneration;
            Generation = clientGeneration;

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
                Port = (ushort)ipEndPoint.Port;
            }

            Identity = $"[{RemoteIpAddress}:{Port}]";

            WriteState.Reset();

            Interlocked.Exchange(ref _pendingIoOperations, 0);
            Volatile.Write(ref _returnedToPool, 0);
            Volatile.Write(ref _disconnectInProgress, 0);
            _isAlive = false;
        }

        public void Send(byte[] data)
        {
            _server.Send(this, data);
        }

        public void Close()
        {
            if (!_isAlive)
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