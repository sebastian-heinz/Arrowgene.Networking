using System;
using System.Threading;
using Arrowgene.Networking.Metrics;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;
using Arrowgene.Networking.SAEAServer.Metric;

namespace Arrowgene.Networking.Tests;

internal sealed class MetricsCaptureAwareTest : IConsumer, IMetricsCapture<ConsumerMetricsSnapshot>
{
    private const int HandlerDurationBucketCount = 10;
    private readonly long[] _queueDepthByLane;
    private readonly long[] _eventsProcessed;
    private int _captureEnabled;
    private int _enableCaptureCount;
    private int _disableCaptureCount;
    private int _connectedCount;
    private int _disconnectedCount;
    private long _handlerErrors;

    internal MetricsCaptureAwareTest(int metricsLaneCount)
    {
        if (metricsLaneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metricsLaneCount));
        }

        _queueDepthByLane = new long[metricsLaneCount];
        _eventsProcessed = new long[Enum.GetValues<ClientEventType>().Length];
    }

    internal int EnableCaptureCount => Volatile.Read(ref _enableCaptureCount);

    internal int DisableCaptureCount => Volatile.Read(ref _disableCaptureCount);

    internal int ConnectedCount => Volatile.Read(ref _connectedCount);

    internal int DisconnectedCount => Volatile.Read(ref _disconnectedCount);

    void IMetricsCapture.EnableCapture()
    {
        Interlocked.Increment(ref _enableCaptureCount);
        Volatile.Write(ref _captureEnabled, 1);
    }

    void IMetricsCapture.DisableCapture()
    {
        Interlocked.Increment(ref _disableCaptureCount);
        Volatile.Write(ref _captureEnabled, 0);
    }

    ConsumerMetricsSnapshot IMetricsCapture<ConsumerMetricsSnapshot>.CreateSnapshot(double elapsedSeconds)
    {
        long[] queueDepthByLane = new long[_queueDepthByLane.Length];
        long[] eventsProcessed = new long[_eventsProcessed.Length];
        long[] handlerDurationBuckets = new long[HandlerDurationBucketCount];

        CopyQueueDepthByLane(queueDepthByLane);
        CopyEventsProcessed(eventsProcessed);

        return new ConsumerMetricsSnapshot(
            Interlocked.Read(ref _handlerErrors),
            queueDepthByLane,
            eventsProcessed,
            handlerDurationBuckets
        );
    }

    public void OnReceivedData(ClientHandle clientHandle, byte[] data)
    {
        RecordProcessedEvent(ClientEventType.ReceivedData);
    }

    public void OnClientDisconnected(ClientSnapshot clientSnapshot)
    {
        Interlocked.Increment(ref _disconnectedCount);
        RecordProcessedEvent(ClientEventType.Disconnected);
    }

    public void OnClientConnected(ClientHandle clientHandle)
    {
        Interlocked.Increment(ref _connectedCount);
        RecordProcessedEvent(ClientEventType.Connected);
    }

    public void OnError(ClientSnapshot clientSnapshot, Exception exception, string message)
    {
        RecordProcessedEvent(ClientEventType.Error);
    }

    internal void SetQueueDepth(int lane, long depth)
    {
        if ((uint)lane >= (uint)_queueDepthByLane.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(lane));
        }

        Interlocked.Exchange(ref _queueDepthByLane[lane], depth);
    }

    internal void SetHandlerErrors(long handlerErrors)
    {
        Interlocked.Exchange(ref _handlerErrors, handlerErrors);
    }

    private void RecordProcessedEvent(ClientEventType clientEventType)
    {
        if (Volatile.Read(ref _captureEnabled) != 1)
        {
            return;
        }

        Interlocked.Increment(ref _eventsProcessed[(int)clientEventType]);
    }

    private void CopyQueueDepthByLane(long[] destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < _queueDepthByLane.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        for (int index = 0; index < _queueDepthByLane.Length; index++)
        {
            destination[index] = Volatile.Read(ref _queueDepthByLane[index]);
        }
    }

    private void CopyEventsProcessed(long[] destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < _eventsProcessed.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        for (int index = 0; index < _eventsProcessed.Length; index++)
        {
            destination[index] = Volatile.Read(ref _eventsProcessed[index]);
        }
    }
}
