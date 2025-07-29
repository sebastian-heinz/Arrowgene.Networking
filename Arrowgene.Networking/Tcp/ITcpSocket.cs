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
        /// Timestamp of last recv operation.
        /// </summary>
        public DateTime LastRead { get; }
        
        /// <summary>
        /// Timestamp of last send operation.
        /// </summary>
        public DateTime LastWrite { get; }
        
        /// <summary>
        /// Timestamp when client connected
        /// </summary>
        public DateTime ConnectedAt { get; }
        
        /// <summary>
        /// Determines if this socket can be used for send/recv.
        /// </summary>
        bool IsAlive { get; }

        void Send(byte[] data);
        void Close();
    }
}