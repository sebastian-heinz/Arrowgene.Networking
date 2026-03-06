namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal enum SendQueueEnqueueResult
{
    Rejected,
    Queued,
    Started,
    Overflow
}
