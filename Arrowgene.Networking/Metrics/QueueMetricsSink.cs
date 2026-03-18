using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Arrowgene.Networking.Metrics;

/// <summary>
/// A thread-safe sink that enqueues every snapshot for external consumption.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot type to enqueue.</typeparam>
public sealed class QueueMetricsSink<TSnapshot> : IMetricsSink<TSnapshot>
{
    private readonly BlockingCollection<TSnapshot> _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMetricsSink{TSnapshot}"/> class with an unbounded queue.
    /// </summary>
    public QueueMetricsSink()
    {
        _queue = new BlockingCollection<TSnapshot>(new ConcurrentQueue<TSnapshot>());
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMetricsSink{TSnapshot}"/> class with a bounded queue.
    /// </summary>
    /// <param name="boundedCapacity">The maximum number of snapshots the queue can hold before <see cref="Write"/> blocks.</param>
    public QueueMetricsSink(int boundedCapacity)
    {
        _queue = new BlockingCollection<TSnapshot>(new ConcurrentQueue<TSnapshot>(), boundedCapacity);
    }

    /// <summary>
    /// Removes and returns the next snapshot, blocking until one is available or the sink is disposed.
    /// </summary>
    /// <param name="snapshot">The dequeued snapshot, or <c>default</c> if the sink has been disposed.</param>
    /// <param name="cancellationToken">An optional token to cancel the wait.</param>
    /// <returns><c>true</c> if a snapshot was dequeued; <c>false</c> if the sink has completed adding.</returns>
    public bool TryTake(out TSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        try
        {
            return _queue.TryTake(out snapshot!, -1, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            snapshot = default!;
            return false;
        }
    }

    /// <inheritdoc />
    public void Write(TSnapshot snapshot)
    {
        while (!_queue.TryAdd(snapshot))
        {
            _queue.TryTake(out _);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.CompleteAdding();
        _queue.Dispose();
    }
}
