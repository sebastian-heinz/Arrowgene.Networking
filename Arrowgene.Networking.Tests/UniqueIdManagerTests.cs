using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Verifies packing and unpacking behavior for client unique identifiers.
/// </summary>
public sealed class UniqueIdManagerTests
{
    /// <summary>
    /// Verifies a packed unique ID round-trips the original client ID and generation.
    /// </summary>
    [Fact]
    public void Pack_RoundTripsClientIdAndGeneration()
    {
        const ushort clientId = 12345;
        const uint generation = 67890;

        long uniqueId = UniqueIdManager.Pack(clientId, generation);

        Assert.Equal(clientId, UniqueIdManager.GetClientId(uniqueId));
        Assert.Equal(generation, UniqueIdManager.GetGeneration(uniqueId));
    }

    /// <summary>
    /// Verifies distinct inputs produce distinct packed unique IDs.
    /// </summary>
    [Fact]
    public void Pack_DifferentInputsProduceDifferentValues()
    {
        long first = UniqueIdManager.Pack(1, 2);
        long second = UniqueIdManager.Pack(2, 2);
        long third = UniqueIdManager.Pack(1, 3);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
    }

    /// <summary>
    /// Verifies the full ushort and uint range round-trips without overflow.
    /// </summary>
    [Fact]
    public void Pack_SupportsMaximumValues()
    {
        const ushort clientId = ushort.MaxValue;
        const uint generation = uint.MaxValue;

        long uniqueId = UniqueIdManager.Pack(clientId, generation);

        Assert.Equal(clientId, UniqueIdManager.GetClientId(uniqueId));
        Assert.Equal(generation, UniqueIdManager.GetGeneration(uniqueId));
    }
}
