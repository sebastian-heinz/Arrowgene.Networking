using System;
using Arrowgene.Networking.SAEAServer.Metric;
using Xunit;

namespace Arrowgene.Networking.Tests;

/// <summary>
/// Unit coverage for the public metric bucket definitions.
/// </summary>
public sealed class MetricBucketDefinitionsTests
{
    /// <summary>
    /// Verifies the shared duration scale exposes the expected bucket names and bounds.
    /// </summary>
    [Fact]
    public void DurationBuckets_ExposeSharedTwentyBucketScale()
    {
        Assert.Equal(20, MetricBucketDefinitions.DurationBucketNames.Count);
        Assert.Equal(
            MetricBucketDefinitions.DurationBucketNames.Count,
            MetricBucketDefinitions.DurationBucketUpperBounds.Count
        );
        Assert.Equal("0..100us", MetricBucketDefinitions.DurationBucketNames[0]);
        Assert.Equal("30m..1h+", MetricBucketDefinitions.DurationBucketNames[19]);
        Assert.Equal(TimeSpan.FromMicroseconds(100), MetricBucketDefinitions.DurationBucketUpperBounds[0]);
        Assert.Equal(TimeSpan.FromHours(1), MetricBucketDefinitions.DurationBucketUpperBounds[19]);
    }

    /// <summary>
    /// Verifies the transfer-size scale exposes the expected bucket names and bounds.
    /// </summary>
    [Fact]
    public void TransferSizeBuckets_ExposeSharedTenBucketScale()
    {
        Assert.Equal(10, MetricBucketDefinitions.TransferSizeBucketNames.Count);
        Assert.Equal(
            MetricBucketDefinitions.TransferSizeBucketNames.Count,
            MetricBucketDefinitions.TransferSizeBucketUpperBounds.Count
        );
        Assert.Equal("0..64", MetricBucketDefinitions.TransferSizeBucketNames[0]);
        Assert.Equal("1048577+", MetricBucketDefinitions.TransferSizeBucketNames[9]);
        Assert.Equal(64, MetricBucketDefinitions.TransferSizeBucketUpperBounds[0]);
        Assert.Equal(1048576, MetricBucketDefinitions.TransferSizeBucketUpperBounds[9]);
    }
}
