using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.Serialization;
using Arrowgene.Logging;

namespace Arrowgene.Networking
{
    [DataContract]
    public class SocketSettings : ICloneable
    {
        public SocketSettings()
        {
            Backlog = 5;
            DualMode = false;
            ExclusiveAddressUse = false;
            NoDelay = false;
            UseOnlyOverlappedIo = false;
            ReceiveBufferSize = 8192;
            ReceiveTimeout = 0;
            SendBufferSize = 8192;
            SendTimeout = 0;
            DontFragment = true;
            Ttl = 32;
            LingerEnabled = false;
            LingerTime = 30;
            SocketOptions = new List<SocketOption>();
            AddSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        }

        public SocketSettings(SocketSettings socketSettings)
        {
            Backlog = socketSettings.Backlog;
            DualMode = socketSettings.DualMode;
            ExclusiveAddressUse = socketSettings.ExclusiveAddressUse;
            NoDelay = socketSettings.NoDelay;
            UseOnlyOverlappedIo = socketSettings.UseOnlyOverlappedIo;
            ReceiveBufferSize = socketSettings.ReceiveBufferSize;
            ReceiveTimeout = socketSettings.ReceiveTimeout;
            SendBufferSize = socketSettings.SendBufferSize;
            SendTimeout = socketSettings.SendTimeout;
            DontFragment = socketSettings.DontFragment;
            Ttl = socketSettings.Ttl;
            LingerEnabled = socketSettings.LingerEnabled;
            LingerTime = socketSettings.LingerTime;
            SocketOptions = new List<SocketOption>();
            foreach (SocketOption socketOption in socketSettings.SocketOptions)
            {
                SocketOptions.Add(new SocketOption(socketOption));
            }
        }

        /// <summary>The maximum length of the pending connections queue.</summary>
        [DataMember(Order = 1)]
        public int Backlog { get; set; }

        /// <summary>Gets or sets a <see cref="T:System.Boolean"></see> value that specifies whether the <see cref="T:System.Net.Sockets.Socket"></see> allows Internet Protocol (IP) datagrams to be fragmented.</summary>
        /// <returns>true if the <see cref="T:System.Net.Sockets.Socket"></see> allows datagram fragmentation; otherwise, false. The default is true.</returns>
        /// <exception cref="T:System.NotSupportedException">This property can be set only for sockets in the <see cref="F:System.Net.Sockets.AddressFamily.InterNetwork"></see> or <see cref="F:System.Net.Sockets.AddressFamily.InterNetworkV6"></see> families.</exception>
        [DataMember(Order = 2)]
        public bool DontFragment { get; set; }

        /// <summary>Gets or sets a <see cref="T:System.Boolean"></see> value that specifies whether the <see cref="T:System.Net.Sockets.Socket"></see> is a dual-mode socket used for both IPv4 and IPv6.</summary>
        /// <returns>true if the <see cref="T:System.Net.Sockets.Socket"></see> is a  dual-mode socket; otherwise, false. The default is false.</returns>
        [DataMember(Order = 3)]
        public bool DualMode { get; set; }

        /// <summary>Gets or sets a <see cref="T:System.Boolean"></see> value that specifies whether the <see cref="T:System.Net.Sockets.Socket"></see> allows only one process to bind to a port.</summary>
        /// <returns>true if the <see cref="T:System.Net.Sockets.Socket"></see> allows only one socket to bind to a specific port; otherwise, false. The default is true for Windows Server 2003 and Windows XP Service Pack 2, and false for all other versions.</returns>
        [DataMember(Order = 4)]
        public bool ExclusiveAddressUse { get; set; }

        /// <summary>Gets or sets a <see cref="T:System.Boolean"></see> value that specifies whether the stream <see cref="T:System.Net.Sockets.Socket"></see> is using the Nagle algorithm.</summary>
        /// <returns>false if the <see cref="T:System.Net.Sockets.Socket"></see> uses the Nagle algorithm; otherwise, true. The default is false.</returns>
        [DataMember(Order = 5)]
        public bool NoDelay { get; set; }

        /// <summary>Specifies whether the socket should only use Overlapped I/O mode.</summary>
        /// <returns>true if the <see cref="T:System.Net.Sockets.Socket"></see> uses only overlapped I/O; otherwise, false. The default is false.</returns>
        /// <exception cref="T:System.InvalidOperationException">The socket has been bound to a completion port.</exception>
        [DataMember(Order = 6)]
        public bool UseOnlyOverlappedIo { get; set; }

        /// <summary>Gets or sets a value that specifies the size of the receive buffer of the <see cref="T:System.Net.Sockets.Socket"></see>.</summary>
        /// <returns>An <see cref="T:System.Int32"></see> that contains the size, in bytes, of the receive buffer. The default is 8192.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than 0.</exception>
        [DataMember(Order = 7)]
        public int ReceiveBufferSize { get; set; }

        /// <summary>Gets or sets a value that specifies the amount of time after which a synchronous <see cref="Socket.Receive"></see> call will time out.</summary>
        /// <returns>The time-out value, in milliseconds. The default value is 0, which indicates an infinite time-out period. Specifying -1 also indicates an infinite time-out period.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than -1.</exception>
        [DataMember(Order = 8)]
        public int ReceiveTimeout { get; set; }

