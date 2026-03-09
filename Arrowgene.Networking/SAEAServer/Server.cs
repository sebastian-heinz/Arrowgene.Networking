/*
 * MIT License
 *
 * Copyright (c) 2017-2026 Sebastian Heinz <sebastian.heinz.gt@googlemail.com>
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Consumer;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// A pooled TCP server built on <see cref="SocketAsyncEventArgs"/>.
/// </summary>
public sealed class Server : IDisposable
{
    private const string UnknownIdentity = "[Unknown Client]";
    private const string AcceptThreadName = "Server";
    private const string TimeoutThreadName = "AsyncEventServer_Timeout";
    private const int ThreadTimeoutMs = 10000;
    private const int MinSocketTimeoutDelayMs = 1000;
    private const int MaxSocketTimeoutDelayMs = 30000;

    private static readonly ILogger Logger = LogProvider.Logger(typeof(Server));

    private readonly object _lifecycleLock;
    private readonly IConsumer _consumer;
    private readonly ServerSettings _settings;
    private readonly AcceptPool _acceptPool;
    private readonly BufferSlab _bufferSlab;
    private readonly ClientRegistry _clientRegistry;
    private readonly TimeSpan _socketTimeout;
    private readonly int _bufferSize;
    private readonly string _identity;
    private readonly CancellationTokenSource _cancellation;
    private readonly Thread _acceptThread;
    private readonly Thread _timeoutThread;
    private Socket? _listenSocket;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private volatile bool _isStopped;

    public IPAddress IpAddress { get; }

    public ushort Port { get; }

    /// <summary>
    /// Creates a server with explicit settings.
    /// </summary>
    /// <param name="ipAddress">The IP address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="consumer">The consumer that receives callbacks.</param>
    /// <param name="settings">The server settings.</param>
    public Server(IPAddress ipAddress, ushort port, IConsumer consumer, ServerSettings settings)
    {
        if (ipAddress is null)
        {
            throw new ArgumentNullException(nameof(ipAddress));
        }

        if (consumer is null)
        {
            throw new ArgumentNullException(nameof(consumer));
        }

        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        settings.Validate();

        IpAddress = ipAddress;
        Port = port;
        _consumer = consumer;
        _settings = new ServerSettings(settings);
        _bufferSize = settings.BufferSize;

        _lifecycleLock = new object();
        _socketTimeout = TimeSpan.FromSeconds(_settings.ClientSocketTimeoutSeconds);
        _identity = string.IsNullOrEmpty(_settings.Identity) ? string.Empty : $"[{_settings.Identity}] ";
        _cancellation = new CancellationTokenSource();
        _bufferSlab = new BufferSlab(_settings.MaxConnections, _bufferSize);
        _clientRegistry = new ClientRegistry(
            _settings.MaxConnections,
            _settings.OrderingLaneCount,
            CreateClient
        );
        _acceptPool = new AcceptPool(_settings.ConcurrentAccepts, AcceptCompleted);

        _isDisposed = false;
        _isRunning = false;
        _isStopped = false;

        _timeoutThread = new Thread(CheckSocketTimeout)
        {
            Name = $"{_identity}{TimeoutThreadName}",
            IsBackground = true
        };
        _acceptThread = new Thread(Run)
        {
            Name = $"{_identity}{AcceptThreadName}",
            IsBackground = true
        };
    }

    public void ServerStart()
    {
        Log(LogLevel.Info, nameof(ServerStart), "Starting server...");

        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                Log(LogLevel.Error, nameof(ServerStart), "Server is disposed.");
                return;
            }

            if (_isStopped)
            {
                Log(LogLevel.Error, nameof(ServerStart), "Server has been stopped. Restart is not supported.");
                return;
            }

            if (_isRunning)
            {
                Log(LogLevel.Error, nameof(ServerStart), "Server already started.");
                return;
            }

            _isRunning = true;
            if (_socketTimeout > TimeSpan.Zero)
            {
                _timeoutThread.Start();
            }

            _acceptThread.Start();
        }

        Log(LogLevel.Info, nameof(ServerStart), "Server start initiated.");
    }

    public void ServerStop()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                Log(LogLevel.Error, nameof(ServerStop), "Server is disposed.");
                return;
            }

            if (_isStopped)
            {
                Log(LogLevel.Error, nameof(ServerStop), "Server already stopped.");
                return;
            }

            _isRunning = false;
            _isStopped = true;

            Log(LogLevel.Info, nameof(ServerStop), "Stopping server...");
            Shutdown();
        }

        Log(LogLevel.Info, nameof(ServerStop), "Server stopped.");
    }

    private void Run()
    {
        lock (_lifecycleLock)
        {
            if (!_isRunning)
            {
                return;
            }

            if (!TrySocketListen())
            {
                Log(LogLevel.Error, nameof(Run), "Stopping server due to startup failure.");
                Shutdown();
                return;
            }
        }

        StartAccept();
    }

    private bool TrySocketListen()
    {
        IPEndPoint localEndPoint = new IPEndPoint(IpAddress, Port);
        CancellationToken cancellationToken = _cancellation.Token;

        for (int attempt = 0; attempt <= _settings.ListenSocketRetries; attempt++)
        {
            if (!_isRunning || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Socket listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _settings.ListenSocketSettings.ConfigureSocket(listenSocket);

            try
            {
                listenSocket.Bind(localEndPoint);
                listenSocket.Listen(_settings.ListenSocketSettings.Backlog);

                lock (_lifecycleLock)
                {
                    _listenSocket = listenSocket;
                }

                Log(LogLevel.Info, nameof(TrySocketListen), $"Listening on {IpAddress}:{Port}.");
                return true;
            }
            catch (Exception exception)
            {
                Logger.Exception(exception);
                Service.CloseSocket(listenSocket);

                if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                {
                    Log(LogLevel.Error, nameof(TrySocketListen), $"Address is already in use ({IpAddress}:{Port}).");
                }

                if (attempt >= _settings.ListenSocketRetries)
                {
                    break;
                }

                Log(LogLevel.Error, nameof(TrySocketListen), "Listener startup failed, retrying in 1 second...");
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    break;
                }
            }
        }

        return false;
    }

    private void StartAccept()
    {
        CancellationToken cancellationToken = _cancellation.Token;

        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            if (!_acceptPool.TryAcquire(cancellationToken, out SocketAsyncEventArgs? acceptEventArgs))
            {
                return;
            }

            SocketAsyncEventArgs acquiredEventArgs = acceptEventArgs!;

            Socket? listenSocket = _listenSocket;
            if (listenSocket is null)
            {
                _acceptPool.Return(acquiredEventArgs);
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = listenSocket.AcceptAsync(acquiredEventArgs);
            }
            catch (ObjectDisposedException)
            {
                _acceptPool.Return(acquiredEventArgs);
                return;
            }
            catch (SocketException exception)
            {
                Logger.Exception(exception);
                _acceptPool.Return(acquiredEventArgs);
                continue;
            }

            if (!willRaiseEvent)
            {
                ProcessAccept(acquiredEventArgs);
            }
        }
    }

    private void AcceptCompleted(object? sender, SocketAsyncEventArgs acceptEventArgs)
    {
        ProcessAccept(acceptEventArgs);
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
    {
        Socket? acceptedSocket = acceptEventArgs.AcceptSocket;
        SocketError socketError = acceptEventArgs.SocketError;
        _acceptPool.Return(acceptEventArgs);

        if (acceptedSocket is null)
        {
            Log(
                LogLevel.Error,
                nameof(ProcessAccept),
                $"Accept completed without a socket. SocketError:{socketError}."
            );
            return;
        }

        string clientIdentity = acceptedSocket.RemoteEndPoint is IPEndPoint endPoint
            ? $"[{endPoint.Address}:{endPoint.Port}]"
            : UnknownIdentity;

        if (socketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), $"SocketError: {socketError}.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (!_isRunning)
        {
            Log(
                LogLevel.Debug,
                nameof(ProcessAccept),
                "Server is not running, rejecting accepted socket.",
                clientIdentity
            );
            Service.CloseSocket(acceptedSocket);
            return;
        }

        _settings.ClientSocketSettings.ConfigureSocket(acceptedSocket);

        if (!_clientRegistry.TryActivateClient(this, acceptedSocket, out ClientHandle clientHandle))
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "No available client slot in the pool.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        try
        {
            _consumer.OnClientConnected(clientHandle);
        }
        catch (Exception exception)
        {
            OnConsumerError(
                clientHandle,
                exception,
                nameof(ProcessAccept),
                "Error during consumer code."
            );
        }

        StartReceive(clientHandle);
    }

    private void StartReceive(ClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(StartReceive), "Client handle is stale.");
            return;
        }

        while (true)
        {
            if (!client.TryBeginSocketOperation(out Socket socket))
            {
                Disconnect(clientHandle);
                return;
            }

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.ReceiveAsync(client.ReceiveEventArgs);
            }
            catch (ObjectDisposedException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(clientHandle);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueReceiving = ProcessReceive(clientHandle);
            if (!continueReceiving)
            {
                Disconnect(clientHandle);
                return;
            }
        }
    }

    private void ReceiveCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not ClientHandle clientHandle)
        {
            Log(LogLevel.Error, nameof(ReceiveCompleted), "Unexpected user token.");
            return;
        }

        bool continueReceiving = ProcessReceive(clientHandle);
        if (continueReceiving)
        {
            StartReceive(clientHandle);
            return;
        }

        Disconnect(clientHandle);
    }

    private bool ProcessReceive(ClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Client handle is stale.");
            return false;
        }

        client.DecrementPendingOperations();

        SocketAsyncEventArgs receiveEventArgs = client.ReceiveEventArgs;

        SocketError socketError = receiveEventArgs.SocketError;
        if (socketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), $"Socket error {socketError}.", client.Identity);
            return false;
        }

        int bytesTransferred = receiveEventArgs.BytesTransferred;
        if (bytesTransferred <= 0)
        {
            Log(
                LogLevel.Error,
                nameof(ProcessReceive),
                "No bytes transferred, remote most likely closed",
                client.Identity
            );
            return false;
        }

        byte[]? receiveBuffer = receiveEventArgs.Buffer;
        int receiveOffset = receiveEventArgs.Offset;
        if (receiveBuffer is null)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Receive buffer is null.", client.Identity);
            return false;
        }

        client.RecordReceive(bytesTransferred);

        byte[] data = new byte[bytesTransferred];
        Buffer.BlockCopy(receiveBuffer, receiveOffset, data, 0, bytesTransferred);
        try
        {
            _consumer.OnReceivedData(clientHandle, data);
        }
        catch (Exception exception)
        {
            OnConsumerError(
                clientHandle,
                exception,
                nameof(ProcessReceive),
                "Error during consumer code."
            );
        }

        return client.IsAlive;
    }

    public void Send(ClientHandle clientHandle, byte[] data)
    {
        if (_isDisposed || _isStopped || !_isRunning)
        {
            Log(LogLevel.Error, nameof(Send), "Server is not running.");
            return;
        }

        if (data.Length == 0)
        {
            Log(LogLevel.Error, nameof(Send), "Empty payload, not sending.");
            return;
        }

        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(Send), "Client handle is stale.");
            return;
        }

        if (!client.IsAlive)
        {
            Log(LogLevel.Error, nameof(Send), "Client is not alive.", client.Identity);
            return;
        }

        if (!client.QueueSend(data, out bool startSend, out bool queueOverflow))
        {
            if (queueOverflow)
            {
                Log(LogLevel.Error, nameof(Send), "Send queue overflow, closing client.", client.Identity);
                Disconnect(clientHandle);
            }

            return;
        }

        if (startSend)
        {
            StartSend(clientHandle);
        }
    }

    private void StartSend(ClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(StartSend), "Client handle is stale.");
            return;
        }

        while (true)
        {
            if (!client.IsAlive)
            {
                Disconnect(clientHandle);
                return;
            }

            if (!client.TryPrepareSendChunk(_bufferSize, out int chunkSize))
            {
                return;
            }

            if (!client.TryBeginSocketOperation(out Socket socket))
            {
                Disconnect(clientHandle);
                return;
            }

            SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.SendAsync(sendEventArgs);
            }
            catch (ObjectDisposedException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(clientHandle);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueSending = ProcessSend(clientHandle);
            if (!continueSending)
            {
                return;
            }
        }
    }

    private void SendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not ClientHandle clientHandle)
        {
            Log(LogLevel.Error, nameof(SendCompleted), "Unexpected user token.");
            return;
        }

        bool continueSending = ProcessSend(clientHandle);
        if (continueSending)
        {
            StartSend(clientHandle);
        }
    }

    private bool ProcessSend(ClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Client handle is stale.");
            return false;
        }

        client.DecrementPendingOperations();

        if (!client.IsAlive)
        {
            Disconnect(clientHandle);
            return false;
        }

        SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
        if (sendEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessSend), $"Socket error {sendEventArgs.SocketError}.", client.Identity);
            Disconnect(clientHandle);
            return false;
        }

        if (sendEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Send completed with zero bytes transferred.", client.Identity);
            Disconnect(clientHandle);
            return false;
        }

        client.RecordSend(sendEventArgs.BytesTransferred);
        return client.CompleteSend(sendEventArgs.BytesTransferred);
    }

    private Client CreateClient(int clientId)
    {
        return new Client(
            clientId,
            _bufferSlab.CreateReceiveEventArgs(clientId, ReceiveCompleted),
            _bufferSlab.CreateSendEventArgs(clientId, SendCompleted),
            _settings.MaxQueuedSendBytes
        );
    }

    internal void Disconnect(ClientHandle clientHandle, string reason = "")
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            return;
        }

        client.Close();
        if (!_clientRegistry.TryDeactivateClient(clientHandle, out ClientSnapshot snapshot))
        {
            return;
        }

        TimeSpan duration = snapshot.ConnectedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : DateTime.UtcNow - snapshot.ConnectedAt;

        Log(
            LogLevel.Info,
            nameof(Disconnect),
            $"Disconnected({reason}){Environment.NewLine}" +
            $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
            $"Total Bytes Received:{snapshot.BytesReceived} ({Service.GetHumanReadableSize(snapshot.BytesReceived)}){Environment.NewLine}" +
            $"Total Bytes Sent:{snapshot.BytesSent} ({Service.GetHumanReadableSize(snapshot.BytesSent)}){Environment.NewLine}" +
            $"Current Connections:{_clientRegistry.GetAliveClientCount()}",
            snapshot.Identity
        );

        try
        {
            _consumer.OnClientDisconnected(clientHandle, snapshot);
        }
        catch (Exception exception)
        {
            OnConsumerError(
                clientHandle,
                exception,
                nameof(Disconnect),
                "Error during consumer code."
            );
        }
    }

    private void CheckSocketTimeout()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);

        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            _clientRegistry.SnapshotActiveHandles(handles);

            foreach (ClientHandle clientHandle in handles)
            {
                if (!clientHandle.TryGetClient(out Client client))
                {
                    continue;
                }

                long lastActivityMs = client.LastWriteMs > client.LastReadMs
                    ? client.LastWriteMs
                    : client.LastReadMs;

                long elapsedMsSinceLastActivity = now - lastActivityMs;
                if (elapsedMsSinceLastActivity < 0)
                {
                    elapsedMsSinceLastActivity = 0;
                }

                if (elapsedMsSinceLastActivity > _socketTimeout.TotalMilliseconds)
                {
                    TimeSpan elapsed = TimeSpan.FromMilliseconds(elapsedMsSinceLastActivity);
                    DateTime lastActiveUtc = DateTime.UtcNow - elapsed;
                    Log(
                        LogLevel.Error,
                        nameof(CheckSocketTimeout),
                        $"Client socket timed out after {elapsed.TotalSeconds} seconds. SocketTimeout:{_socketTimeout.TotalSeconds} LastActive(UTC):{lastActiveUtc:yyyy-MM-dd HH:mm:ss}",
                        client.Identity
                    );
                    Disconnect(clientHandle);
                }
            }

            int timeoutMs = Math.Clamp(
                (int)_socketTimeout.TotalMilliseconds,
                MinSocketTimeoutDelayMs,
                MaxSocketTimeoutDelayMs
            );
            cancellationToken.WaitHandle.WaitOne(timeoutMs);
        }
    }

    private void Shutdown()
    {
        lock (_lifecycleLock)
        {
            _isRunning = false;
            _isStopped = true;
        }

        Service.CloseSocket(_listenSocket);
        _listenSocket = null;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);
        _clientRegistry.SnapshotActiveHandles(handles);
        foreach (ClientHandle clientHandle in handles)
        {
            Disconnect(clientHandle);
        }

        Service.JoinThread(_acceptThread, ThreadTimeoutMs);
        Service.JoinThread(_timeoutThread, ThreadTimeoutMs);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Shutdown();
        }

        _acceptPool.Dispose();
        _clientRegistry.Dispose();
        _cancellation.Dispose();
        Log(LogLevel.Info, nameof(Dispose), "Server resources disposed.");
    }

    private void OnConsumerError(
        ClientHandle clientHandle,
        Exception exception,
        string function,
        string message
    )
    {
        string clientIdentity = UnknownIdentity;
        if (clientHandle.TryGetClient(out Client client))
        {
            clientIdentity = client.Identity;
        }

        Log(LogLevel.Error, function, message, clientIdentity);
        Logger.Exception(exception);
        try
        {
            _consumer.OnError(clientHandle, exception, message);
        }
        catch (Exception e)
        {
            Log(LogLevel.Error, nameof(OnConsumerError), "Error during consumer 'OnError' handler.", clientIdentity);
            Logger.Exception(e);
        }
    }

    private void Log(LogLevel level, string function, string message, string clientIdentity = "")
    {
        string prefix = _identity.Length > 0 || clientIdentity.Length > 0
            ? $"{_identity}{clientIdentity} "
            : string.Empty;
        Logger.Write(level, $"{prefix}{function} - {message}", null);
    }
}