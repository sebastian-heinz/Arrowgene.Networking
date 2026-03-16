using System;
using System.Threading;

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
    private readonly int _orderingLaneCount;
    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _thread;
    private TcpServerMetricsSnapshot _latestSnapshot;
    private DateTime _previousTimestampUtc;
    private long _previousBytesReceived;
    private long _previousBytesSent;
    private bool _disposed;

    internal TcpServerMetricsCollector(
        TcpServerMetricsState metricsState,
        ClientRegistry clientRegistry,
        AcceptPool acceptPool,
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
        _orderingLaneCount = orderingLaneCount;
        _latestSnapshot = CreateSnapshot(DateTime.UtcNow, 0.0d, 0.0d);
        _previousTimestampUtc = _latestSnapshot.TimestampUtc;
        _previousBytesReceived = _latestSnapshot.BytesReceived;
        _previousBytesSent = _latestSnapshot.BytesSent;
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
            _previousTimestampUtc = now;
            _previousBytesReceived = _metricsState.GetBytesReceived();
            _previousBytesSent = _metricsState.GetBytesSent();
            PublishSnapshotNoLock(now, 0.0d, 0.0d);

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
            double elapsedSeconds = (now - _previousTimestampUtc).TotalSeconds;

            double receiveBytesPerSecond = 0.0d;
            double sendBytesPerSecond = 0.0d;

            if (elapsedSeconds > 0.0d)
            {
                receiveBytesPerSecond = (bytesReceived - _previousBytesReceived) / elapsedSeconds;
                sendBytesPerSecond = (bytesSent - _previousBytesSent) / elapsedSeconds;
            }

            PublishSnapshotNoLock(now, receiveBytesPerSecond, sendBytesPerSecond);
            _previousTimestampUtc = now;
            _previousBytesReceived = bytesReceived;
            _previousBytesSent = bytesSent;
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
        double sendBytesPerSecond)
    {
        long[] disconnectsByReason = new long[_metricsState.DisconnectReasonCount];
        long[] laneActiveConnections = new long[_orderingLaneCount];
        long[] receiveSizeBuckets = new long[_metricsState.ReceiveSizeBucketCount];
        long[] sendSizeBuckets = new long[_metricsState.SendSizeBucketCount];
        long[] socketErrorsByCode = new long[_metricsState.SocketErrorCodeCount];
        _metricsState.CopyDisconnectsByReason(disconnectsByReason);
        _metricsState.CopyReceiveSizeBuckets(receiveSizeBuckets);
        _metricsState.CopySendSizeBuckets(sendSizeBuckets);
        _metricsState.CopySocketErrorsByCode(socketErrorsByCode);
        _clientRegistry.SnapshotLaneLoads(laneActiveConnections);

        return new TcpServerMetricsSnapshot(
            timestampUtc,
            _metricsState.GetAcceptedConnections(),
            _metricsState.GetRejectedConnections(),
            _metricsState.GetActiveConnections(),
            _metricsState.GetDisconnectedConnections(),
            _metricsState.GetTimedOutConnections(),
            _metricsState.GetSendQueueOverflows(),
            _metricsState.GetSocketAcceptErrors(),
            _metricsState.GetSocketReceiveErrors(),
            _metricsState.GetSocketSendErrors(),
            _metricsState.GetReceiveOperations(),
            _metricsState.GetSendOperations(),
            _metricsState.GetBytesReceived(),
            _metricsState.GetBytesSent(),
            receiveBytesPerSecond,
            sendBytesPerSecond,
            _metricsState.GetInFlightAsyncCallbacks(),
            _metricsState.GetDisconnectCleanupQueueDepth(),
            _acceptPool.CurrentCount,
            _clientRegistry.GetAvailableClientSlotCount(),
            disconnectsByReason,
            laneActiveConnections,
            receiveSizeBuckets,
            sendSizeBuckets,
            socketErrorsByCode,
            _metricsState.SocketErrorCodeMinimum
        );
    }

    private void PublishSnapshotNoLock(
        DateTime timestampUtc,
        double receiveBytesPerSecond,
        double sendBytesPerSecond)
    {
        TcpServerMetricsSnapshot snapshot = CreateSnapshot(timestampUtc, receiveBytesPerSecond, sendBytesPerSecond);

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
