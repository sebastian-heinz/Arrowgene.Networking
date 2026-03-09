using System;
using System.Collections.Concurrent;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.SAEAServer;

namespace Arrowgene.Networking.Consumer.BlockingQueueConsumption
{
    /**
     * Consumer creates number of threads based on `ServerSettings.MaxUnitOfOrder`.
     * Handle*-methods will be called from various threads with order of packets preserved.
     */
    public abstract class ThreadedBlockingQueueConsumer : IConsumer
    {
        private static readonly ILogger Logger = LogProvider.Logger(typeof(ThreadedBlockingQueueConsumer));

        private readonly BlockingCollection<ClientEvent>[] _queues;
        private readonly Thread[] _threads;
        private readonly int _maxUnitOfOrder;
        private volatile bool _isRunning;
        private readonly string _identity;

        private CancellationTokenSource _cancellationTokenSource;

        public ThreadedBlockingQueueConsumer(int maxUnitOfOrder,
            string identity = "ThreadedBlockingQueueConsumer")
        {
            _maxUnitOfOrder = maxUnitOfOrder;
            _identity = identity;
            _queues = new BlockingCollection<ClientEvent>[_maxUnitOfOrder];
            _threads = new Thread[_maxUnitOfOrder];
        }

        protected abstract void HandleReceived(ClientHandle? clientHandle, byte[] data);
        protected abstract void HandleDisconnected(ClientSnapshot? clientSnapshot);
        protected abstract void HandleConnected(ClientHandle? clientHandle);

        private void Consume(int unitOfOrder)
        {
            while (_isRunning)
            {
                ClientEvent clientEvent;
                try
                {
                    clientEvent = _queues[unitOfOrder].Take(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException ex)
                {
                    if (_isRunning)
                    {
                        Logger.Exception(ex);
                        Stop();
                    }

                    return;
                }

                switch (clientEvent.ClientEventType)
                {
                    case ClientEventType.ReceivedData:
                        HandleReceived(clientEvent.ClientHandle, clientEvent.Data);
                        break;
                    case ClientEventType.Connected:
                        HandleConnected(clientEvent.ClientHandle);
                        break;
                    case ClientEventType.Disconnected:
                        HandleDisconnected(clientEvent.ClientSnapshot);
                        break;
                }
            }
        }

        public virtual void Start()
        {
            if (_isRunning)
            {
                Logger.Error($"[{_identity}] Consumer already running.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            for (int i = 0; i < _maxUnitOfOrder; i++)
            {
                int uuo = i;
                _queues[i] = new BlockingCollection<ClientEvent>();
                _threads[i] = new Thread(() => Consume(uuo));
                _threads[i].Name = $"[{_identity}] Consumer: {i}";
                Logger.Info($"[{_identity}] Starting Consumer: {i}");
                _threads[i].Start();
            }
        }

        void IConsumer.OnReceivedData(ClientHandle socket, byte[] data)
        {
            _queues[socket.UnitOfOrder].Add(new ClientEvent(socket, null, ClientEventType.ReceivedData, data));
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            _queues[clientSnapshot.UnitOfOrder].Add(new ClientEvent(
                    null,
                    clientSnapshot,
                    ClientEventType.Disconnected
                )
            );
        }

        void IConsumer.OnClientConnected(ClientHandle socket)
        {
            _queues[socket.UnitOfOrder].Add(new ClientEvent(socket, null, ClientEventType.Connected));
        }

        void IConsumer.OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            throw exception;
        }

        private void Stop()
        {
            if (!_isRunning)
            {
                Logger.Error($"[{_identity}] Consumer already stopped.");
                return;
            }

            _isRunning = false;
            _cancellationTokenSource.Cancel();
            for (int i = 0; i < _maxUnitOfOrder; i++)
            {
                Thread consumerThread = _threads[i];
                Logger.Info($"[{_identity}] Shutting Consumer: {i} down...");
                Service.JoinThread(consumerThread, 10000);
                Logger.Info($"[{_identity}] Consumer: {i} ended.");
                _threads[i] = null;
            }
        }
    }
}