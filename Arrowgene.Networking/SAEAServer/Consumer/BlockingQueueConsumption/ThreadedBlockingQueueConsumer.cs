using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.SAEAServer.Metric;

namespace Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption
{
    /// <summary>
    /// Dispatches consumer callbacks onto one worker thread per ordering lane while preserving lane order.
    /// </summary>
    public abstract class ThreadedBlockingQueueConsumer : IConsumer, ISupportsOrderingLaneCount, IConsumerMetrics, IDisposable
    {
        private const int DefaultQueueCapacityPerLane = 1024;
        private const int ThreadJoinTimeoutMs = 10000;

        private static readonly ILogger Logger = LogProvider.Logger(typeof(ThreadedBlockingQueueConsumer));

        private readonly object _lifecycleSync;
        private readonly BlockingCollection<IClientEvent>?[] _queues;
        private readonly Thread?[] _threads;
        private readonly int _orderingLaneCount;
        private readonly int _queueCapacityPerLane;
        private readonly ConsumerMetricsState _consumerMetricsState;
        private volatile bool _isRunning;
        private readonly string _identity;
        private bool _disposed;

        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Initializes a threaded queue consumer.
        /// </summary>
        /// <param name="orderingLaneCount">The number of ordering lanes to process in parallel.</param>
        /// <param name="identity">The log identity used for worker threads.</param>
        public ThreadedBlockingQueueConsumer(int orderingLaneCount, string identity = "ThreadedBlockingQueueConsumer")
            : this(orderingLaneCount, DefaultQueueCapacityPerLane, identity)
        {
        }

        /// <summary>
        /// Initializes a threaded queue consumer.
        /// </summary>
        /// <param name="orderingLaneCount">The number of ordering lanes to process in parallel.</param>
        /// <param name="queueCapacityPerLane">The maximum queued events allowed per ordering lane before producers backpressure.</param>
        /// <param name="identity">The log identity used for worker threads.</param>
        public ThreadedBlockingQueueConsumer(
            int orderingLaneCount,
            int queueCapacityPerLane,
            string identity = "ThreadedBlockingQueueConsumer"
        )
        {
            if (orderingLaneCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(orderingLaneCount));
            }

            if (queueCapacityPerLane <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(queueCapacityPerLane));
            }

