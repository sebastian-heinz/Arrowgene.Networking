namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventWriteState
    {
        public byte[] Data { get; private set; }
        public int TransferredCount { get; private set; }
        public int OutstandingCount { get; private set; }

        public void Assign(byte[] data)
        {
            Data = data;
            OutstandingCount = data.Length;
            TransferredCount = 0;
        }

        public void Update(int transferredCount)
        {
            TransferredCount += transferredCount;
            OutstandingCount -= transferredCount;
        }

        public void Reset()
        {
            Data = null;
            OutstandingCount = 0;
            TransferredCount = 0;
        }
    }
}