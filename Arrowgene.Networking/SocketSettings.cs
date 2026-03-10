using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.Serialization;
using Arrowgene.Logging;

namespace Arrowgene.Networking
{
    /// <summary>
    /// Defines socket configuration applied to listener or client sockets.
    /// </summary>
    [DataContract]
    public class SocketSettings : ICloneable
    {
        private static readonly ILogger Logger = LogProvider.Logger(typeof(SocketSettings));

        /// <summary>
        /// Initializes the default socket settings.
        /// </summary>
        public SocketSettings()
        {
            Backlog = 5;
            DualMode = false;
            ExclusiveAddressUse = false;
            NoDelay = false;
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

        /// <summary>
        /// Initializes a deep copy of an existing socket settings instance.
        /// </summary>
        /// <param name="socketSettings">The settings to copy.</param>
        public SocketSettings(SocketSettings socketSettings)
        {
            Backlog = socketSettings.Backlog;
            DualMode = socketSettings.DualMode;
            ExclusiveAddressUse = socketSettings.ExclusiveAddressUse;
            NoDelay = socketSettings.NoDelay;
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

        /// <summary>Gets or sets a value that specifies the size of the receive buffer of the <see cref="T:System.Net.Sockets.Socket"></see>.</summary>
        /// <returns>An <see cref="T:System.Int32"></see> that contains the size, in bytes, of the receive buffer. The default is 8192.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than 0.</exception>
        [DataMember(Order = 7)]
        public int ReceiveBufferSize { get; set; }

        /// <summary>Gets or sets a value that specifies the amount of time after which a synchronous <see cref="Socket.Receive(byte[])"></see> call will time out.</summary>
        /// <returns>The time-out value, in milliseconds. The default value is 0, which indicates an infinite time-out period. Specifying -1 also indicates an infinite time-out period.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than -1.</exception>
        [DataMember(Order = 8)]
        public int ReceiveTimeout { get; set; }

        /// <summary>Gets or sets a value that specifies the size of the send buffer of the <see cref="T:System.Net.Sockets.Socket"></see>.</summary>
        /// <returns>An <see cref="T:System.Int32"></see> that contains the size, in bytes, of the send buffer. The default is 8192.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The value specified for a set operation is less than 0.</exception>
        [DataMember(Order = 9)]
        public int SendBufferSize { get; set; }

        /// <summary>Gets or sets a value that specifies the amount of time after which a synchronous <see cref="Socket.Send(byte[])"></see> call will time out.</summary>
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

        /// <summary>Gets or sets a value that indicates whether the socket should linger after it is closed.</summary>
        [DataMember(Order = 12)]
        public bool LingerEnabled { get; set; }

        /// <summary>Gets or sets the linger duration in seconds when <see cref="LingerEnabled"/> is enabled.</summary>
        [DataMember(Order = 13)]
        public int LingerTime { get; set; }

        /// <summary>
        /// Gets or sets additional raw socket options applied after the strongly typed settings.
        /// </summary>
        [DataMember(Order = 14)]
        public List<SocketOption> SocketOptions { get; set; }

        /// <summary>
        /// Adds a raw socket option definition.
        /// </summary>
        /// <param name="optionLevel">The socket option level.</param>
        /// <param name="optionName">The socket option name.</param>
        /// <param name="optionValue">The socket option value.</param>
        public void AddSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, object optionValue)
        {
            AddSocketOption(new SocketOption(optionLevel, optionName, optionValue));
        }

        /// <summary>
        /// Adds a raw socket option definition.
        /// </summary>
        /// <param name="socketOption">The socket option to add.</param>
        public void AddSocketOption(SocketOption socketOption)
        {
            SocketOptions.Add(socketOption);
        }

        private void SetSocketOptions(Socket socket)
        {
            foreach (SocketOption option in SocketOptions)
            {
                try
                {
                    socket.SetSocketOption(option.Level, option.Name, option.Value);
                }
                catch (Exception)
                {
                    Logger.Debug(
                        $"Ignoring Socket Option: (Level:{option?.Level} Name:{option?.Name} Value:{option?.Value})");
                }
            }
        }

        /// <summary>
        /// Applies the configured settings and raw socket options to a socket.
        /// </summary>
        /// <param name="socket">The socket to configure.</param>
        public void ConfigureSocket(Socket socket)
        {
            if (socket.SocketType == SocketType.Dgram)
            {
                try
                {
                    socket.DontFragment = DontFragment;
                }
                catch (Exception)
                {
                    Logger.Debug("Ignoring Socket Setting: DontFragment");
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
                    Logger.Debug("Ignoring Socket Setting: DualMode");
                }
            }

            try
            {
                socket.ExclusiveAddressUse = ExclusiveAddressUse;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: ExclusiveAddressUse");
            }

            try
            {
                socket.LingerState = new LingerOption(LingerEnabled, LingerTime);
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: LingerState");
            }

            try
            {
                socket.NoDelay = NoDelay;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: NoDelay");
            }

            try
            {
                socket.ReceiveBufferSize = ReceiveBufferSize;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: ReceiveBufferSize");
            }


            try
            {
                socket.ReceiveTimeout = ReceiveTimeout;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: ReceiveTimeout");
            }


            try
            {
                socket.SendBufferSize = SendBufferSize;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: SendBufferSize");
            }

            try
            {
                socket.SendTimeout = SendTimeout;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: SendTimeout");
            }

            try
            {
                socket.Ttl = Ttl;
            }
            catch (Exception)
            {
                Logger.Debug("Ignoring Socket Setting: Ttl");
            }

            SetSocketOptions(socket);
        }

        
        /// <summary>
        /// Creates a deep copy of the socket settings.
        /// </summary>
        /// <returns>A copied <see cref="SocketSettings"/> instance.</returns>
        public object Clone()
        {
            return new SocketSettings(this);
        }
    }
}
