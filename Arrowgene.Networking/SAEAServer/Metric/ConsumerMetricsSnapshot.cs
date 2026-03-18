using System;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.SAEAServer.Metric;

/// <summary>
/// Represents an immutable point-in-time view of consumer metrics.
/// </summary>
public readonly struct ConsumerMetricsSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerMetricsSnapshot"/> struct.
    /// </summary>
    /// <param name="handlerErrors">The total number of handler errors recorded by the consumer.</param>
    /// <param name="queueDepthByLane">Consumer queue depths indexed by ordering lane.</param>
    /// <param name="eventsProcessed">Consumer event counters indexed by <see cref="ClientEventType"/>.</param>
    /// <param name="handlerDurationBuckets">Consumer handler duration buckets using the fixed latency histogram ranges.</param>
    public ConsumerMetricsSnapshot(
        long handlerErrors,
        long[] queueDepthByLane,
        long[] eventsProcessed,
        long[] handlerDurationBuckets)
    {
        HandlerErrors = handlerErrors;
        QueueDepthByLane = CloneArray(queueDepthByLane, nameof(queueDepthByLane));
        EventsProcessed = CloneArray(eventsProcessed, nameof(eventsProcessed));
        HandlerDurationBuckets = CloneArray(handlerDurationBuckets, nameof(handlerDurationBuckets));
    }

    /// <summary>
    /// Gets the total number of handler errors recorded by the consumer.
    /// </summary>
    public long HandlerErrors { get; }

    /// <summary>
    /// Gets consumer queue depths indexed by ordering lane.
    /// </summary>
    public ReadOnlyMemory<long> QueueDepthByLane { get; }

    /// <summary>
    /// Gets consumer event counters indexed by <see cref="ClientEventType"/>.
    /// </summary>
    public ReadOnlyMemory<long> EventsProcessed { get; }

    /// <summary>
    /// Gets handler duration histogram buckets using the ranges 0..100us, 100us..1ms, 1..10ms, 10..50ms, 50..250ms, 250ms..1s, 1..5s, 5..30s, 30s..2m, and 2m+.
    /// </summary>
    public ReadOnlyMemory<long> HandlerDurationBuckets { get; }

    /// <summary>
    /// Gets the count recorded for a specific consumer event type.
    /// </summary>
    /// <param name="clientEventType">The consumer event type to query.</param>
    /// <returns>The number of times the event type has been processed in the snapshot.</returns>
    public long GetEventsProcessedCount(ClientEventType clientEventType)
    {
        int index = (int)clientEventType;
        if ((uint)index >= (uint)EventsProcessed.Length)
        {
            return 0;
        }

        return EventsProcessed.Span[index];
    }

    private static long[] CloneArray(long[] source, string paramName)
    {
        if (source is null)
        {
            throw new ArgumentNullException(paramName);
        }

        return (long[])source.Clone();
    }
}
