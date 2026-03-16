using Arrowgene.Networking.SAEAServer.Metric;

namespace Arrowgene.Networking.SAEAServer.Consumer;

/// <summary>
/// Implemented by consumers that expose operational metrics to the server metrics collector.
/// </summary>
public interface IConsumerMetrics
{
    /// <summary>
    /// Enables metric capture.
    /// </summary>
    void EnableCapture();

    /// <summary>
    /// Disables metric capture.
    /// </summary>
    void DisableCapture();

    /// <summary>
    /// Creates an immutable snapshot of the consumer's current metrics.
    /// </summary>
    /// <returns>The current consumer metrics snapshot.</returns>
    ConsumerMetricsSnapshot CreateSnapshot();
}
