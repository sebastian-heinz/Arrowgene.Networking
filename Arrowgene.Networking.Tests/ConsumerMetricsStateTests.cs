using System;
using System.Diagnostics;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for mutable consumer metrics state.
/// </summary>
public sealed class ConsumerMetricsStateTests
{
    /// <summary>
    /// Verifies handler durations are bucketed at the documented latency boundaries.
    /// </summary>
    [Fact]
    public void RecordHandlerDuration_TracksBucketBoundaries()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();
        state.EnableCapture();

        long[] samples = new long[]
        {
            GetElapsedTicksAtOrBelowMicroseconds(100.0d),
            GetElapsedTicksAtOrBelowMicroseconds(1_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(10_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(50_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(250_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(1_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(5_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(30_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(120_000_000.0d),
            GetElapsedTicksAboveMicroseconds(120_000_000.0d)
        };

        for (int index = 0; index < samples.Length; index++)
        {
            state.RecordHandlerDuration(samples[index]);
        }

        long[] handlerDurationBuckets = new long[state.HandlerDurationBucketsCount];
        state.CopyHandlerDurationBuckets(handlerDurationBuckets);

        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, handlerDurationBuckets);
        Assert.Equal(samples.Length, GetCounterTotal(handlerDurationBuckets));
    }

    /// <summary>
    /// Verifies handler durations are ignored when capture is disabled.
    /// </summary>
    [Fact]
    public void RecordHandlerDuration_DoesNotTrackWhenCaptureIsDisabled()
    {
        ConsumerMetricsState state = new ConsumerMetricsState();

        state.RecordHandlerDuration(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.EnableCapture();
        state.RecordHandlerDuration(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.DisableCapture();
        state.RecordHandlerDuration(GetElapsedTicksAboveMicroseconds(1_000_000.0d));

        long[] handlerDurationBuckets = new long[state.HandlerDurationBucketsCount];
        state.CopyHandlerDurationBuckets(handlerDurationBuckets);

        Assert.Equal(new long[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, handlerDurationBuckets);
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

        long[] samples = new long[]
        {
            GetElapsedTicksAtOrBelowMicroseconds(100.0d),
            GetElapsedTicksAtOrBelowMicroseconds(1_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(10_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(50_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(250_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(1_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(5_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(30_000_000.0d),
            GetElapsedTicksAtOrBelowMicroseconds(120_000_000.0d),
            GetElapsedTicksAboveMicroseconds(120_000_000.0d)
        };

        for (int index = 0; index < samples.Length; index++)
        {
            state.RecordReceivedDataQueueDelay(samples[index]);
            state.RecordReceivedDataHandlerDuration(samples[index]);
        }

        long[] receivedDataQueueDelayBuckets = new long[state.ReceivedDataQueueDelayBucketsCount];
        long[] receivedDataHandlerDurationBuckets = new long[state.ReceivedDataHandlerDurationBucketsCount];
        state.CopyReceivedDataQueueDelayBuckets(receivedDataQueueDelayBuckets);
        state.CopyReceivedDataHandlerDurationBuckets(receivedDataHandlerDurationBuckets);

        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, receivedDataQueueDelayBuckets);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, receivedDataHandlerDurationBuckets);
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

        state.RecordReceivedDataQueueDelay(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.EnableCapture();
        state.RecordReceivedDataQueueDelay(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAtOrBelowMicroseconds(10_000.0d));
        state.DisableCapture();
        state.RecordReceivedDataQueueDelay(GetElapsedTicksAboveMicroseconds(1_000_000.0d));
        state.RecordReceivedDataHandlerDuration(GetElapsedTicksAboveMicroseconds(1_000_000.0d));

        long[] receivedDataQueueDelayBuckets = new long[state.ReceivedDataQueueDelayBucketsCount];
        long[] receivedDataHandlerDurationBuckets = new long[state.ReceivedDataHandlerDurationBucketsCount];
        state.CopyReceivedDataQueueDelayBuckets(receivedDataQueueDelayBuckets);
        state.CopyReceivedDataHandlerDurationBuckets(receivedDataHandlerDurationBuckets);

        Assert.Equal(new long[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, receivedDataQueueDelayBuckets);
        Assert.Equal(new long[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, receivedDataHandlerDurationBuckets);
        Assert.Equal(1, GetCounterTotal(receivedDataQueueDelayBuckets));
        Assert.Equal(1, GetCounterTotal(receivedDataHandlerDurationBuckets));
    }

    private static long GetElapsedTicksAtOrBelowMicroseconds(double microseconds)
    {
        long elapsedTicks = (long)Math.Floor(microseconds * Stopwatch.Frequency / 1_000_000.0d);

        while (elapsedTicks > 0 && GetMicroseconds(elapsedTicks) > microseconds)
        {
            elapsedTicks--;
        }

        return elapsedTicks;
    }

    private static long GetElapsedTicksAboveMicroseconds(double microseconds)
    {
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
