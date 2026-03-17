using System.Net.Sockets;

namespace Arrowgene.Networking.SAEAServer;

internal interface ISendQueue
{
    EnqueueResult Enqueue(byte[] data, int offset, int length);

    bool TryGetNextChunk(SocketAsyncEventArgs sendEventArgs, out int chunkSize);

    bool CompleteSend(int bytesTransferred);

    void Reset();

    int QueuedBytes { get; }
}
