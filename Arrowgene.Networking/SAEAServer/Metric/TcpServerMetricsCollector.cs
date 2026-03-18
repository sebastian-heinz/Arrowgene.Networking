using System;
using System.Threading;

namespace Arrowgene.Networking.SAEAServer.Metric;

public sealed class TcpServerMetricsCollector : IDisposable
{
    private const int SamplingIntervalMs = 1000;
    private const int ThreadJoinTimeoutMs = 10000;

    private readonly object _lifecycleSync;
    private readonly object _snapshotSync;
    private readonly IMetricsCapture<TcpServerMetricsSnapshot> _serverCapture;
    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _thread;
    private TcpServerMetricsSnapshot _latestSnapshot;
    private DateTime _previousTimestampUtc;
    private bool _disposed;

    public TcpServerMetricsCollector(IMetricsCapture<TcpServerMetricsSnapshot> serverCapture)
    {
        if (serverCapture is null)
        {
            throw new ArgumentNullException(nameof(serverCapture));
        }

        _lifecycleSync = new object();
        _snapshotSync = new object();
        _serverCapture = serverCapture;
        _previousTimestampUtc = DateTime.UtcNow;
        _latestSnapshot = default;
    }

    public void Start(string threadName)
    {
        Thread metricsThread;

        lock (_lifecycleSync)
        {
            ThrowIfDisposed();

            if (_thread is not null)
            {
                return;
            }

            _previousTimestampUtc = DateTime.UtcNow;
            PublishSnapshotNoLock(0.0d);

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

    public void Stop()
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

    public void CaptureSnapshot()
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposed();

            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _previousTimestampUtc).TotalSeconds;
            PublishSnapshotNoLock(elapsedSeconds);
            _previousTimestampUtc = now;
        }
    }

    public TcpServerMetricsSnapshot GetMetricsSnapshot()
    {
        try
        {
            CaptureSnapshot();
        }
        catch (ObjectDisposedException)
        {
        }

        return GetPublishedMetricsSnapshot();
    }

    public TcpServerMetricsSnapshot GetPublishedMetricsSnapshot()
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

    private void PublishSnapshotNoLock(double elapsedSeconds)
    {
        TcpServerMetricsSnapshot snapshot = _serverCapture.CreateSnapshot(elapsedSeconds);

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
