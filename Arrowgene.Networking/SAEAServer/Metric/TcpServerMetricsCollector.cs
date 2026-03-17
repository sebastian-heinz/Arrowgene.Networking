using System;
using System.Threading;
using Arrowgene.Networking.SAEAServer.Consumer;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class TcpServerMetricsCollector : IDisposable
{
    private const int SamplingIntervalMs = 1000;
    private const int ThreadJoinTimeoutMs = 10000;

    private readonly object _lifecycleSync;
    private readonly object _snapshotSync;
    private readonly TcpServerMetricsState _metricsState;
    private readonly ClientRegistry _clientRegistry;
    private readonly AcceptPool _acceptPool;
    private readonly IConsumerMetrics? _consumerMetrics;
    private readonly int _orderingLaneCount;
    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _thread;
    private TcpServerMetricsSnapshot _latestSnapshot;
    private DateTime _serverStartedAtUtc;
    private DateTime _previousTimestampUtc;
    private long _previousBytesReceived;
    private long _previousBytesSent;
    private long _previousReceiveOperations;
    private long _previousSendOperations;
    private long _previousAcceptedConnections;
    private long _snapshotSequenceNumber;
    private bool _disposed;

    internal TcpServerMetricsCollector(
        TcpServerMetricsState metricsState,
        ClientRegistry clientRegistry,
        AcceptPool acceptPool,
        IConsumerMetrics? consumerMetrics,
        int orderingLaneCount)
    {
        if (metricsState is null)
        {
            throw new ArgumentNullException(nameof(metricsState));
        }

        if (clientRegistry is null)
        {
            throw new ArgumentNullException(nameof(clientRegistry));
        }

        if (acceptPool is null)
        {
            throw new ArgumentNullException(nameof(acceptPool));
        }

        if (orderingLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderingLaneCount));
        }

        _lifecycleSync = new object();
        _snapshotSync = new object();
        _metricsState = metricsState;
        _clientRegistry = clientRegistry;
        _acceptPool = acceptPool;
        _consumerMetrics = consumerMetrics;
        _orderingLaneCount = orderingLaneCount;
        _serverStartedAtUtc = DateTime.MinValue;
        _snapshotSequenceNumber = 0;
        _latestSnapshot = CreateSnapshot(DateTime.UtcNow, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d);
        _previousTimestampUtc = _latestSnapshot.TimestampUtc;
        _previousBytesReceived = _latestSnapshot.BytesReceived;
        _previousBytesSent = _latestSnapshot.BytesSent;
        _previousReceiveOperations = _latestSnapshot.ReceiveOperations;
        _previousSendOperations = _latestSnapshot.SendOperations;
        _previousAcceptedConnections = _latestSnapshot.AcceptedConnections;
    }

    internal void Start(string threadName)
    {
        Thread metricsThread;

        lock (_lifecycleSync)
        {
            ThrowIfDisposed();

            if (_thread is not null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            _serverStartedAtUtc = now;
            _snapshotSequenceNumber = 0;
            _previousTimestampUtc = now;
            _previousBytesReceived = _metricsState.GetBytesReceived();
            _previousBytesSent = _metricsState.GetBytesSent();
            _previousReceiveOperations = _metricsState.GetReceiveOperations();
            _previousSendOperations = _metricsState.GetSendOperations();
            _previousAcceptedConnections = _metricsState.GetAcceptedConnections();
            PublishSnapshotNoLock(now, 0.0d, 0.0d, 0.0d, 0.0d, 0.0d);

            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            metricsThread = new Thread(() => Run(cancellationTokenSource.Token))
            {
                Name = threadName,
                IsBackground = true
            };

            _cancellationTokenSource = cancellationTokenSource;
            _thread = metricsThread;
        }

        metricsThread.Start();
    }

    internal void Stop()
    {
        Thread? metricsThread;
        CancellationTokenSource? cancellationTokenSource;

        lock (_lifecycleSync)
        {
            metricsThread = _thread;
            cancellationTokenSource = _cancellationTokenSource;
            _thread = null;
            _cancellationTokenSource = null;
        }

        cancellationTokenSource?.Cancel();
        Service.JoinThread(metricsThread, ThreadJoinTimeoutMs);
        cancellationTokenSource?.Dispose();
    }

    internal void CaptureSnapshot()
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposed();

            DateTime now = DateTime.UtcNow;
            long bytesReceived = _metricsState.GetBytesReceived();
            long bytesSent = _metricsState.GetBytesSent();
            long receiveOperations = _metricsState.GetReceiveOperations();
            long sendOperations = _metricsState.GetSendOperations();
            long acceptedConnections = _metricsState.GetAcceptedConnections();
            double elapsedSeconds = (now - _previousTimestampUtc).TotalSeconds;

            double receiveBytesPerSecond = 0.0d;
            double sendBytesPerSecond = 0.0d;
            double receiveOpsPerSecond = 0.0d;
            double sendOpsPerSecond = 0.0d;
            double acceptsPerSecond = 0.0d;

            if (elapsedSeconds > 0.0d)
            {
                receiveBytesPerSecond = (bytesReceived - _previousBytesReceived) / elapsedSeconds;
                sendBytesPerSecond = (bytesSent - _previousBytesSent) / elapsedSeconds;
                receiveOpsPerSecond = (receiveOperations - _previousReceiveOperations) / elapsedSeconds;
                sendOpsPerSecond = (sendOperations - _previousSendOperations) / elapsedSeconds;
                acceptsPerSecond = (acceptedConnections - _previousAcceptedConnections) / elapsedSeconds;
            }

            PublishSnapshotNoLock(
                now,
                receiveBytesPerSecond,
                sendBytesPerSecond,
                receiveOpsPerSecond,
                sendOpsPerSecond,
                acceptsPerSecond
            );
            _previousTimestampUtc = now;
            _previousBytesReceived = bytesReceived;
            _previousBytesSent = bytesSent;
            _previousReceiveOperations = receiveOperations;
            _previousSendOperations = sendOperations;
            _previousAcceptedConnections = acceptedConnections;
        }
    }

    internal TcpServerMetricsSnapshot GetSnapshot()
    {
        lock (_snapshotSync)
        {
            return _latestSnapshot;
        }
    }

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

    private void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.WaitHandle.WaitOne(SamplingIntervalMs))
            {
                return;
            }

            try
            {
                CaptureSnapshot();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private TcpServerMetricsSnapshot CreateSnapshot(
        DateTime timestampUtc,
        double receiveBytesPerSecond,
        double sendBytesPerSecond,
        double receiveOpsPerSecond,
        double sendOpsPerSecond,
        double acceptsPerSecond)
    {
        ConsumerMetricsSnapshot? consumerMetrics = _consumerMetrics?.CreateSnapshot();
        long snapshotSequenceNumber = _serverStartedAtUtc == DateTime.MinValue
            ? 0
            : Interlocked.Increment(ref _snapshotSequenceNumber);
        long activeConnections = _metricsState.GetActiveConnections();
        long peakActiveConnections = _metricsState.GetAndResetPeakActiveConnections(activeConnections);
        long[] disconnectsByReason = new long[_metricsState.DisconnectReasonCount];
        long[] laneActiveConnections = new long[_orderingLaneCount];
        long[] connectionDurationBuckets = new long[_metricsState.ConnectionDurationBucketsCount];
        long[] receiveSizeBuckets = new long[_metricsState.ReceiveSizeBucketCount];
        long[] sendSizeBuckets = new long[_metricsState.SendSizeBucketCount];
        long[] socketErrorsByCode = new long[_metricsState.SocketErrorCodeCount];
        _metricsState.CopyDisconnectsByReason(disconnectsByReason);
        _metricsState.CopyConnectionDurationBuckets(connectionDurationBuckets);
        _metricsState.CopyReceiveSizeBuckets(receiveSizeBuckets);
        _metricsState.CopySendSizeBuckets(sendSizeBuckets);
        _metricsState.CopySocketErrorsByCode(socketErrorsByCode);
        _clientRegistry.SnapshotLaneLoads(laneActiveConnections);

        return new TcpServerMetricsSnapshot(
            timestampUtc,
            _serverStartedAtUtc,
            snapshotSequenceNumber,
            _metricsState.GetAcceptedConnections(),
            _metricsState.GetRejectedConnections(),
            activeConnections,
            peakActiveConnections,
            _metricsState.GetDisconnectedConnections(),
            _metricsState.GetTimedOutConnections(),
            _metricsState.GetSendQueueOverflows(),
            _metricsState.GetSocketAcceptErrors(),
            _metricsState.GetSocketReceiveErrors(),
            _metricsState.GetSocketSendErrors(),
            _metricsState.GetZeroByteReceives(),
            _metricsState.GetReceiveOperations(),
            _metricsState.GetSendOperations(),
            _metricsState.GetBytesReceived(),
            _metricsState.GetBytesSent(),
            receiveBytesPerSecond,
            sendBytesPerSecond,
            receiveOpsPerSecond,
            sendOpsPerSecond,
            acceptsPerSecond,
            _metricsState.GetInFlightAsyncCallbacks(),
            _metricsState.GetDisconnectCleanupQueueDepth(),
            _acceptPool.CurrentCount,
            _clientRegistry.GetAvailableClientSlotCount(),
            consumerMetrics,
            disconnectsByReason,
            laneActiveConnections,
            connectionDurationBuckets,
            receiveSizeBuckets,
            sendSizeBuckets,
            socketErrorsByCode,
            _metricsState.SocketErrorCodeMinimum
        );
    }

    private void PublishSnapshotNoLock(
        DateTime timestampUtc,
        double receiveBytesPerSecond,
        double sendBytesPerSecond,
        double receiveOpsPerSecond,
        double sendOpsPerSecond,
        double acceptsPerSecond)
    {
        TcpServerMetricsSnapshot snapshot = CreateSnapshot(
            timestampUtc,
            receiveBytesPerSecond,
            sendBytesPerSecond,
            receiveOpsPerSecond,
            sendOpsPerSecond,
            acceptsPerSecond
        );

        lock (_snapshotSync)
        {
            _latestSnapshot = snapshot;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TcpServerMetricsCollector));
        }
    }
}
