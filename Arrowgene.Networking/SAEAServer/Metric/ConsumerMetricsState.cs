using System;
using System.Diagnostics;
using System.Threading;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class ConsumerMetricsState
{
    private const int HandlerDurationBucketCount = 10;
    private readonly long[] _consumerEventsProcessed;
    private readonly long[] _handlerDurationBuckets;
    private int _captureEnabled;
    private long _consumerHandlerErrors;

    internal ConsumerMetricsState()
    {
        _consumerEventsProcessed = new long[Enum.GetValues<ClientEventType>().Length];
        _handlerDurationBuckets = new long[HandlerDurationBucketCount];
    }

    internal int ConsumerEventTypeCount => _consumerEventsProcessed.Length;

    internal int HandlerDurationBucketsCount => _handlerDurationBuckets.Length;

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

        Interlocked.Increment(ref _handlerDurationBuckets[GetHandlerDurationBucketIndex(elapsedTicks)]);
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

    private static int GetHandlerDurationBucketIndex(long elapsedTicks)
    {
        double microseconds = (double)elapsedTicks / Stopwatch.Frequency * 1_000_000.0d;

        if (microseconds <= 100.0d)
        {
            return 0;
        }

        if (microseconds <= 1_000.0d)
        {
            return 1;
        }

        if (microseconds <= 10_000.0d)
        {
            return 2;
        }

        if (microseconds <= 50_000.0d)
        {
            return 3;
        }

        if (microseconds <= 250_000.0d)
        {
            return 4;
        }

        if (microseconds <= 1_000_000.0d)
        {
            return 5;
        }

        if (microseconds <= 5_000_000.0d)
        {
            return 6;
        }

        if (microseconds <= 30_000_000.0d)
        {
            return 7;
        }

        if (microseconds <= 120_000_000.0d)
        {
            return 8;
        }

        return 9;
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
