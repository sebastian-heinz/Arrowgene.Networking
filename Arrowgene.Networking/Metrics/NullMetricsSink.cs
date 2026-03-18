namespace Arrowgene.Networking.Metrics;

/// <summary>
/// A no-op sink that discards all snapshots.
/// </summary>
/// <typeparam name="TSnapshot">The snapshot type to discard.</typeparam>
public sealed class NullMetricsSink<TSnapshot> : IMetricsSink<TSnapshot>
{
    /// <inheritdoc />
    public void Write(TSnapshot snapshot)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
