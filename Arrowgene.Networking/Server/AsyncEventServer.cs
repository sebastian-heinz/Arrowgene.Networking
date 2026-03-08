using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking.Consumer;

namespace Arrowgene.Networking.Server;

/// <summary>
/// A pooled TCP server built on <see cref="SocketAsyncEventArgs"/>.
/// </summary>
public sealed class AsyncEventServer : IDisposable
{
    private const string AcceptThreadName = "AsyncEventServer";
    private const string TimeoutThreadName = "AsyncEventServer_Timeout";
    private const int ThreadTimeoutMs = 10000;
    private const int MinSocketTimeoutDelayMs = 1000;
    private const int MaxSocketTimeoutDelayMs = 30000;

    private static readonly ILogger Logger = LogProvider.Logger(typeof(AsyncEventServer));

    private readonly object _lifecycleLock;
    private readonly IConsumer _consumer;
    private readonly AsyncEventSettings _settings;
    private readonly AsyncEventAcceptPool _acceptPool;
    private readonly AsyncEventBufferSlab _bufferSlab;
    private readonly AsyncEventClientRegistry _clientRegistry;
    private readonly TimeSpan _socketTimeout;
    private readonly string _identity;
    private readonly CancellationTokenSource _cancellation;
    private Thread? _acceptThread;
    private Thread? _timeoutThread;
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
    public AsyncEventServer(IPAddress ipAddress, ushort port, IConsumer consumer, AsyncEventSettings settings)
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
        _settings = new AsyncEventSettings(settings);

        _lifecycleLock = new object();
        _socketTimeout = TimeSpan.FromSeconds(_settings.SocketTimeoutSeconds);
        _identity = string.IsNullOrEmpty(_settings.Identity) ? string.Empty : $"[{_settings.Identity}] ";
        _cancellation = new CancellationTokenSource();
        _bufferSlab = new AsyncEventBufferSlab(_settings.MaxConnections, _settings.BufferSize);
        _clientRegistry = new AsyncEventClientRegistry(
            _settings.MaxConnections,
            _settings.OrderingLaneCount,
            CreateClient
        );
        _acceptPool = new AsyncEventAcceptPool(_settings.AcceptConcurrency, AcceptCompleted);

