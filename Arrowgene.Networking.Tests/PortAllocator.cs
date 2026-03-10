using System;
using System.Net;
using System.Net.Sockets;

namespace Arrowgene.Networking.Tests;

internal static class PortAllocator
{
    internal static ushort GetFreeTcpPort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }
}
