using System;

namespace Arrowgene.Networking.Metrics;

/// <summary>
/// Receives every snapshot produced by a <see cref="MetricsCollector{TSnapshot}"/>.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot type to consume.</typeparam>
public interface IMetricsSink<in TSnapshot> : IDisposable
{
    /// <summary>
    /// Called for each captured snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to process.</param>
    void Write(TSnapshot snapshot);
}
