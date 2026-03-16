namespace Arrowgene.Networking.SAEAServer.Consumer;

/// <summary>
/// Implemented by consumers that require a minimum number of ordering lanes to operate correctly.
/// </summary>
public interface ISupportsOrderingLaneCount
{
    /// <summary>
    /// Gets the number of ordering lanes this consumer is configured to handle.
    /// </summary>
    int OrderingLaneCount { get; }
}
