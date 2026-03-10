using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace Arrowgene.Networking.Tests;

internal static class TestWait
{
    internal static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        if (condition())
        {
            return;
        }

        throw new XunitException(failureMessage);
    }
}
