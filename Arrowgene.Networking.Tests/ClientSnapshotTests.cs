using System;
using System.Net;
using System.Reflection;
using Arrowgene.Networking.SAEAServer;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Verifies immutable snapshot behavior.
/// </summary>
public sealed class ClientSnapshotTests
{
    /// <summary>
    /// Verifies the packed unique identifier is captured from the constructor inputs.
    /// </summary>
    [Fact]
    public void Constructor_CapturesUniqueId()
    {
        const ushort clientId = 321;
        const uint generation = 654u;

        ClientSnapshot snapshot = new ClientSnapshot(
            clientId,
            generation,
            "[127.0.0.1:1234]",
            IPAddress.Loopback,
            1234,
            true,
            DateTime.UtcNow,
            10,
            20,
            30,
            40,
            0,
            1
        );

        Assert.Equal(UniqueIdManager.Pack(clientId, generation), snapshot.UniqueId);
    }

    /// <summary>
    /// Verifies snapshot properties cannot be reassigned after construction.
    /// </summary>
    [Fact]
    public void PublicProperties_DoNotExposeSetters()
    {
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.ClientId));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Generation));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Identity));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.RemoteIpAddress));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.Port));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.IsAlive));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.ConnectedAt));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.LastReadMs));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.LastWriteMs));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.BytesReceived));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.BytesSent));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.PendingOperations));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.UnitOfOrder));
        AssertPropertyIsReadOnly(nameof(ClientSnapshot.UniqueId));
    }

    private static void AssertPropertyIsReadOnly(string propertyName)
    {
        PropertyInfo property = typeof(ClientSnapshot).GetProperty(propertyName)!;
        Assert.Null(property.SetMethod);
    }
}
