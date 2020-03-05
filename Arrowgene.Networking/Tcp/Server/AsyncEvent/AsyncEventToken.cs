namespace Arrowgene.Networking.Tcp.Server.AsyncEvent
{
    public class AsyncEventToken
    {
        public byte[] Data { get; private set; }
        public AsyncEventClient Client { get; private set; }
        public int TransferredCount { get; private set; }
        public int OutstandingCount { get; private set; }

        public void Assign(AsyncEventClient client, byte[] data)
        {
            Client = client;
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
            Client = null;
            Data = null;
            OutstandingCount = 0;
            TransferredCount = 0;
        }
    }
}