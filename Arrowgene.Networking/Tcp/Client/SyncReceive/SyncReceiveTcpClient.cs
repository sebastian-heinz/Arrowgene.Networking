using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Consumer;

namespace Arrowgene.Networking.Tcp.Client.SyncReceive
{
    public class SyncReceiveTcpClient : TcpClient
    {
        private const string DefaultName = "Tcp Client";

        private volatile bool _isConnected;
        private readonly int _pollTimeout;
        private readonly int _bufferSize;
        private readonly ILogger _logger;
        private Socket _socket;
        private Thread _readThread;

        public int SocketPollTimeout { get; }
        public int ThreadJoinTimeout { get; }
        public string Name { get; set; }

        public override bool IsAlive => _isConnected;

        public SyncReceiveTcpClient(IConsumer consumer) : base(consumer)
        {
            _logger = LogProvider.Logger(this);
            SocketPollTimeout = 100;
            Name = DefaultName;
            ThreadJoinTimeout = 1000;
            _pollTimeout = 10;
            _bufferSize = 1024;
        }

        public override void Send(byte[] payload)
        {
            _socket.Send(payload);
        }

        protected override void OnConnect(IPAddress remoteIpAddress, ushort serverPort, TimeSpan timeout)
        {
            if (!_isConnected)
            {
                if (remoteIpAddress == null || serverPort <= 0)
                {
                    throw new Exception($"Address({remoteIpAddress}) or Port({serverPort}) invalid");
                }

                RemoteIpAddress = remoteIpAddress;
                Port = serverPort;
                try
                {
                    Socket socket = CreateSocket();
                    if (socket != null)
                    {
                        if (timeout != TimeSpan.Zero)
                        {
                            IAsyncResult result = socket.BeginConnect(RemoteIpAddress, Port, null, null);
                            bool success = result.AsyncWaitHandle.WaitOne(timeout, true);
                            if (socket.Connected && success)
                            {
                                socket.EndConnect(result);
                                ConnectionEstablished(socket);
                            }
                            else
                            {
                                const string errTimeout = "Client connection timed out.";
                                _logger.Error(errTimeout);
                                socket.Close();
                                OnConnectError(this, errTimeout, RemoteIpAddress, Port, timeout);
                            }
                        }
                        else
                        {
                            socket.Connect(RemoteIpAddress, Port);
                            ConnectionEstablished(socket);
                        }
                    }
                    else
                    {
                        const string errConnect = "Client could not connect.";
                        _logger.Error(errConnect);
                        OnConnectError(this, errConnect, RemoteIpAddress, Port, timeout);
                    }
                }
                catch (Exception exception)
                {
                    _logger.Exception(exception);
                    OnConnectError(this, exception.Message, RemoteIpAddress, Port, timeout);
                }
            }
            else
            {
                const string errConnected = "Client is already connected.";
                _logger.Error(errConnected);
                OnConnectError(this, errConnected, RemoteIpAddress, Port, timeout);
            }
        }

        protected override void OnClose()
        {
            _isConnected = false;
            Service.JoinThread(_readThread, ThreadJoinTimeout, _logger);

            if (_socket != null)
            {
                _socket.Close();
            }

            _logger.Debug($"{Name} Closed");
            OnClientDisconnected(this);
        }

        private Socket CreateSocket()
        {
            Socket socket;
            _logger.Info($"{Name} Creating Socket...");
            if (RemoteIpAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                _logger.Info($"{Name} Created Socket (IPv6)");
            }
            else
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _logger.Info($"{Name} Created Socket (IPv4)");
            }

            return socket;
        }

        private void ConnectionEstablished(Socket socket)
        {
            _socket = socket;
            _readThread = new Thread(ReadProcess);
            _readThread.Name = Name;
            _readThread.Start();
            _logger.Info($"{Name} connected");
            OnClientConnected(this);
        }

        private void ReadProcess()
        {
            _logger.Info($"{Name} started.");
            _isConnected = true;
            while (_isConnected)
            {
                if (_socket.Poll(_pollTimeout, SelectMode.SelectRead))
                {
                    byte[] buffer = new byte[_bufferSize];
                    try
                    {
                        int bytesReceived;
                        while (_socket.Available > 0 &&
                               (bytesReceived = _socket.Receive(buffer, 0, _bufferSize, SocketFlags.None)) > 0)
                        {
                            byte[] received = new byte[bytesReceived];
                            Buffer.BlockCopy(buffer, 0, received, 0, received.Length);
                            OnReceivedData(this, received);
                        }
                    }
                    catch (Exception e)
                    {
                        if (!_socket.Connected)
                        {
                            _logger.Error($"{Name} {e.Message}");
                        }
                        else
                        {
                            _logger.Exception(e);
                        }

                        Close();
                    }
                }

                Thread.Sleep(SocketPollTimeout);
            }

            _logger.Info($"{Name} ended.");
        }
    }
}