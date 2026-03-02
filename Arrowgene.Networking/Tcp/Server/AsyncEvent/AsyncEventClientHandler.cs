using System;
using System.Net;

namespace Arrowgene.Networking.Tcp.Server.AsyncEvent;

public readonly struct AsyncEventClientHandle(AsyncEventClient client, int runGeneration, int generation) : ITcpSocket
{
    public AsyncEventClient Client
    {
        get
        {
            if (client.Generation != Generation)
                throw new ObjectDisposedException("Handle is stale: The object has been recycled.");
            return client;
        }
    }

    public readonly int RunGeneration = runGeneration;
    public readonly int Generation = generation;
    
    public string Identity => Client.Identity;

    public IPAddress RemoteIpAddress => Client.RemoteIpAddress;

    public ushort Port => Client.Port;

    public int UnitOfOrder => Client.UnitOfOrder;

    public DateTime LastRead => Client.LastRead;

    public DateTime LastWrite => Client.LastWrite;

    public DateTime ConnectedAt => Client.ConnectedAt;

    public ulong BytesReceived => Client.BytesReceived;

    public ulong BytesSend => Client.BytesSend;

    public bool IsAlive => Client.IsAlive;

    public void Send(byte[] data)
    {
        Client.Send(data);
    }

    public void Close()
    {
        Client.Close();
    }
}