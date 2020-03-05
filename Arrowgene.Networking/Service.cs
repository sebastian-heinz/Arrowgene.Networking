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
    }
}