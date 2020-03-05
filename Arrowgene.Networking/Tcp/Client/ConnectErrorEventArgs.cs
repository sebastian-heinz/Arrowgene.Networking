// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System;
using System.Net;

namespace Arrowgene.Networking.Tcp.Client
{
    public class ConnectErrorEventArgs : EventArgs
    {
        public ConnectErrorEventArgs(ITcpClient client, string reason, IPAddress serverIpAddress, int serverPort, TimeSpan timeout)
        {
            Client = client;
            Reason = reason;
            ServerIpAddress = serverIpAddress;
            ServerPort = serverPort;
            Timeout = timeout;
        }

        public ITcpClient Client { get; }
        public string Reason { get; }
        public IPAddress ServerIpAddress { get; }
        public int ServerPort { get; }
        public TimeSpan Timeout { get; }
    }
}