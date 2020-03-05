using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arrowgene.Networking.Udp
{
    /// <summary>
    /// Class for handling udp sending and receiving of packets.
    /// Call <see cref="StartReceive"/> before sending any data, to be able to receive a response.
    /// If you act as a server with <see cref="StartListen(IPEndPoint)"/>, there is no need to call <see cref="StartReceive"/>
    /// </summary>
    public class UdpSocket
    {
        /// <summary>
        /// Defines the maximum size to be received or send,
        /// drops requests exceeding limit.
        /// </summary>
        public const int DefaultMaxPayloadSizeBytes = 384;

        public int MaxPayloadSizeBytes { get; set; }

        private readonly Socket _socket;
        private readonly byte[] _buffer;
        private Thread _udpThread;
        private bool _receive;
        private bool _isBound;


        /// <summary>
        /// Creates a new instance of <see cref="UdpSocket"/>
        /// </summary>
        public UdpSocket() : this(DefaultMaxPayloadSizeBytes)
        {
        }

        public UdpSocket(int maxPayloadSizeBytes)
        {
            MaxPayloadSizeBytes = maxPayloadSizeBytes;
            _isBound = false;
            _receive = false;
            _buffer = new byte[MaxPayloadSizeBytes];
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        /// <summary>
        /// Occurs when data is received
        /// </summary>
        public event EventHandler<ReceivedUdpPacketEventArgs> ReceivedPacket;

        /// <summary>
        /// Listen for incomming data and start receiving
        /// </summary>
        /// <param name="remoteEp"></param>
        public void StartListen(IPEndPoint remoteEp)
        {
            if (!_isBound)
            {
                _socket.Bind(remoteEp);
                _isBound = true;
            }

            StartReceive();
        }

        /// <summary>
        /// Send data to an destination
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="remoteEp"></param>
        public void Send(byte[] buffer, EndPoint remoteEp)
        {
            SendTo(buffer, remoteEp);
        }

        /// <summary>
        /// Send data as broadcast.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="port"></param>
        public void SendBroadcast(byte[] buffer, ushort port)
        {
            SendToBroadcast(buffer, port);
        }

        /// <summary>
        /// Starts receiving data
        /// </summary>
        public void StartReceive()
        {
            StartReceiveThread();
        }

        /// <summary>
        /// Stops receiving any data
        /// </summary>
        public void StopReceive()
        {
            _receive = false;

            if (_udpThread != null)
            {
                if (Thread.CurrentThread != _udpThread)
                {
                    const int waitTimeout = 1000;

                    if (_udpThread.Join(waitTimeout))
                    {
                        Debug.WriteLine("UDPBase::Stop: Udp thread ended clean.");
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"UDPBase::Stop: Exceeded maximum timeout of {waitTimeout} ms, aborting thread...");
                        _udpThread.Abort();
                    }
                }
                else
                {
                    Debug.WriteLine(
                        "UDPBase::Stop: Tried to join udp thread from within udp thread, letting udp thread run out..");
                }
            }
        }

        /// <summary>
        /// Releases all ressources
        /// </summary>
        public void Dispose()
        {
            StopReceive();
            _socket.Close();
        }

        private void StartReceiveThread()
        {
            if (!_receive)
            {
                _receive = true;
                _udpThread = new Thread(Receive);
                _udpThread.Name = "UdpReceive";
                _udpThread.Start();
            }
        }

        private void Receive()
        {
            while (_receive)
            {
                if (_socket.Poll(10, SelectMode.SelectRead))
                {
                    // Create EndPoint, Senders information will be written to object.
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    EndPoint senderRemote = sender;

                    int read = _socket.ReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref senderRemote);

                    IPEndPoint remoteIpEndPoint = (IPEndPoint) senderRemote;

                    byte[] received = new byte[read];
                    Buffer.BlockCopy(_buffer, 0, received, 0, received.Length);
                    OnReceivedUdpPacket(read, received, remoteIpEndPoint);
                }
            }
        }

        private void SendTo(byte[] buffer, EndPoint remoteEp)
        {
            if (buffer.Length <= MaxPayloadSizeBytes)
            {
                _socket.SendTo(buffer, 0, buffer.Length, SocketFlags.None, remoteEp);
            }
            else
            {
                Debug.WriteLine($"UDPBase::SendTo: Exceeded maximum size of {MaxPayloadSizeBytes} byte");
            }
        }

        private void SendToBroadcast(byte[] buffer, ushort port)
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _socket.SendTo(buffer, 0, buffer.Length, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, port));
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, false);
        }

        private void OnReceivedUdpPacket(int receivedBytesCount, byte[] received, IPEndPoint remoteIpEndPoint)
        {
            EventHandler<ReceivedUdpPacketEventArgs> receivedBroadcast = ReceivedPacket;
            if (receivedBroadcast != null)
            {
                ReceivedUdpPacketEventArgs receivedProxyPacketEventArgs =
                    new ReceivedUdpPacketEventArgs(receivedBytesCount, received, remoteIpEndPoint);
                receivedBroadcast(this, receivedProxyPacketEventArgs);
            }
        }
    }
}