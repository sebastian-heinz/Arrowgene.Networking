using System;
using System.Threading;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class ConsumerMetricsState
{
    private readonly long[] _consumerEventsProcessed;
    private readonly long[] _handlerDurationBuckets;
    private readonly long[] _receivedDataHandlerDurationBuckets;
    private readonly long[] _receivedDataQueueDelayBuckets;
    private int _captureEnabled;
    private long _consumerHandlerErrors;

    internal ConsumerMetricsState()
    {
        int latencyBucketCount = MetricBucketDefinitions.DurationBucketNames.Count;
        _consumerEventsProcessed = new long[Enum.GetValues<ClientEventType>().Length];
        _handlerDurationBuckets = new long[latencyBucketCount];
        _receivedDataQueueDelayBuckets = new long[latencyBucketCount];
        _receivedDataHandlerDurationBuckets = new long[latencyBucketCount];
    }

    internal int ConsumerEventTypeCount => _consumerEventsProcessed.Length;

    internal int HandlerDurationBucketsCount => _handlerDurationBuckets.Length;

    internal int ReceivedDataHandlerDurationBucketsCount => _receivedDataHandlerDurationBuckets.Length;

    internal int ReceivedDataQueueDelayBucketsCount => _receivedDataQueueDelayBuckets.Length;

    internal void EnableCapture()
    {
        Volatile.Write(ref _captureEnabled, 1);
    }

    internal void DisableCapture()
    {
        Volatile.Write(ref _captureEnabled, 0);
    }

    internal long GetConsumerHandlerErrors()
    {
        return Interlocked.Read(ref _consumerHandlerErrors);
    }

    internal void RecordProcessedEvent(ClientEventType clientEventType)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _consumerEventsProcessed[(int)clientEventType]);
    }

    internal void RecordHandlerDuration(long elapsedTicks)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(
            ref _handlerDurationBuckets[MetricBucketDefinitions.GetDurationBucketIndex(elapsedTicks)]
        );
    }

    internal void RecordReceivedDataHandlerDuration(long elapsedTicks)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(
            ref _receivedDataHandlerDurationBuckets[
                MetricBucketDefinitions.GetDurationBucketIndex(elapsedTicks)
            ]
        );
    }

    internal void RecordReceivedDataQueueDelay(long elapsedTicks)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(
            ref _receivedDataQueueDelayBuckets[
                MetricBucketDefinitions.GetDurationBucketIndex(elapsedTicks)
            ]
        );
    }

    internal void IncrementConsumerHandlerErrors()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _consumerHandlerErrors);
    }

    internal void CopyConsumerEventsProcessed(long[] destination)
    {
        CopyCounterArray(
            _consumerEventsProcessed,
            destination,
            "Destination must be at least as large as the consumer event counter array."
        );
    }

    internal void CopyHandlerDurationBuckets(long[] destination)
    {
        CopyCounterArray(
            _handlerDurationBuckets,
            destination,
            "Destination must be at least as large as the handler-duration counter array."
        );
    }

    internal void CopyReceivedDataHandlerDurationBuckets(long[] destination)
    {
        CopyCounterArray(
            _receivedDataHandlerDurationBuckets,
            destination,
            "Destination must be at least as large as the received-data handler-duration counter array."
        );
    }

    internal void CopyReceivedDataQueueDelayBuckets(long[] destination)
    {
        CopyCounterArray(
            _receivedDataQueueDelayBuckets,
            destination,
            "Destination must be at least as large as the received-data queue-delay counter array."
        );
    }

    private static void CopyCounterArray(long[] source, long[] destination, string lengthErrorMessage)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), lengthErrorMessage);
        }

        for (int index = 0; index < source.Length; index++)
        {
            destination[index] = Volatile.Read(ref source[index]);
        }
    }

    private bool IsCaptureEnabled()
    {
        return Volatile.Read(ref _captureEnabled) == 1;
    }
}
