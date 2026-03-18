namespace Arrowgene.Networking.SAEAServer.Metric;

/// <summary>
/// Provides enable/disable control over metric capture.
/// </summary>
public interface IMetricsCapture
{
    /// <summary>
    /// Enables metric capture.
    /// </summary>
    void EnableCapture();

    /// <summary>
    /// Disables metric capture.
    /// </summary>
    void DisableCapture();
}

/// <summary>
/// Extends <see cref="IMetricsCapture"/> with a typed snapshot.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot type produced by the implementer.</typeparam>
public interface IMetricsCapture<out TSnapshot> : IMetricsCapture
{
    /// <summary>
    /// Creates an immutable snapshot of the current metrics.
    /// </summary>
    /// <param name="elapsedSeconds">The elapsed seconds since the previous snapshot, used for rate calculations.</param>
    /// <returns>The current metrics snapshot.</returns>
    TSnapshot CreateSnapshot(double elapsedSeconds);
}
