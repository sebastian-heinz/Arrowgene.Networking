namespace Arrowgene.Networking.SAEAServer;

internal enum EnqueueResult
{
    Overflow = 0,
    Queued = 1,
    SendNow = 2
}