        /// <summary>Gets or sets a value that specifies the size of the send buffer of the <see cref="T:System.Net.Sockets.Socket"></see>.</summary>
        /// <returns>An <see cref="T:System.Int32"></see> that contains the size, in bytes, of the send buffer. The default is 8192.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than 0.</exception>
        [DataMember(Order = 9)]
        public int SendBufferSize { get; set; }

        /// <summary>Gets or sets a value that specifies the amount of time after which a synchronous <see cref="Socket.Send"></see> call will time out.</summary>
        /// <returns>The time-out value, in milliseconds. If you set the property with a value between 1 and 499, the value will be changed to 500. The default value is 0, which indicates an infinite time-out period. Specifying -1 also indicates an infinite time-out period.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than -1.</exception>
        [DataMember(Order = 10)]
        public int SendTimeout { get; set; }

        /// <summary>Gets or sets a value that specifies the Time To Live (TTL) value of Internet Protocol (IP) packets sent by the <see cref="T:System.Net.Sockets.Socket"></see>. The TTL value may be set to a value from 0 to 255. When this property is not set, the default TTL value for a socket is 32.</summary>
        /// <returns>The TTL value.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The TTL value can't be set to a negative number.</exception>
        /// <exception cref="T:System.NotSupportedException">This property can be set only for sockets in the <see cref="F:System.Net.Sockets.AddressFamily.InterNetwork"></see> or <see cref="F:System.Net.Sockets.AddressFamily.InterNetworkV6"></see> families.</exception>
        /// <exception cref="T:System.Net.Sockets.SocketException">An error occurred when attempting to access the socket. This error is also returned when an attempt was made to set TTL to a value higher than 255.</exception>
        [DataMember(Order = 11)]
        public short Ttl { get; set; }

        /// <summary>Gets or sets a value that specifies whether the <see cref="T:System.Net.Sockets.Socket"></see> will delay closing a socket in an attempt to send all pending data.</summary>
        /// <summary>Gets or sets a value that indicates whether to linger after the <see cref="T:System.Net.Sockets.Socket"></see> is closed.</summary>
        /// <returns>true if the <see cref="T:System.Net.Sockets.Socket"></see> should linger after <see cref="M:System.Net.Sockets.Socket.Close"></see> is called; otherwise, false.</returns>
        [DataMember(Order = 12)]
        public bool LingerEnabled { get; set; }

        /// <summary>Gets or sets a value that specifies whether the <see cref="T:System.Net.Sockets.Socket"></see> will delay closing a socket in an attempt to send all pending data.</summary>
        /// <summary>Gets or sets the amount of time to remain connected after calling the <see cref="M:System.Net.Sockets.Socket.Close"></see> method if data remains to be sent.</summary>
        /// <returns>The amount of time, in seconds, to remain connected after calling <see cref="M:System.Net.Sockets.Socket.Close"></see>.</returns>
        [DataMember(Order = 13)]
        public int LingerTime { get; set; }

        [DataMember(Order = 14)]
        public List<SocketOption> SocketOptions { get; set; }

        public void AddSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, object optionValue)
        {
            AddSocketOption(new SocketOption(optionLevel, optionName, optionValue));
        }

        public void AddSocketOption(SocketOption socketOption)
        {
            SocketOptions.Add(socketOption);
        }

        public void SetSocketOptions(Socket socket, ILogger logger = null)
        {
            foreach (SocketOption option in SocketOptions)
            {
                try
                {
                    socket.SetSocketOption(option.Level, option.Name, option.Value);
                }
                catch (Exception)
                {
                    logger?.Debug(
                        $"Ignoring Socket Option: (Level:{option?.Level} Name:{option?.Name} Value:{option?.Value})");
                }
            }
        }

        public void ConfigureSocket(Socket socket, ILogger logger = null)
        {
            if (socket.SocketType == SocketType.Dgram)
            {
                try
                {
                    socket.DontFragment = DontFragment;
                }
                catch (Exception)
                {
                    logger?.Debug("Ignoring Socket Setting: DontFragment");
                }
            }

            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                try
                {
                    socket.DualMode = DualMode;
                }
                catch (Exception)
                {
                    logger?.Debug("Ignoring Socket Setting: DualMode");
                }
            }

            try
            {
                socket.ExclusiveAddressUse = ExclusiveAddressUse;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: ExclusiveAddressUse");
            }

            try
            {
                socket.LingerState = new LingerOption(LingerEnabled, LingerTime);
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: LingerState");
            }

            try
            {
                socket.NoDelay = NoDelay;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: NoDelay");
            }

            try
            {
                socket.ReceiveBufferSize = ReceiveBufferSize;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: ReceiveBufferSize");
            }


            try
            {
                socket.ReceiveTimeout = ReceiveTimeout;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: ReceiveTimeout");
            }


            try
            {
                socket.SendBufferSize = SendBufferSize;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: SendBufferSize");
            }

            try
            {
                socket.SendTimeout = SendTimeout;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: SendTimeout");
            }

            try
            {
                socket.Ttl = Ttl;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: Ttl");
            }


            try
            {
                socket.UseOnlyOverlappedIO = UseOnlyOverlappedIo;
            }
            catch (Exception)
            {
                logger?.Debug("Ignoring Socket Setting: UseOnlyOverlappedIo");
            }

            SetSocketOptions(socket);
        }


        public object Clone()
        {
            return new SocketSettings(this);
        }
    }
}