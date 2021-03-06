using System;
using System.Collections.Concurrent;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Server.AsyncEvent;

namespace Arrowgene.Networking.Tcp.Consumer.BlockingQueueConsumption
{
    /**
     * Consumer creates number of threads based on `AsyncEventSettings.MaxUnitOfOrder`.
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

        public ThreadedBlockingQueueConsumer(AsyncEventSettings socketSetting,
            string identity = "ThreadedBlockingQueueConsumer")
        {
            _maxUnitOfOrder = socketSetting.MaxUnitOfOrder;
            _identity = identity;
            _queues = new BlockingCollection<ClientEvent>[_maxUnitOfOrder];
            _threads = new Thread[_maxUnitOfOrder];
        }

        protected abstract void HandleReceived(ITcpSocket socket, byte[] data);
        protected abstract void HandleDisconnected(ITcpSocket socket);
        protected abstract void HandleConnected(ITcpSocket socket);

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
                        HandleReceived(clientEvent.Socket, clientEvent.Data);
                        break;
                    case ClientEventType.Connected:
                        HandleConnected(clientEvent.Socket);
                        break;
                    case ClientEventType.Disconnected:
                        HandleDisconnected(clientEvent.Socket);
                        break;
                }
            }
        }

        public virtual void OnStart()
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

        public virtual void OnStarted()
        {
        }

        void IConsumer.OnReceivedData(ITcpSocket socket, byte[] data)
        {
            _queues[socket.UnitOfOrder].Add(new ClientEvent(socket, ClientEventType.ReceivedData, data));
        }

        void IConsumer.OnClientDisconnected(ITcpSocket socket)
        {
            _queues[socket.UnitOfOrder].Add(new ClientEvent(socket, ClientEventType.Disconnected));
        }

        void IConsumer.OnClientConnected(ITcpSocket socket)
        {
            _queues[socket.UnitOfOrder].Add(new ClientEvent(socket, ClientEventType.Connected));
        }

        void IConsumer.OnStop()
        {
            Stop();
        }

        public virtual void OnStopped()
        {
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
                Service.JoinThread(consumerThread, 10000, Logger);
                Logger.Info($"[{_identity}] Consumer: {i} ended.");
                _threads[i] = null;
            }
        }

        public void LogStatus()
        {
            Logger.Info($"[{_identity}] _isRunning :{_isRunning}");
            for (int i = 0; i < _maxUnitOfOrder; i++)
            {
                Logger.Info($"[{_identity}] _threads[{i}].IsAlive:{_threads[i].IsAlive}");
                Logger.Info($"[{_identity}] _threads[{i}].ThreadState:{_threads[i].ThreadState}");
                Logger.Info($"[{_identity}] _queues[{i}].Count :{_queues[i].Count}");
            }
        }
    }
}