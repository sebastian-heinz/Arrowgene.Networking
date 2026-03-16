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
    public ConsumerMetricsSnapshot(
        long handlerErrors,
        long[] queueDepthByLane,
        long[] eventsProcessed)
    {
        HandlerErrors = handlerErrors;
        QueueDepthByLane = queueDepthByLane ?? throw new ArgumentNullException(nameof(queueDepthByLane));
        EventsProcessed = eventsProcessed ?? throw new ArgumentNullException(nameof(eventsProcessed));
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
}
