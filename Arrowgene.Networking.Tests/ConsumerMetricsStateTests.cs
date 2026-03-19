using System;
using System.Collections.Generic;
using System.Diagnostics;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for mutable consumer metrics state.
/// </summary>
public sealed class ConsumerMetricsStateTests
{
    private static readonly int DurationBucketCount = MetricBucketDefinitions.DurationBucketNames.Count;

    /// <summary>
    /// Verifies handler durations are bucketed at the documented latency boundaries.
    /// </summary>
    [Fact]
    public void RecordHandlerDuration_TracksBucketBoundaries()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();
        state.EnableCapture();

        long[] samples = CreateDurationBoundarySamples();

        for (int index = 0; index < samples.Length; index++)
        {
            state.RecordHandlerDuration(samples[index]);
        }

        long[] handlerDurationBuckets = new long[state.HandlerDurationBucketsCount];
        state.CopyHandlerDurationBuckets(handlerDurationBuckets);

        Assert.Equal(CreateDurationBoundaryBucketCounts(), handlerDurationBuckets);
        Assert.Equal(samples.Length, GetCounterTotal(handlerDurationBuckets));
    }

    /// <summary>
    /// Verifies handler durations are ignored when capture is disabled.
    /// </summary>
    [Fact]
    public void RecordHandlerDuration_DoesNotTrackWhenCaptureIsDisabled()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();

        TimeSpan sampleDuration = TimeSpan.FromMilliseconds(10);
        state.RecordHandlerDuration(GetElapsedTicksAtOrBelow(sampleDuration));
        state.EnableCapture();
        state.RecordHandlerDuration(GetElapsedTicksAtOrBelow(sampleDuration));
        state.DisableCapture();
        state.RecordHandlerDuration(GetElapsedTicksAbove(TimeSpan.FromSeconds(1)));

        long[] handlerDurationBuckets = new long[state.HandlerDurationBucketsCount];
        state.CopyHandlerDurationBuckets(handlerDurationBuckets);

        Assert.Equal(CreateSingleBucketCounterArray(sampleDuration), handlerDurationBuckets);
        Assert.Equal(1, GetCounterTotal(handlerDurationBuckets));
    }

    /// <summary>
    /// Verifies received-data queue delays and handler durations are bucketed at the documented latency boundaries.
    /// </summary>
    [Fact]
    public void ReceivedDataLatencyMetrics_TrackBucketBoundaries()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();
        state.EnableCapture();

        long[] samples = CreateDurationBoundarySamples();

        for (int index = 0; index < samples.Length; index++)
        {
            state.RecordReceivedDataQueueDelay(samples[index]);
            state.RecordReceivedDataHandlerDuration(samples[index]);
        }

        long[] receivedDataQueueDelayBuckets = new long[state.ReceivedDataQueueDelayBucketsCount];
        long[] receivedDataHandlerDurationBuckets = new long[state.ReceivedDataHandlerDurationBucketsCount];
        state.CopyReceivedDataQueueDelayBuckets(receivedDataQueueDelayBuckets);
        state.CopyReceivedDataHandlerDurationBuckets(receivedDataHandlerDurationBuckets);

        Assert.Equal(CreateDurationBoundaryBucketCounts(), receivedDataQueueDelayBuckets);
        Assert.Equal(CreateDurationBoundaryBucketCounts(), receivedDataHandlerDurationBuckets);
        Assert.Equal(samples.Length, GetCounterTotal(receivedDataQueueDelayBuckets));
        Assert.Equal(samples.Length, GetCounterTotal(receivedDataHandlerDurationBuckets));
    }

    /// <summary>
    /// Verifies received-data queue delays and handler durations are ignored when capture is disabled.
    /// </summary>
    [Fact]
    public void ReceivedDataLatencyMetrics_DoNotTrackWhenCaptureIsDisabled()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();

        TimeSpan sampleDuration = TimeSpan.FromMilliseconds(10);
        state.RecordReceivedDataQueueDelay(GetElapsedTicksAtOrBelow(sampleDuration));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAtOrBelow(sampleDuration));
        state.EnableCapture();
        state.RecordReceivedDataQueueDelay(GetElapsedTicksAtOrBelow(sampleDuration));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAtOrBelow(sampleDuration));
        state.DisableCapture();
        state.RecordReceivedDataQueueDelay(GetElapsedTicksAbove(TimeSpan.FromSeconds(1)));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAbove(TimeSpan.FromSeconds(1)));

        long[] receivedDataQueueDelayBuckets = new long[state.ReceivedDataQueueDelayBucketsCount];
        long[] receivedDataHandlerDurationBuckets = new long[state.ReceivedDataHandlerDurationBucketsCount];
        state.CopyReceivedDataQueueDelayBuckets(receivedDataQueueDelayBuckets);
        state.CopyReceivedDataHandlerDurationBuckets(receivedDataHandlerDurationBuckets);

        Assert.Equal(CreateSingleBucketCounterArray(sampleDuration), receivedDataQueueDelayBuckets);
        Assert.Equal(CreateSingleBucketCounterArray(sampleDuration), receivedDataHandlerDurationBuckets);
        Assert.Equal(1, GetCounterTotal(receivedDataQueueDelayBuckets));
        Assert.Equal(1, GetCounterTotal(receivedDataHandlerDurationBuckets));
    }

    private static long[] CreateDurationBoundarySamples()
    {
        IReadOnlyList<TimeSpan> upperBounds = MetricBucketDefinitions.DurationBucketUpperBounds;
        long[] samples = new long[upperBounds.Count + 1];

        for (int index = 0; index < upperBounds.Count; index++)
        {
            samples[index] = GetElapsedTicksAtOrBelow(upperBounds[index]);
        }

        samples[samples.Length - 1] = GetElapsedTicksAbove(upperBounds[upperBounds.Count - 1]);
        return samples;
    }

    private static long[] CreateDurationBoundaryBucketCounts()
    {
        long[] counters = new long[DurationBucketCount];

        for (int index = 0; index < counters.Length; index++)
        {
            counters[index] = 1;
        }

        counters[counters.Length - 1]++;
        return counters;
    }

    private static long[] CreateSingleBucketCounterArray(TimeSpan duration)
    {
        long[] counters = new long[DurationBucketCount];
        int bucketIndex = MetricBucketDefinitions.GetDurationBucketIndex(duration);
        counters[bucketIndex] = 1;
        return counters;
    }

    private static long GetElapsedTicksAtOrBelow(TimeSpan duration)
    {
        double microseconds = duration.TotalMicroseconds;
        long elapsedTicks = (long)Math.Floor(microseconds * Stopwatch.Frequency / 1_000_000.0d);

        while (elapsedTicks > 0 && GetMicroseconds(elapsedTicks) > microseconds)
        {
            elapsedTicks--;
        }

        return elapsedTicks;
    }

    private static long GetElapsedTicksAbove(TimeSpan duration)
    {
        double microseconds = duration.TotalMicroseconds;
        long elapsedTicks = (long)Math.Floor(microseconds * Stopwatch.Frequency / 1_000_000.0d) + 1L;

        while (GetMicroseconds(elapsedTicks) <= microseconds)
        {
            elapsedTicks++;
        }

        return elapsedTicks;
    }

    private static double GetMicroseconds(long elapsedTicks)
    {
        return (double)elapsedTicks / Stopwatch.Frequency * 1_000_000.0d;
    }

    private static long GetCounterTotal(long[] counters)
    {
        long total = 0;

        for (int index = 0; index < counters.Length; index++)
        {
            total += counters[index];
        }

        return total;
    }
}
