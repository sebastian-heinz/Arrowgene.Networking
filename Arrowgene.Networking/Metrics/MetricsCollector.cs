using System;
using System.Threading;

namespace Arrowgene.Networking.Metrics;

/// <summary>
/// Periodically captures snapshots from an <see cref="IMetricsCapture{TSnapshot}"/> source on a background thread.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot type produced by the capture source.</typeparam>
public sealed class MetricsCollector<TSnapshot> : IDisposable
{
    private const int DefaultSamplingIntervalMs = 1000;
    private const int ThreadJoinTimeoutMs = 10000;

    private readonly object _lifecycleSync;
    private readonly object _snapshotSync;
    private readonly IMetricsCapture<TSnapshot> _capture;
    private readonly IMetricsSink<TSnapshot> _sink;
    private readonly int _samplingIntervalMs;
    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _thread;
    private TSnapshot _latestSnapshot;
    private DateTime _previousTimestampUtc;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsCollector{TSnapshot}"/> class with a <see cref="NullMetricsSink{TSnapshot}"/> and the default sampling interval.
    /// </summary>
    /// <param name="capture">The capture source that produces snapshots.</param>
    public MetricsCollector(IMetricsCapture<TSnapshot> capture)
        : this(capture, new NullMetricsSink<TSnapshot>())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsCollector{TSnapshot}"/> class.
    /// </summary>
    /// <param name="capture">The capture source that produces snapshots.</param>
    /// <param name="sink">The sink that receives every captured snapshot.</param>
    /// <param name="samplingIntervalMs">The sampling interval in milliseconds between automatic captures.</param>
    public MetricsCollector(IMetricsCapture<TSnapshot> capture, IMetricsSink<TSnapshot> sink,
        int samplingIntervalMs = DefaultSamplingIntervalMs)
    {
        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        if (samplingIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplingIntervalMs));
        }

        _lifecycleSync = new object();
        _snapshotSync = new object();
        _capture = capture;
        _sink = sink;
        _samplingIntervalMs = samplingIntervalMs;
        _previousTimestampUtc = DateTime.UtcNow;
        _latestSnapshot = default!;
    }

    /// <summary>
    /// Starts the background sampling thread.
    /// </summary>
    /// <param name="threadName">The name assigned to the sampling thread.</param>
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

    /// <summary>
    /// Stops the background sampling thread.
    /// </summary>
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

    /// <summary>
    /// Forces an immediate snapshot capture outside the normal sampling interval.
    /// </summary>
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

    /// <summary>
    /// Captures a fresh snapshot and returns it.
    /// </summary>
    /// <returns>The most recently captured snapshot.</returns>
    public TSnapshot GetMetricsSnapshot()
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

    /// <summary>
    /// Returns the latest published snapshot without forcing a new capture.
    /// </summary>
    /// <returns>The most recently published snapshot.</returns>
    public TSnapshot GetPublishedMetricsSnapshot()
    {
        lock (_snapshotSync)
        {
            return _latestSnapshot;
        }
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
        _sink.Dispose();
    }

    private void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.WaitHandle.WaitOne(_samplingIntervalMs))
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
        TSnapshot snapshot = _capture.CreateSnapshot(elapsedSeconds);

        lock (_snapshotSync)
        {
            _latestSnapshot = snapshot;
        }

        _sink.Write(snapshot);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MetricsCollector<TSnapshot>));
        }
    }
}