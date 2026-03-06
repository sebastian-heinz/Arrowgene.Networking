namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal readonly struct SendBufferLease
{
    internal SendBufferLease(byte[] buffer, int length)
    {
        Buffer = buffer;
        Length = length;
    }

    internal byte[] Buffer { get; }

    internal int Length { get; }
}
