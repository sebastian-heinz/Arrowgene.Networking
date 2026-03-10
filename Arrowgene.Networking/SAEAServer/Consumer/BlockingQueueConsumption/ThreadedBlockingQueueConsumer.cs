using System;
using System.Collections.Concurrent;
using System.Threading;
using Arrowgene.Logging;

namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Dispatches consumer callbacks onto one worker thread per ordering lane while preserving lane order.
    /// </summary>
    public abstract class ThreadedBlockingQueueConsumer : IConsumer
    {
        private static readonly ILogger Logger = LogProvider.Logger(typeof(ThreadedBlockingQueueConsumer));

        private readonly BlockingCollection<IClientEvent>[] _queues;
        private readonly Thread[] _threads;
        private readonly int _maxUnitOfOrder;
        private volatile bool _isRunning;
        private readonly string _identity;

        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a threaded queue consumer.
        /// </summary>
        /// <param name="maxUnitOfOrder">The number of ordering lanes to process in parallel.</param>
        /// <param name="identity">The log identity used for worker threads.</param>
        public ThreadedBlockingQueueConsumer(int maxUnitOfOrder, string identity = "ThreadedBlockingQueueConsumer")
        {
            _maxUnitOfOrder = maxUnitOfOrder;
            _identity = identity;
            _queues = new BlockingCollection<IClientEvent>[_maxUnitOfOrder];
            _threads = new Thread[_maxUnitOfOrder];
        }

        /// <summary>
        /// Handles a received payload on the worker thread assigned to the client's ordering lane.
        /// </summary>
        /// <param name="clientHandle">The client that produced the payload.</param>
        /// <param name="data">The copied payload buffer.</param>
        protected abstract void HandleReceived(ClientHandle clientHandle, byte[] data);

        /// <summary>
        /// Handles a client disconnection on the worker thread assigned to the client's ordering lane.
        /// </summary>
        /// <param name="clientSnapshot">The final snapshot of the disconnected client.</param>
        protected abstract void HandleDisconnected(ClientSnapshot clientSnapshot);

        /// <summary>
        /// Handles a client connection on the worker thread assigned to the client's ordering lane.
        /// </summary>
        /// <param name="clientHandle">The connected client.</param>
        protected abstract void HandleConnected(ClientHandle clientHandle);

        /// <summary>
        /// Handles a consumer error on the worker thread assigned to the client's ordering lane.
        /// </summary>
        /// <param name="clientHandle">The client associated with the error.</param>
        /// <param name="exception">The exception that was thrown.</param>
        /// <param name="message">Additional context about where the error occurred.</param>
        protected abstract void HandleError(ClientHandle clientHandle, Exception exception, string message);

        private void Consume(int unitOfOrder)
        {
            while (_isRunning)
            {
                IClientEvent clientEvent;
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

                switch (clientEvent)
                {
                    case ClientDataEvent dataEvent:
                    {
                        HandleReceived(dataEvent.ClientHandle, dataEvent.Data);
                        break;
                    }
                    case ClientConnectedEvent connectedEvent:
                    {
                        HandleConnected(connectedEvent.ClientHandle);
                        break;
                    }
                    case ClientDisconnectedEvent disconnectedEvent:
                    {
                        HandleDisconnected(disconnectedEvent.ClientSnapshot);
                        break;
                    }
                    case ClientErrorEvent errorEvent:
                    {
                        HandleError(errorEvent.ClientHandle, errorEvent.Exception, errorEvent.Message);
                        break;
                    }
                    default:
                    {
                        throw new InvalidOperationException(
                            $"Unsupported client event type: {clientEvent.GetType().FullName}");
                    }
                }
            }
        }

        /// <summary>
        /// Starts the worker threads for all configured ordering lanes.
        /// </summary>
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
                _queues[i] = new BlockingCollection<IClientEvent>();
                _threads[i] = new Thread(() => Consume(uuo));
                _threads[i].Name = $"[{_identity}] Consumer: {i}";
                Logger.Info($"[{_identity}] Starting Consumer: {i}");
                _threads[i].Start();
            }
        }

        void IConsumer.OnReceivedData(ClientHandle clientHandle, byte[] data)
        {
            _queues[clientHandle.UnitOfOrder].Add(new ClientDataEvent(clientHandle, data));
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            _queues[clientSnapshot.UnitOfOrder].Add(new ClientDisconnectedEvent(clientSnapshot));
        }

        void IConsumer.OnClientConnected(ClientHandle clientHandle)
        {
            _queues[clientHandle.UnitOfOrder].Add(new ClientConnectedEvent(clientHandle));
        }

        void IConsumer.OnError(ClientHandle clientHandle, Exception exception, string message)
        {
            _queues[clientHandle.UnitOfOrder].Add(new ClientErrorEvent(clientHandle, exception, message));
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