            _lifecycleSync = new object();
            _orderingLaneCount = orderingLaneCount;
            _queueCapacityPerLane = queueCapacityPerLane;
            _identity = identity;
            _queues = new BlockingCollection<IClientEvent>[_orderingLaneCount];
            _threads = new Thread[_orderingLaneCount];
            _consumerMetricsState = new ConsumerMetricsState();
        }

        /// <inheritdoc />
        public int OrderingLaneCount => _orderingLaneCount;

        void IConsumerMetrics.EnableCapture()
        {
            _consumerMetricsState.EnableCapture();
        }

        void IConsumerMetrics.DisableCapture()
        {
            _consumerMetricsState.DisableCapture();
        }

        ConsumerMetricsSnapshot IConsumerMetrics.CreateSnapshot()
        {
            long[] queueDepthByLane = new long[_orderingLaneCount];
            long[] eventsProcessed = new long[_consumerMetricsState.ConsumerEventTypeCount];
            long[] handlerDurationBuckets = new long[_consumerMetricsState.HandlerDurationBucketsCount];
            SnapshotQueueDepthByLane(queueDepthByLane);
            _consumerMetricsState.CopyConsumerEventsProcessed(eventsProcessed);
            _consumerMetricsState.CopyHandlerDurationBuckets(handlerDurationBuckets);

            return new ConsumerMetricsSnapshot(
                _consumerMetricsState.GetConsumerHandlerErrors(),
                queueDepthByLane,
                eventsProcessed,
                handlerDurationBuckets
            );
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
        /// <param name="clientSnapshot">The immutable client snapshot associated with the error.</param>
        /// <param name="exception">The exception that was thrown.</param>
        /// <param name="message">Additional context about where the error occurred.</param>
        protected abstract void HandleError(ClientSnapshot clientSnapshot, Exception exception, string message);

        private void Consume(int unitOfOrder, CancellationToken cancellationToken)
        {
            BlockingCollection<IClientEvent>? queue = _queues[unitOfOrder];
            if (queue is null)
            {
                Logger.Error($"[{_identity}] Consumer queue {unitOfOrder} was not initialized.");
                return;
            }

            while (true)
            {
                IClientEvent clientEvent;
                try
                {
                    clientEvent = queue.Take(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    long startTimestamp = Stopwatch.GetTimestamp();

                    switch (clientEvent)
                    {
                        case ClientDataEvent dataEvent:
                            HandleReceived(dataEvent.ClientHandle, dataEvent.Data);
                            break;
                        case ClientConnectedEvent connectedEvent:
                            HandleConnected(connectedEvent.ClientHandle);
                            break;
                        case ClientDisconnectedEvent disconnectedEvent:
                            HandleDisconnected(disconnectedEvent.ClientSnapshot);
                            break;
                        case ClientErrorEvent errorEvent:
                            HandleError(errorEvent.ClientSnapshot, errorEvent.Exception, errorEvent.Message);
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unsupported client event type: {clientEvent.GetType().FullName}");
                    }

                    long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
                    _consumerMetricsState.RecordHandlerDuration(elapsedTicks);
                    _consumerMetricsState.RecordProcessedEvent(clientEvent.ClientEventType);
                }
                catch (Exception exception)
                {
                    _consumerMetricsState.IncrementConsumerHandlerErrors();
                    Logger.Error(
                        $"[{_identity}] Consumer handler failed on lane {unitOfOrder} for event {clientEvent.GetType().Name}."
                    );
                    Logger.Exception(exception);
                }
            }
        }

        internal void SnapshotQueueDepthByLane(long[] destination)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destination.Length < _queues.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(destination),
                    "Destination must be at least as large as the configured ordering lane count."
                );
            }

            lock (_lifecycleSync)
            {
                for (int index = 0; index < _queues.Length; index++)
                {
                    BlockingCollection<IClientEvent>? queue = _queues[index];
                    destination[index] = queue is null ? 0 : queue.Count;
                }
            }
        }

        /// <summary>
        /// Starts the worker threads for all configured ordering lanes.
        /// </summary>
        public virtual void Start()
        {
            lock (_lifecycleSync)
            {
                ThrowIfDisposed();

                if (_isRunning)
                {
                    Logger.Error($"[{_identity}] Consumer already running.");
                    return;
                }

                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource = cancellationTokenSource;

                for (int i = 0; i < _orderingLaneCount; i++)
                {
                    int unitOfOrder = i;
                    _queues[i] = new BlockingCollection<IClientEvent>(
                        new ConcurrentQueue<IClientEvent>(),
                        _queueCapacityPerLane
                    );
                    _threads[i] = new Thread(() => Consume(unitOfOrder, cancellationTokenSource.Token))
                    {
                        Name = $"[{_identity}] Consumer: {i}",
                        IsBackground = true
                    };
                }

                _isRunning = true;

                try
                {
                    for (int i = 0; i < _orderingLaneCount; i++)
                    {
                        Thread? thread = _threads[i];
                        if (thread is null)
                        {
                            continue;
                        }

                        Logger.Info($"[{_identity}] Starting Consumer: {i}");
                        thread.Start();
                    }
                }
                catch
                {
                    _isRunning = false;
                    _cancellationTokenSource = null;
                    cancellationTokenSource.Cancel();
                    cancellationTokenSource.Dispose();

                    for (int i = 0; i < _orderingLaneCount; i++)
                    {
                        _queues[i]?.Dispose();
                        _queues[i] = null;
                        _threads[i] = null;
                    }

                    throw;
                }
            }
        }

        void IConsumer.OnReceivedData(ClientHandle clientHandle, byte[] data)
        {
            EnqueueForHandle(clientHandle, new ClientDataEvent(clientHandle, data), nameof(IConsumer.OnReceivedData));
        }

        void IConsumer.OnClientDisconnected(ClientSnapshot clientSnapshot)
        {
            EnqueueForLane(
                clientSnapshot.UnitOfOrder,
                new ClientDisconnectedEvent(clientSnapshot),
                nameof(IConsumer.OnClientDisconnected)
            );
        }

        void IConsumer.OnClientConnected(ClientHandle clientHandle)
        {
            EnqueueForHandle(clientHandle, new ClientConnectedEvent(clientHandle), nameof(IConsumer.OnClientConnected));
        }

        void IConsumer.OnError(ClientSnapshot clientSnapshot, Exception exception, string message)
        {
            EnqueueForLane(
                clientSnapshot.UnitOfOrder,
                new ClientErrorEvent(clientSnapshot, exception, message),
                nameof(IConsumer.OnError)
            );
        }

        /// <summary>
        /// Stops the worker threads and releases all queue resources.
        /// </summary>
        public virtual void Stop()
        {
            Thread?[] threadsToJoin;
            BlockingCollection<IClientEvent>?[] queuesToDispose;
            CancellationTokenSource? cancellationTokenSource;

            lock (_lifecycleSync)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
                cancellationTokenSource = _cancellationTokenSource;
                _cancellationTokenSource = null;

                threadsToJoin = new Thread?[_threads.Length];
                Array.Copy(_threads, threadsToJoin, _threads.Length);

                queuesToDispose = new BlockingCollection<IClientEvent>?[_queues.Length];
                Array.Copy(_queues, queuesToDispose, _queues.Length);

                Array.Clear(_threads, 0, _threads.Length);
                Array.Clear(_queues, 0, _queues.Length);
            }

            cancellationTokenSource?.Cancel();

            for (int i = 0; i < _orderingLaneCount; i++)
            {
                Thread? consumerThread = threadsToJoin[i];
                Logger.Info($"[{_identity}] Shutting Consumer: {i} down...");
                Service.JoinThread(consumerThread, ThreadJoinTimeoutMs);
                Logger.Info($"[{_identity}] Consumer: {i} ended.");
            }

            for (int i = 0; i < queuesToDispose.Length; i++)
            {
                queuesToDispose[i]?.Dispose();
            }

            cancellationTokenSource?.Dispose();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
        }

        private void EnqueueForHandle(ClientHandle clientHandle, IClientEvent clientEvent, string source)
        {
            try
            {
                EnqueueForLane(clientHandle.UnitOfOrder, clientEvent, source);
            }
            catch (ObjectDisposedException)
            {
                Logger.Error($"[{_identity}] Dropping {source} event because the client handle is stale.");
            }
        }

        private void EnqueueForLane(int unitOfOrder, IClientEvent clientEvent, string source)
        {
            if ((uint)unitOfOrder >= (uint)_queues.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(unitOfOrder),
                    $"[{_identity}] {source} lane {unitOfOrder} is outside the configured range 0..{_queues.Length - 1}."
                );
            }

            if (!_isRunning)
            {
                return;
            }

            BlockingCollection<IClientEvent>? queue = _queues[unitOfOrder];
            CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;
            if (queue is null || cancellationTokenSource is null)
            {
                return;
            }

            try
            {
                queue.Add(clientEvent, cancellationTokenSource.Token);
            }
            catch (InvalidOperationException) when (!_isRunning)
            {
            }
            catch (OperationCanceledException) when (!_isRunning)
            {
            }
            catch (ObjectDisposedException) when (!_isRunning)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ThreadedBlockingQueueConsumer));
            }
        }
    }
}
