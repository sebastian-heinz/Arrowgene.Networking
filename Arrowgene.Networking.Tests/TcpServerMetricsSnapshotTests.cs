using System;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for derived values on <see cref="TcpServerMetricsSnapshot"/>.
/// </summary>
public sealed class TcpServerMetricsSnapshotTests
{
    /// <summary>
    /// Verifies uptime is derived from the snapshot and server-start timestamps.
    /// </summary>
    [Fact]
    public void Constructor_DerivesUptimeFromTimestamps()
    {
        DateTime serverStartedAtUtc = new DateTime(2026, 3, 17, 0, 0, 0, DateTimeKind.Utc);
        DateTime timestampUtc = serverStartedAtUtc.AddMinutes(5);

        TcpServerMetricsSnapshot snapshot = CreateSnapshot(timestampUtc, serverStartedAtUtc);

        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Uptime);
    }

    /// <summary>
    /// Verifies pre-start snapshots with <see cref="DateTime.MinValue"/> start times still expose uptime without throwing.
    /// </summary>
    [Fact]
    public void Constructor_AllowsPreStartUptimeFromMinValue()
    {
        DateTime timestampUtc = new DateTime(2026, 3, 17, 0, 0, 0, DateTimeKind.Utc);

        TcpServerMetricsSnapshot snapshot = CreateSnapshot(timestampUtc, DateTime.MinValue);

        Assert.True(snapshot.Uptime > TimeSpan.Zero);
        Assert.Equal(timestampUtc - DateTime.MinValue, snapshot.Uptime);
    }

    private static TcpServerMetricsSnapshot CreateSnapshot(
        DateTime timestampUtc,
        DateTime serverStartedAtUtc)
    {
        return new TcpServerMetricsSnapshot(
            timestampUtc,
            serverStartedAtUtc,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0.0d,
            0.0d,
            0.0d,
            0.0d,
            0.0d,
            0,
            0,
            0,
            0,
            0,
            null,
            Array.Empty<long>(),
            Array.Empty<long>(),
            Array.Empty<long>(),
            Array.Empty<long>(),
            Array.Empty<long>(),
            Array.Empty<long>(),
            0
        );
    }
}
