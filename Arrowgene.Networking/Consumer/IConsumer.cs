using System;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer
{
    public interface IConsumer
    {
        void OnReceivedData(ClientHandle clientHandle, byte[] data);
        void OnClientDisconnected(ClientHandle clientHandle, ClientSnapshot clientSnapshot);
        void OnClientConnected(ClientHandle clientHandle);
        void OnError(ClientHandle clientHandle, Exception exception, string message);
    }
}