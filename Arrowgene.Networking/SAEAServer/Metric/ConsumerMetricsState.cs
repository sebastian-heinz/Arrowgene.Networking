using System;
using System.Threading;
using Arrowgene.Networking.SAEAServer.Consumer.BlockingQueueConsumption;

namespace Arrowgene.Networking.SAEAServer.Metric;

internal sealed class ConsumerMetricsState
{
    private readonly long[] _consumerEventsProcessed;
    private int _captureEnabled;
    private long _consumerHandlerErrors;

    internal ConsumerMetricsState()
    {
        _consumerEventsProcessed = new long[Enum.GetValues<ClientEventType>().Length];
    }

    internal int ConsumerEventTypeCount => _consumerEventsProcessed.Length;

    internal void EnableCapture()
    {
        Volatile.Write(ref _captureEnabled, 1);
    }

    internal void DisableCapture()
    {
        Volatile.Write(ref _captureEnabled, 0);
    }

    internal long GetConsumerHandlerErrors()
    {
        return Interlocked.Read(ref _consumerHandlerErrors);
    }

    internal void RecordProcessedEvent(ClientEventType clientEventType)
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _consumerEventsProcessed[(int)clientEventType]);
    }

    internal void IncrementConsumerHandlerErrors()
    {
        if (!IsCaptureEnabled())
        {
            return;
        }

        Interlocked.Increment(ref _consumerHandlerErrors);
    }

    internal void CopyConsumerEventsProcessed(long[] destination)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length < _consumerEventsProcessed.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                "Destination must be at least as large as the consumer event counter array."
            );
        }

        for (int index = 0; index < _consumerEventsProcessed.Length; index++)
        {
            destination[index] = Volatile.Read(ref _consumerEventsProcessed[index]);
        }
    }

    private bool IsCaptureEnabled()
    {
        return Volatile.Read(ref _captureEnabled) == 1;
    }
}
