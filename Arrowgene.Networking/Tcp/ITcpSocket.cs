using System;
using System.Net;

namespace Arrowgene.Networking.Tcp
{
    public interface ITcpSocket
    {
        string Identity { get; }
        IPAddress RemoteIpAddress { get; }
        ushort Port { get; }

        /// <summary>
        /// Allows for distribution among multiple queues.
        /// </summary>
        int UnitOfOrder { get; }

        /// <summary>
        /// Ticks in ms of last recv operation.
        /// </summary>
        public long LastReadTicks { get; }
        
        /// <summary>
        /// Ticks in ms of last send operation.
        /// </summary>
        public long LastWriteTicks { get; }
        
        /// <summary>
        /// Timestamp when client connected
        /// </summary>
        public DateTime ConnectedAt { get; }
        
        /// <summary>
        /// Number of bytes received
        /// </summary>
        public ulong BytesReceived { get; }
        
        /// <summary>
        /// Number of bytes send
        /// </summary>
        public ulong BytesSend { get; }
        
        /// <summary>
        /// Determines if this socket can be used for send/recv.
        /// </summary>
        bool IsAlive { get; }

        void Send(byte[] data);
        void Close();
    }
}