namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// Packs and unpacks the stable client identifier and generation into a single 64-bit value.
/// </summary>
public static class UniqueIdManager
{
    private const int ClientIdBitCount = 16;
    private const long ClientIdMask = (1L << ClientIdBitCount) - 1;

    /// <summary>
    /// Combines the client ID and generation into a single 64-bit integer.
    /// </summary>
    /// <param name="clientId">The stable pooled client identifier.</param>
    /// <param name="generation">The generation used to detect recycled clients.</param>
    /// <returns>A packed unique identifier that round-trips through <see cref="GetClientId"/> and <see cref="GetGeneration"/>.</returns>
    public static long Pack(ushort clientId, uint generation)
    {
        return ((long)generation << ClientIdBitCount) | clientId;
    }

    /// <summary>
    /// Extracts the client ID from a packed unique identifier.
    /// </summary>
    /// <param name="uniqueId">The packed unique identifier.</param>
    /// <returns>The unpacked client ID.</returns>
    public static ushort GetClientId(long uniqueId)
    {
        return checked((ushort)(uniqueId & ClientIdMask));
    }

    /// <summary>
    /// Extracts the generation from a packed unique identifier.
    /// </summary>
    /// <param name="uniqueId">The packed unique identifier.</param>
    /// <returns>The unpacked generation.</returns>
    public static uint GetGeneration(long uniqueId)
    {
        return checked((uint)(uniqueId >> ClientIdBitCount));
    }
}
