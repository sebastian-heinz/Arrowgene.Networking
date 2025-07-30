using System;
using System.Collections.Generic;
using System.Threading;
using Arrowgene.Logging;

namespace Arrowgene.Networking
{
    internal static class Service
    {
        public static void JoinThread(Thread thread, int timeoutMs, ILogger logger)
        {
            if (thread != null)
            {
                logger.Info($"{thread.Name} - Thread: Shutting down...");
                if (!thread.IsAlive)
                {
                    logger.Info($"{thread.Name} - Thread: ended (not alive).");
                    return;
                }

                if (Thread.CurrentThread != thread)
                {
                    if (thread.Join(timeoutMs))
                    {
                        logger.Info($"{thread.Name} - Thread: ended.");
                    }
                    else
                    {
                        logger.Error(
                            $"{thread.Name} - Thread: Exceeded join timeout of {timeoutMs}MS, could not join.");
                    }
                }
                else
                {
                    logger.Debug(
                        $"{thread.Name} - Thread: Tried to join thread from within thread, already joined.");
                }
            }
        }
        //739461 days, 14 hours, 32 minutes, 24 seconds
        public static string GetHumanReadableDuration(TimeSpan timeSpan)
        {
            var parts = new List<string>();
            if (timeSpan.Days > 0)
                parts.Add($"{timeSpan.Days} day{(timeSpan.Days == 1 ? "" : "s")}");
            if (timeSpan.Hours > 0)
                parts.Add($"{timeSpan.Hours} hour{(timeSpan.Hours == 1 ? "" : "s")}");
            if (timeSpan.Minutes > 0)
                parts.Add($"{timeSpan.Minutes} minute{(timeSpan.Minutes == 1 ? "" : "s")}");
            if (timeSpan.Seconds > 0)
                parts.Add($"{timeSpan.Seconds} second{(timeSpan.Seconds == 1 ? "" : "s")}");
            if (parts.Count == 0)
                return "0 seconds";
            return string.Join(", ", parts);
        }
        
        private static readonly string[] SizeSuffixes = 
            { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };

        /// <summary>
        /// Converts a long representing bytes into a human-readable string (e.g., "1.23 MB").
        /// </summary>
        /// <param name="value">The number of bytes.</param>
        /// <returns>A human-readable string representation of the byte size.</returns>
        public static string GetHumanReadableSize(ulong value)
        {
            if (value == 0)
            {
                return "0.0 bytes"; // Handles zero bytes
            }

            int mag = (int)Math.Log(value, 1024);
            decimal adjustedSize = (decimal)value / (1L << (mag * 10));

            return $"{adjustedSize:n2} {SizeSuffixes[mag]}";
        }
    }
}