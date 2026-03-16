using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arrowgene.Networking.SAEAServer;
using Arrowgene.Networking.SAEAServer.Consumer;

namespace Arrowgene.Networking.Tests;

internal sealed class DisconnectErrorRecordingConsumer : IConsumer
{
    private readonly object _sync;
    private readonly List<ClientHandle> _connectedClients;
    private readonly List<ClientSnapshot> _disconnectSnapshots;
    private readonly List<ClientSnapshot> _errorSnapshots;
    private readonly List<Exception> _errors;
    private readonly List<string> _messages;

    internal DisconnectErrorRecordingConsumer()
    {
        _sync = new object();
        _connectedClients = new List<ClientHandle>();
        _disconnectSnapshots = new List<ClientSnapshot>();
        _errorSnapshots = new List<ClientSnapshot>();
        _errors = new List<Exception>();
        _messages = new List<string>();
    }

    public void OnReceivedData(ClientHandle clientHandle, byte[] data)
    {
    }

    public void OnClientDisconnected(ClientSnapshot clientSnapshot)
    {
        lock (_sync)
        {
            _disconnectSnapshots.Add(clientSnapshot);
        }

        throw new InvalidOperationException("Intentional disconnect callback failure.");
    }

    public void OnClientConnected(ClientHandle clientHandle)
    {
        lock (_sync)
        {
            _connectedClients.Add(clientHandle);
        }
    }

    public void OnError(ClientSnapshot clientSnapshot, Exception exception, string message)
    {
        lock (_sync)
        {
            _errorSnapshots.Add(clientSnapshot);
            _errors.Add(exception);
            _messages.Add(message);
        }
    }

    internal ClientHandle GetConnectedClient(int index)
    {
        lock (_sync)
        {
            return _connectedClients[index];
        }
    }

    internal ClientSnapshot GetDisconnectSnapshot(int index)
    {
        lock (_sync)
        {
            return _disconnectSnapshots[index];
        }
    }

    internal ClientSnapshot GetErrorSnapshot(int index)
    {
        lock (_sync)
        {
            return _errorSnapshots[index];
        }
    }

    internal Exception GetError(int index)
    {
        lock (_sync)
        {
            return _errors[index];
        }
    }

    internal string GetMessage(int index)
    {
        lock (_sync)
        {
            return _messages[index];
        }
    }

    internal async Task WaitForConnectedCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => GetCount(_connectedClients) >= expected,
            timeout,
            $"Timed out waiting for {expected} connected clients."
        ).ConfigureAwait(false);
    }

    internal async Task WaitForDisconnectCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => GetCount(_disconnectSnapshots) >= expected,
            timeout,
            $"Timed out waiting for {expected} disconnect callbacks."
        ).ConfigureAwait(false);
    }

    internal async Task WaitForErrorCountAsync(int expected, TimeSpan timeout)
    {
        await TestWait.UntilAsync(
            () => GetCount(_errorSnapshots) >= expected,
            timeout,
            $"Timed out waiting for {expected} error callbacks."
        ).ConfigureAwait(false);
    }

    private int GetCount<T>(List<T> list)
    {
        lock (_sync)
        {
            return list.Count;
        }
    }
}
