using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Arrowgene.Networking.SAEAServer.Metric;

/// <summary>
/// Provides the shared histogram bucket names and upper bounds used by server and consumer metrics.
/// </summary>
public static class MetricBucketDefinitions
{
    private static readonly TimeSpan[] _durationBucketUpperBoundsValues = new TimeSpan[]
    {
        TimeSpan.FromMicroseconds(100),
        TimeSpan.FromMicroseconds(500),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1)
    };

    private static readonly string[] _durationBucketNamesValues = new string[]
    {
        "0..100us",
        "100us..500us",
        "500us..1ms",
        "1ms..5ms",
        "5ms..10ms",
        "10ms..50ms",
        "50ms..100ms",
        "100ms..250ms",
        "250ms..500ms",
        "500ms..1s",
        "1s..2s",
        "2s..5s",
        "5s..10s",
        "10s..30s",
        "30s..1m",
        "1m..2m",
        "2m..5m",
        "5m..10m",
        "10m..30m",
        "30m..1h+"
    };

    private static readonly IReadOnlyList<TimeSpan> _durationBucketUpperBounds =
        Array.AsReadOnly(_durationBucketUpperBoundsValues);

    private static readonly IReadOnlyList<string> _durationBucketNames =
        Array.AsReadOnly(_durationBucketNamesValues);

    private static readonly long[] _durationBucketUpperBoundTimeSpanTicks =
        CreateDurationBucketUpperBoundTimeSpanTicks();

    private static readonly double[] _durationBucketUpperBoundStopwatchTicks =
        CreateDurationBucketUpperBoundStopwatchTicks();

    private static readonly int[] _transferSizeBucketUpperBoundsValues = new int[]
    {
        64,
        256,
        1024,
        4096,
        8192,
        16384,
        65536,
        262144,
        1048576,
        1048576
    };

    private static readonly string[] _transferSizeBucketNamesValues = new string[]
    {
        "0..64",
        "65..256",
        "257..1024",
        "1025..4096",
        "4097..8192",
        "8193..16384",
        "16385..65536",
        "65537..262144",
        "262145..1048576",
        "1048577+"
    };

    private static readonly IReadOnlyList<int> _transferSizeBucketUpperBounds =
        Array.AsReadOnly(_transferSizeBucketUpperBoundsValues);

    private static readonly IReadOnlyList<string> _transferSizeBucketNames =
        Array.AsReadOnly(_transferSizeBucketNamesValues);

    /// <summary>
    /// Gets the shared duration bucket names used by connection-duration and consumer-latency histograms.
    /// Values above the final upper bound remain in the last bucket.
    /// </summary>
    public static IReadOnlyList<string> DurationBucketNames => _durationBucketNames;

    /// <summary>
    /// Gets the shared inclusive duration bucket upper bounds used by connection-duration and consumer-latency histograms.
    /// Values above the final upper bound remain in the last bucket.
    /// </summary>
    public static IReadOnlyList<TimeSpan> DurationBucketUpperBounds => _durationBucketUpperBounds;

    /// <summary>
    /// Gets the shared transfer-size bucket names used by receive-size and send-size histograms.
    /// Values above the final upper bound remain in the last bucket.
    /// </summary>
    public static IReadOnlyList<string> TransferSizeBucketNames => _transferSizeBucketNames;

    /// <summary>
    /// Gets the shared inclusive transfer-size bucket upper bounds used by receive-size and send-size histograms.
    /// Values above the final upper bound remain in the last bucket.
    /// </summary>
    public static IReadOnlyList<int> TransferSizeBucketUpperBounds => _transferSizeBucketUpperBounds;

    internal static int GetDurationBucketIndex(TimeSpan duration)
    {
        long durationTicks = duration.Ticks;
        if (durationTicks < 0)
        {
            durationTicks = 0;
        }

        for (int index = 0; index < _durationBucketUpperBoundTimeSpanTicks.Length; index++)
        {
            if (durationTicks <= _durationBucketUpperBoundTimeSpanTicks[index])
            {
                return index;
            }
        }

        return _durationBucketUpperBoundTimeSpanTicks.Length - 1;
    }

    internal static int GetDurationBucketIndex(long elapsedStopwatchTicks)
    {
        double elapsedTicks = elapsedStopwatchTicks;
        if (elapsedTicks < 0.0d)
        {
            elapsedTicks = 0.0d;
        }

        for (int index = 0; index < _durationBucketUpperBoundStopwatchTicks.Length; index++)
        {
            if (elapsedTicks <= _durationBucketUpperBoundStopwatchTicks[index])
            {
                return index;
            }
        }

        return _durationBucketUpperBoundStopwatchTicks.Length - 1;
    }

    internal static int GetTransferSizeBucketIndex(int bytesTransferred)
    {
        for (int index = 0; index < _transferSizeBucketUpperBoundsValues.Length; index++)
        {
            if (bytesTransferred <= _transferSizeBucketUpperBoundsValues[index])
            {
                return index;
            }
        }

        return _transferSizeBucketUpperBoundsValues.Length - 1;
    }

    private static long[] CreateDurationBucketUpperBoundTimeSpanTicks()
    {
        long[] bounds = new long[_durationBucketUpperBoundsValues.Length];

        for (int index = 0; index < bounds.Length; index++)
        {
            bounds[index] = _durationBucketUpperBoundsValues[index].Ticks;
        }

        return bounds;
    }

    private static double[] CreateDurationBucketUpperBoundStopwatchTicks()
    {
        double[] bounds = new double[_durationBucketUpperBoundsValues.Length];

        for (int index = 0; index < bounds.Length; index++)
        {
            bounds[index] =
                _durationBucketUpperBoundsValues[index].TotalSeconds * Stopwatch.Frequency;
        }

        return bounds;
    }
}