        _isDisposed = false;
        _isRunning = false;
        _isStopped = false;
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
            _acceptThread = new Thread(Run)
            {
                Name = $"{_identity}{AcceptThreadName}",
                IsBackground = true
            };
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
                Log(LogLevel.Error, nameof(ServerStart), "Server is disposed.");
                return;
            }

            if (_isStopped)
            {
                Log(LogLevel.Error, nameof(ServerStop), "Server already stopped.");
                return;
            }

            _isRunning = false;
            _isStopped = true;
        }

        Log(LogLevel.Info, nameof(ServerStop), "Stopping server...");
        Shutdown();
        Log(LogLevel.Info, nameof(ServerStop), "Server stopped.");
    }

    private void Shutdown()
    {
        lock (_lifecycleLock)
        {
            _isRunning = false;
            _isStopped = true;
        }

        Service.CloseSocket(_listenSocket);
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        List<AsyncEventClientHandle> handles = new List<AsyncEventClientHandle>(_settings.MaxConnections);
        _clientRegistry.SnapshotActiveHandles(handles);
        foreach (AsyncEventClientHandle handle in handles)
        {
            Disconnect(handle);
        }
    }

    public void Send(AsyncEventClientHandle socket, byte[] data)
    {
        if (_isDisposed || !_isRunning || _isDisposed)
        {
            Log(LogLevel.Error, nameof(Send), "Server is not running.");
            return;
        }

        if (data.Length == 0)
        {
            Log(LogLevel.Error, nameof(Send), "Empty payload, not sending.");
            return;
        }

        if (!socket.TryGetClient(out AsyncEventClient client))
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
                Disconnect(client);
            }

            return;
        }

        if (startSend)
        {
            StartSend(client);
        }
    }

    public void CloseClientHandle(AsyncEventClientHandle client)
    {
        Disconnect(client);
    }

    internal void Disconnect(AsyncEventClient client)
    {
        string clientIdentity = client.Identity;
        DateTime connectedAt = client.ConnectedAt;
        ulong bytesReceived = client.BytesReceived;
        ulong bytesSent = client.BytesSent;

        AsyncEventClientHandle disconnectHandle = new AsyncEventClientHandle(this, client);
        // TODO verify dc handle
        client.TryDisconnect(out bool wasAlive);
        if (!wasAlive)
        {
            TryFinalizeDispose(nameof(Disconnect));
            return;
        }

        bool removed = _clientRegistry.TryRemoveActiveClient(client, disconnectHandle, out int currentConnections);
        if (!removed)
        {
            Log(LogLevel.Error, nameof(Disconnect), "Could not remove client from the active registry.",
                clientIdentity);
        }

        TimeSpan duration = connectedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : DateTime.UtcNow - connectedAt;

        Log(
            LogLevel.Debug,
            nameof(Disconnect),
            $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
            $"Total Bytes Received:{bytesReceived} ({Service.GetHumanReadableSize(bytesReceived)}){Environment.NewLine}" +
            $"Total Bytes Sent:{bytesSent} ({Service.GetHumanReadableSize(bytesSent)}){Environment.NewLine}" +
            $"Current Connections:{currentConnections}",
            clientIdentity);

        InvokeClientDisconnected(disconnectHandle, clientIdentity);
        TryRecycleClient(client);
        TryFinalizeDispose(nameof(Disconnect));
    }

    private void Disconnect(AsyncEventClientHandle clientHandle)
    {
        if (!clientHandle.TryGetClient(out AsyncEventClient client))
        {
            Log(LogLevel.Error, nameof(Disconnect), "Client handle is stale.");
            return;
        }

        Disconnect(client);
    }

    private void Run()
    {
        try
        {
            if (!_isRunning)
            {
                return;
            }

            if (!TryStartListener())
            {
                Log(LogLevel.Error, nameof(Run), "Stopping server due to startup failure.");
                if (StopCore(nameof(Run), logAlreadyStopped: false))
                {
                    Log(LogLevel.Info, nameof(Run), "Server stopped due to listener failure.");
                }

                return;
            }

            if (_socketTimeout > TimeSpan.Zero)
            {
                Thread timeoutThread = new Thread(CheckSocketTimeout)
                {
                    Name = $"{_identity}{TimeoutThreadName}",
                    IsBackground = true
                };

                lock (_lifecycleLock)
                {
                    if (_isRunning)
                    {
                        _timeoutThread = timeoutThread;
                        timeoutThread.Start();
                    }
                }
            }

            StartAcceptLoop();
        }
        finally
        {
            _acceptThreadExited = true;
            TryFinalizeDispose(nameof(Run));
        }
    }

    private bool TryStartListener()
    {
        IPEndPoint localEndPoint = new IPEndPoint(IpAddress, Port);
        CancellationToken cancellationToken = _cancellation.Token;

        for (int attempt = 0; _isRunning && attempt <= _settings.BindRetryCount; attempt++)
        {
            if (attempt > 0 && cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Socket listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _settings.SocketSettings.ConfigureSocket(listenSocket, Logger);

            try
            {
                listenSocket.Bind(localEndPoint);
                listenSocket.Listen(_settings.SocketSettings.Backlog);

                lock (_lifecycleLock)
                {
                    if (!_isRunning)
                    {
                        Service.CloseSocket(listenSocket);
                        return false;
                    }

                    _listenSocket = listenSocket;
                }

                Log(LogLevel.Info, nameof(TryStartListener), $"Listening on {IpAddress}:{Port}.");
                return true;
            }
            catch (Exception exception)
            {
                Service.CloseSocket(listenSocket);
                Logger.Exception(exception);

                if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                {
                    Log(LogLevel.Error, nameof(TryStartListener), $"Address is already in use ({IpAddress}:{Port}).");
                }

                if (attempt == _settings.BindRetryCount)
                {
                    break;
                }

                Log(LogLevel.Error, nameof(TryStartListener), "Listener startup failed, retrying in 1 second...");
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    break;
                }
            }
        }

        return false;
    }

    private void StartAcceptLoop()
    {
        CancellationToken cancellationToken = _cancellation.Token;

        while (_isRunning)
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
                TryFinalizeDispose(nameof(StartAcceptLoop));
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
                TryFinalizeDispose(nameof(StartAcceptLoop));
                return;
            }
            catch (SocketException exception)
            {
                Logger.Exception(exception);
                _acceptPool.Return(acquiredEventArgs);
                TryFinalizeDispose(nameof(StartAcceptLoop));
                if (_isRunning)
                {
                    Log(LogLevel.Error, nameof(StartAcceptLoop), "AcceptAsync failed.");
                }

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
        TryFinalizeDispose(nameof(ProcessAccept));

        if (acceptedSocket is null)
        {
            if (socketError != SocketError.Success && socketError != SocketError.OperationAborted)
            {
                Log(LogLevel.Error, nameof(ProcessAccept),
                    $"Accept completed without a socket. SocketError:{socketError}.");
            }

            return;
        }

        string clientIdentity = acceptedSocket.RemoteEndPoint is IPEndPoint endPoint
            ? $"[{endPoint.Address}:{endPoint.Port}]"
            : "[Unknown Client]";

        if (!_isRunning)
        {
            Log(LogLevel.Debug, nameof(ProcessAccept), "Server is not running, rejecting accepted socket.",
                clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (socketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), $"Socket error: {socketError}.", clientIdentity);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        try
        {
            ConfigureAcceptedSocket(acceptedSocket, clientIdentity);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Failed to configure the accepted socket.", clientIdentity);
            Logger.Exception(exception);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        AsyncEventClientHandle clientHandle;
        try
        {
            lock (_lifecycleLock)
            {
                if (!_isRunning)
                {
                    Log(LogLevel.Debug, nameof(ProcessAccept), "Server is not running, rejecting accepted socket.",
                        clientIdentity);
                    Service.CloseSocket(acceptedSocket);
                    return;
                }

                if (!_clientRegistry.TryActivateClient(
                        this,
                        acceptedSocket,
                        out AsyncEventClient? client,
                        out clientHandle,
                        out int activeConnections))
                {
                    Log(LogLevel.Error, nameof(ProcessAccept), "No available client slot in the pool.", clientIdentity);
                    Service.CloseSocket(acceptedSocket);
                    return;
                }

                if (client is null)
                {
                    Log(LogLevel.Error, nameof(ProcessAccept), "Client activation returned no client instance.",
                        clientIdentity);
                    Service.CloseSocket(acceptedSocket);
                    return;
                }

                Log(LogLevel.Info, nameof(ProcessAccept), $"Active Client Connections:{activeConnections}.",
                    clientIdentity);
            }
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Failed to activate the accepted client slot.", clientIdentity);
            Logger.Exception(exception);
            Service.CloseSocket(acceptedSocket);
            return;
        }

        if (clientHandle.TryGetClient(out AsyncEventClient connectedClient))
        {
            InvokeClientConnected(connectedClient, clientIdentity);
        }

        if (!clientHandle.TryGetClient(out AsyncEventClient clientToReceive))
        {
            TryFinalizeDispose(nameof(ProcessAccept));
            return;
        }

        StartReceive(clientToReceive);
    }

    private void ConfigureAcceptedSocket(Socket acceptedSocket, string clientIdentity)
    {
        _settings.SocketSettings.ConfigureSocket(acceptedSocket, Logger);

        if (_socketTimeout <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            acceptedSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Debug, nameof(ConfigureAcceptedSocket),
                $"Unable to enable SO_KEEPALIVE on accepted socket: {exception.Message}", clientIdentity);
        }
    }

    private void StartReceive(AsyncEventClient client)
    {
        while (true)
        {
            if (!client.TryBeginSocketOperation(out Socket socket))
            {
                Disconnect(client);
                TryRecycleClient(client);
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
                Disconnect(client);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(client);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueReceiving = ProcessReceive(client);
            if (!continueReceiving)
            {
                TryRecycleClient(client);
                return;
            }
        }
    }

    private void ReceiveCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not AsyncEventClient client)
        {
            Log(LogLevel.Error, nameof(ReceiveCompleted), "Unexpected user token.");
            return;
        }

        bool continueReceiving = ProcessReceive(client);
        if (continueReceiving)
        {
            StartReceive(client);
            return;
        }

        TryRecycleClient(client);
    }

    private bool ProcessReceive(AsyncEventClient client)
    {
        SocketAsyncEventArgs receiveEventArgs = client.ReceiveEventArgs;
        SocketError socketError = receiveEventArgs.SocketError;
        int bytesTransferred = receiveEventArgs.BytesTransferred;
        byte[]? receiveBuffer = receiveEventArgs.Buffer;
        bool wasAlive = client.IsAlive;
        bool invokeCallback = false;
        AsyncEventClientHandle callbackHandle = default;

        if (wasAlive &&
            socketError == SocketError.Success &&
            bytesTransferred > 0 &&
            receiveBuffer is not null)
        {
            client.RecordReceive(bytesTransferred);
            if (client.IsAlive)
            {
                client.IncrementPendingOperations();
                callbackHandle = new AsyncEventClientHandle(this, client);
                // TODO verify handle
                invokeCallback = true;
            }
        }

        client.DecrementPendingOperations();

        if (!wasAlive)
        {
            TryFinalizeDispose(nameof(ProcessReceive));
            return false;
        }

        if (socketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), $"Socket error {socketError}.", client.Identity);
            Disconnect(client);
            return false;
        }

        if (bytesTransferred <= 0)
        {
            Disconnect(client);
            return false;
        }

        if (receiveBuffer is null)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Receive buffer is null.", client.Identity);
            Disconnect(client);
            return false;
        }

        if (invokeCallback)
        {
            InvokeReceivedData(client, callbackHandle, receiveBuffer, receiveEventArgs.Offset, bytesTransferred);
        }

        return client.IsAlive;
    }

    private void StartSend(AsyncEventClient client)
    {
        while (true)
        {
            if (!client.IsAlive)
            {
                TryRecycleClient(client);
                return;
            }

            if (!client.TryPrepareSendChunk(out int chunkSize))
            {
                return;
            }

            if (!client.TryBeginSocketOperation(out Socket socket))
            {
                Disconnect(client);
                TryRecycleClient(client);
                return;
            }

            SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
            sendEventArgs.SetBuffer(sendEventArgs.Offset, chunkSize);

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = socket.SendAsync(sendEventArgs);
            }
            catch (ObjectDisposedException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(client);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                Disconnect(client);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueSending = ProcessSend(client);
            if (!continueSending)
            {
                TryRecycleClient(client);
                return;
            }
        }
    }

    private void SendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        if (eventArgs.UserToken is not AsyncEventClient client)
        {
            Log(LogLevel.Error, nameof(SendCompleted), "Unexpected user token.");
            return;
        }

        bool continueSending = ProcessSend(client);
        if (continueSending)
        {
            StartSend(client);
            return;
        }

        TryRecycleClient(client);
    }

    private bool ProcessSend(AsyncEventClient client)
    {
        client.DecrementPendingOperations();

        if (!client.IsAlive)
        {
            TryFinalizeDispose(nameof(ProcessSend));
            return false;
        }

        SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
        if (sendEventArgs.SocketError != SocketError.Success)
        {
            Log(LogLevel.Error, nameof(ProcessSend), $"Socket error {sendEventArgs.SocketError}.", client.Identity);
            Disconnect(client);
            return false;
        }

        if (sendEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Send completed with zero bytes transferred.", client.Identity);
            Disconnect(client);
            return false;
        }

        client.RecordSend(sendEventArgs.BytesTransferred);
        return client.CompleteSend(sendEventArgs.BytesTransferred);
    }

    private void TryRecycleClient(AsyncEventClient client)
    {
        if (_isDisposed)
        {
            return;
        }

        _clientRegistry.TryRecycle(client);
        TryFinalizeDispose(nameof(TryRecycleClient));
    }

    private void CheckSocketTimeout()
    {
        try
        {
            CancellationToken cancellationToken = _cancellation.Token;
            List<AsyncEventClientHandle> handles = new List<AsyncEventClientHandle>(_settings.MaxConnections);

            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                _clientRegistry.SnapshotActiveHandles(handles);

                foreach (AsyncEventClientHandle handle in handles)
                {
                    if (!handle.TryGetClient(out AsyncEventClient client))
                    {
                        continue;
                    }

                    long lastActivityTicks = client.LastWriteTicks > client.LastReadTicks
                        ? client.LastWriteTicks
                        : client.LastReadTicks;

                    long elapsedTicksSinceLastActivity = now - lastActivityTicks;
                    if (elapsedTicksSinceLastActivity < 0)
                    {
                        elapsedTicksSinceLastActivity = 0;
                    }

                    if (elapsedTicksSinceLastActivity > _socketTimeout.TotalMilliseconds)
                    {
                        TimeSpan elapsed = TimeSpan.FromMilliseconds(elapsedTicksSinceLastActivity);
                        DateTime lastActiveUtc = DateTime.UtcNow - elapsed;
                        Log(
                            LogLevel.Error,
                            nameof(CheckSocketTimeout),
                            $"Client socket timed out after {elapsed.TotalSeconds} seconds. SocketTimeout:{_socketTimeout.TotalSeconds} LastActive(UTC):{lastActiveUtc:yyyy-MM-dd HH:mm:ss}",
                            client.Identity);
                        Disconnect(client);
                    }
                }

                int timeoutMs = Math.Clamp((int)_socketTimeout.TotalMilliseconds, MinSocketTimeoutDelayMs,
                    MaxSocketTimeoutDelayMs);
                cancellationToken.WaitHandle.WaitOne(timeoutMs);
            }
        }
        finally
        {
            _timeoutThreadExited = true;
            TryFinalizeDispose(nameof(CheckSocketTimeout));
        }
    }

    /// <inheritdoc />
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

    private AsyncEventClient CreateClient(int clientId)
    {
        return new AsyncEventClient(
            clientId,
            _bufferSlab.CreateReceiveEventArgs(clientId, ReceiveCompleted),
            _bufferSlab.CreateSendEventArgs(clientId, SendCompleted),
            _settings.MaxQueuedSendBytes
        );
    }
 
    private void InvokeClientConnected(AsyncEventClient client, string clientIdentity)
    {
        if (!client.IsAlive)
        {
            return;
        }

        client.IncrementPendingOperations();
        AsyncEventClientHandle clientHandle = new AsyncEventClientHandle(this, client);
        // TODO verify
        try
        {
            _consumer.OnClientConnected(clientHandle);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessAccept), "Error during OnClientConnected user code.", clientIdentity);
            Logger.Exception(exception);
        }
        finally
        {
            client.DecrementPendingOperations();
            TryRecycleClient(client);
            TryFinalizeDispose(nameof(InvokeClientConnected));
        }
    }

    private void InvokeReceivedData(
        AsyncEventClient client,
        AsyncEventClientHandle clientHandle,
        byte[] receiveBuffer,
        int offset,
        int count)
    {
        string clientIdentity = client.Identity;
        byte[] data = new byte[count];
        Buffer.BlockCopy(receiveBuffer, offset, data, 0, count);

        try
        {
            _consumer.OnReceivedData(clientHandle, data);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Error in OnReceivedData user code.", clientIdentity);
            Logger.Exception(exception);
        }
        finally
        {
            client.DecrementPendingOperations();
            TryRecycleClient(client);
            TryFinalizeDispose(nameof(InvokeReceivedData));
        }
    }

    private void InvokeClientDisconnected(AsyncEventClientHandle clientHandle, string clientIdentity)
    {
        try
        {
            _consumer.OnClientDisconnected(clientHandle);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, nameof(Disconnect), "Error during OnClientDisconnected user code.", clientIdentity);
            Logger.Exception(exception);
        }
        finally
        {
            TryFinalizeDispose(nameof(InvokeClientDisconnected));
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