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
using Arrowgene.Networking.SAEAServer.Consumer;
using Arrowgene.Networking.SAEAServer.Metric;

namespace Arrowgene.Networking.SAEAServer;

/// <summary>
/// A pooled TCP server built on <see cref="SocketAsyncEventArgs"/>.
/// </summary>
public sealed class TcpServer : IDisposable
{
    private enum ServerState : int
    {
        Created = 0,
        Running = 1,
        Stopping = 2,
        Stopped = 3,
        Disposed = 4
    }

    private const string UnknownIdentity = "[Unknown Client]";
    private const string AcceptThreadName = "TcpServer";
    private const string TimeoutThreadName = "TcpServer_Timeout";
    private const string DisconnectCleanupThreadName = "TcpServer_DisconnectCleanup";
    private const string MetricsThreadName = "TcpServer_Metrics";
    private const int ThreadTimeoutMs = 10000;
    private const int MinSocketTimeoutDelayMs = 1000;
    private const int MaxSocketTimeoutDelayMs = 30000;
    private const int DisconnectCleanupDelayMs = 500;

    private static readonly ILogger Logger = LogProvider.Logger(typeof(TcpServer));

    private readonly object _lifecycleLock;
    private readonly IConsumer _consumer;
    private readonly TcpServerSettings _settings;
    private readonly AcceptPool _acceptPool;
    private readonly BufferSlab _bufferSlab;
    private readonly ClientRegistry _clientRegistry;
    private readonly IConsumerMetrics? _consumerMetrics;
    private readonly TcpServerMetricsState _metricsState;
    private readonly TcpServerMetricsCollector _metricsCollector;
    private readonly TimeSpan _socketTimeout;
    private readonly int _bufferSize;
    private readonly string _identity;
    private readonly object _disconnectCleanupSync;
    private readonly Queue<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)>
        _disconnectCleanupQueue;
    private readonly ManualResetEventSlim _shutdownCompleted;
    private readonly ManualResetEventSlim _asyncCallbacksDrained;
    private CancellationTokenSource _cancellation;
    private Thread _acceptThread;
    private Thread _timeoutThread;
    private Thread _disconnectCleanupThread;
    private Socket? _listenSocket;
    private ServerState _state;
    private int _shutdownStarted;
    private int _inFlightAsyncCallbacks;

    /// <summary>
    /// Gets the local IP address the server binds to.
    /// </summary>
    public IPAddress IpAddress { get; }

    /// <summary>
    /// Gets the local TCP port the server binds to.
    /// </summary>
    public ushort Port { get; }

    /// <summary>
    /// Creates a server with explicit settings.
    /// </summary>
    /// <param name="ipAddress">The IP address to bind.</param>
    /// <param name="port">The TCP port to bind.</param>
    /// <param name="consumer">The consumer that receives callbacks.</param>
    /// <param name="settings">The server settings.</param>
    public TcpServer(IPAddress ipAddress, ushort port, IConsumer consumer, TcpServerSettings settings)
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

        if (consumer is ISupportsOrderingLaneCount laneAwareConsumer
            && laneAwareConsumer.OrderingLaneCount < settings.OrderingLaneCount)
        {
            throw new ArgumentException(
                $"The consumer's OrderingLaneCount ({laneAwareConsumer.OrderingLaneCount}) " +
                $"must be >= the server's OrderingLaneCount ({settings.OrderingLaneCount}).",
                paramName: nameof(consumer)
            );
        }

        IpAddress = ipAddress;
        Port = port;
        _consumer = consumer;
        _consumerMetrics = consumer as IConsumerMetrics;
        _settings = new TcpServerSettings(settings);
        _bufferSize = settings.BufferSize;

        _lifecycleLock = new object();
        _socketTimeout = TimeSpan.FromSeconds(_settings.ClientSocketTimeoutSeconds);
        _identity = string.IsNullOrEmpty(_settings.Identity) ? string.Empty : $"[{_settings.Identity}] ";
        _disconnectCleanupSync = new object();
        _disconnectCleanupQueue = new Queue<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)>(
            _settings.MaxConnections
        );
        _bufferSlab = new BufferSlab(_settings.MaxConnections, _bufferSize);
        _clientRegistry = new ClientRegistry(
            _settings.MaxConnections,
            _settings.OrderingLaneCount,
            CreateClient
        );
        _metricsState = new TcpServerMetricsState();
        _acceptPool = new AcceptPool(_settings.ConcurrentAccepts, AcceptCompleted);
        _metricsCollector = new TcpServerMetricsCollector(
            _metricsState,
            _clientRegistry,
            _acceptPool,
            _consumerMetrics,
            _settings.OrderingLaneCount
        );

        _state = ServerState.Created;
        _shutdownCompleted = new ManualResetEventSlim(false);
        _asyncCallbacksDrained = new ManualResetEventSlim(true);
        _cancellation = new CancellationTokenSource();
        _acceptThread = CreateBackgroundThread(Run, AcceptThreadName);
        _timeoutThread = CreateBackgroundThread(CheckSocketTimeout, TimeoutThreadName);
        _disconnectCleanupThread = CreateBackgroundThread(CleanupDisconnectedClients, DisconnectCleanupThreadName);
    }

    /// <summary>
    /// Gets the latest published metrics snapshot for the server.
    /// </summary>
    /// <returns>The latest published <see cref="TcpServerMetricsSnapshot"/>.</returns>
    public TcpServerMetricsSnapshot GetMetricsSnapshot()
    {
        if (IsMetricsCaptureEnabled())
        {
            try
            {
                _metricsCollector.CaptureSnapshot();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return _metricsCollector.GetSnapshot();
    }

    /// <summary>
    /// Starts the server. The server can be started again after it has been stopped.
    /// </summary>
    public void Start()
    {
        Log(LogLevel.Info, nameof(Start), "Starting server...");

        bool startTimeoutThread;
        lock (_lifecycleLock)
        {
            ServerState state = _state;
            if (state == ServerState.Disposed)
            {
                Log(LogLevel.Error, nameof(Start), "TcpServer is disposed.");
                return;
            }

            if (state == ServerState.Stopping)
            {
                Log(LogLevel.Error, nameof(Start), "TcpServer is stopping.");
                return;
            }

            if (state == ServerState.Running)
            {
                Log(LogLevel.Error, nameof(Start), "TcpServer already started.");
                return;
            }

            if (state == ServerState.Stopped)
            {
                RecreateRunResources();
            }

            _state = ServerState.Running;
            EnableMetricsCapture();
            startTimeoutThread = _socketTimeout > TimeSpan.Zero;
        }

        try
        {
            string metricsThreadName = $"{_identity}{MetricsThreadName}".Trim();
            _metricsCollector.Start(metricsThreadName);
            _disconnectCleanupThread.Start();
            if (startTimeoutThread)
            {
                _timeoutThread.Start();
            }

            _acceptThread.Start();
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);

            lock (_lifecycleLock)
            {
                if (_state == ServerState.Running)
                {
                    _state = ServerState.Stopping;
                    DisableMetricsCapture();
                }
            }

            Log(LogLevel.Error, nameof(Start), "TcpServer startup failed, shutting down.");
            Shutdown();
            return;
        }

        Log(LogLevel.Info, nameof(Start), "TcpServer start initiated.");
    }

    /// <summary>
    /// Stops the server and waits for in-flight socket completions to drain.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            ServerState state = _state;
            if (state == ServerState.Disposed)
            {
                Log(LogLevel.Error, nameof(Stop), "TcpServer is disposed.");
                return;
            }

            if (state == ServerState.Stopped)
            {
                Log(LogLevel.Error, nameof(Stop), "TcpServer already stopped.");
                return;
            }

            if (state == ServerState.Stopping)
            {
                Log(LogLevel.Error, nameof(Stop), "TcpServer stop is already in progress.");
                return;
            }

            _state = ServerState.Stopping;
            DisableMetricsCapture();
        }

        Log(LogLevel.Info, nameof(Stop), "Stopping server...");
        _metricsCollector.Stop();
        Shutdown();
        Log(LogLevel.Info, nameof(Stop), "TcpServer stopped.");
    }

    private void Run()
    {
        if (!IsRunningState())
        {
            return;
        }

        if (!TrySocketListen())
        {
            bool shouldShutdown = false;
            lock (_lifecycleLock)
            {
                if (_state == ServerState.Running)
                {
                    _state = ServerState.Stopping;
                    DisableMetricsCapture();
                    shouldShutdown = true;
                }
            }

            if (shouldShutdown)
            {
                Log(LogLevel.Error, nameof(Run), "Stopping server due to startup failure.");
                Shutdown();
            }

            return;
        }

        StartAccept();
    }

    private bool TrySocketListen()
    {
        IPEndPoint localEndPoint = new IPEndPoint(IpAddress, Port);
        CancellationToken cancellationToken = _cancellation.Token;

        for (int attempt = 0; attempt <= _settings.ListenSocketRetries; attempt++)
        {
            if (!IsRunningState() || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Socket listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _settings.ListenSocketSettings.ConfigureSocket(listenSocket);

            try
            {
                listenSocket.Bind(localEndPoint);
                listenSocket.Listen(_settings.ListenSocketSettings.Backlog);

                bool listenerPublished = false;
                lock (_lifecycleLock)
                {
                    if (_state == ServerState.Running && !cancellationToken.IsCancellationRequested)
                    {
                        _listenSocket = listenSocket;
                        listenerPublished = true;
                    }
                }

                if (!listenerPublished)
                {
                    Service.CloseSocket(listenSocket);
                    return false;
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

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_acceptPool.TryAcquire(cancellationToken, out SocketAsyncEventArgs? acceptEventArgs))
            {
                return;
            }

            SocketAsyncEventArgs acquiredEventArgs = acceptEventArgs!;

            Socket? listenSocket;
            lock (_lifecycleLock)
            {
                listenSocket = _listenSocket;
            }

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
                _metricsState.IncrementSocketAcceptErrors();
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
        EnterAsyncCallback();
        try
        {
            ProcessAccept(acceptEventArgs);
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);
            _metricsState.IncrementSocketAcceptErrors();
            Service.CloseSocket(acceptEventArgs.AcceptSocket);
        }
        finally
        {
            ExitAsyncCallback();
        }
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
    {
        Socket? acceptedSocket = acceptEventArgs.AcceptSocket;
        SocketError socketError = acceptEventArgs.SocketError;
        _acceptPool.Return(acceptEventArgs);
        ClientHandle clientHandle = default;
        bool clientActivated = false;

        try
        {
            if (acceptedSocket is null)
            {
                if (socketError != SocketError.Success)
                {
                    _metricsState.RecordSocketAcceptError(socketError);
                }
                else
                {
                    _metricsState.IncrementSocketAcceptErrors();
                }

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
                _metricsState.RecordSocketAcceptError(socketError);
                Log(LogLevel.Error, nameof(ProcessAccept), $"SocketError: {socketError}.", clientIdentity);
                Service.CloseSocket(acceptedSocket);
                return;
            }

            if (!IsRunningState())
            {
                _metricsState.IncrementRejectedConnections();
                Log(
                    LogLevel.Debug,
                    nameof(ProcessAccept),
                    "TcpServer is not running, rejecting accepted socket.",
                    clientIdentity
                );
                Service.CloseSocket(acceptedSocket);
                return;
            }

            _settings.ClientSocketSettings.ConfigureSocket(acceptedSocket);

            if (!_clientRegistry.TryActivateClient(this, acceptedSocket, out clientHandle))
            {
                _metricsState.IncrementRejectedConnections();
                Log(LogLevel.Error, nameof(ProcessAccept), "No available client slot in the pool.", clientIdentity);
                Service.CloseSocket(acceptedSocket);
                return;
            }

            clientActivated = true;
            _metricsState.IncrementAcceptedConnections();
        }

        catch (Exception exception)
        {
            Logger.Exception(exception);
            if (clientActivated)
            {
                Disconnect(clientHandle, DisconnectReason.AcceptFailure);
            }
            else
            {
                Service.CloseSocket(acceptedSocket);
            }

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
            if (!client.TryBeginSocketOperation(clientHandle.Generation, out Socket socket))
            {
                Disconnect(clientHandle, DisconnectReason.StaleHandle);
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
                Disconnect(clientHandle, DisconnectReason.ReceiveFailure);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle, DisconnectReason.ReceiveFailure);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                _metricsState.RecordSocketReceiveError(exception.SocketErrorCode);
                Disconnect(clientHandle, DisconnectReason.ReceiveFailure);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueReceiving = ProcessReceive(clientHandle, out DisconnectReason disconnectReason);
            if (!continueReceiving)
            {
                Disconnect(clientHandle, disconnectReason);
                return;
            }
        }
    }

    private void ReceiveCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        EnterAsyncCallback();
        try
        {
            if (eventArgs.UserToken is not ClientHandle clientHandle)
            {
                Log(LogLevel.Error, nameof(ReceiveCompleted), "Unexpected user token.");
                return;
            }

            bool continueReceiving = ProcessReceive(clientHandle, out DisconnectReason disconnectReason);
            if (continueReceiving)
            {
                StartReceive(clientHandle);
                return;
            }

            Disconnect(clientHandle, disconnectReason);
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);
            if (eventArgs.UserToken is ClientHandle clientHandle)
            {
                Disconnect(clientHandle, DisconnectReason.ReceiveCompletedFailure);
            }
        }
        finally
        {
            ExitAsyncCallback();
        }
    }

    private bool ProcessReceive(ClientHandle clientHandle, out DisconnectReason disconnectReason)
    {
        disconnectReason = DisconnectReason.None;

        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(ProcessReceive), "Client handle is stale.");
            disconnectReason = DisconnectReason.StaleHandle;
            return false;
        }

        try
        {
            SocketAsyncEventArgs receiveEventArgs = client.ReceiveEventArgs;

            SocketError socketError = receiveEventArgs.SocketError;
            if (socketError != SocketError.Success)
            {
                _metricsState.RecordSocketReceiveError(socketError);
                Log(LogLevel.Error, nameof(ProcessReceive), $"Socket error {socketError}.", client.Identity);
                disconnectReason = DisconnectReason.ReceiveFailure;
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
                disconnectReason = DisconnectReason.RemoteClosed;
                return false;
            }

            byte[]? receiveBuffer = receiveEventArgs.Buffer;
            int receiveOffset = receiveEventArgs.Offset;
            if (receiveBuffer is null)
            {
                Log(LogLevel.Error, nameof(ProcessReceive), "Receive buffer is null.", client.Identity);
                disconnectReason = DisconnectReason.ReceiveFailure;
                return false;
            }

            client.RecordReceive(bytesTransferred);
            _metricsState.RecordReceive(bytesTransferred);

            byte[] data = GC.AllocateUninitializedArray<byte>(bytesTransferred);
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
        finally
        {
            client.DecrementPendingOperations();
        }
    }

    /// <summary>
    /// Queues a payload to be sent to the specified client.
    /// </summary>
    /// <param name="clientHandle">The target client.</param>
    /// <param name="data">The payload to send.</param>
    public void Send(ClientHandle clientHandle, byte[] data)
    {
        if (!IsRunningState())
        {
            Log(LogLevel.Error, nameof(Send), "TcpServer is not running.");
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

        if (!client.QueueSend(clientHandle.Generation, data, out bool startSend, out bool queueOverflow))
        {
            if (queueOverflow)
            {
                _metricsState.IncrementSendQueueOverflows();
                Log(LogLevel.Error, nameof(Send), "Send queue overflow, closing client.", client.Identity);
                Disconnect(clientHandle, DisconnectReason.SendQueueOverflow);
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
            if (!client.TryPrepareSendChunk(clientHandle.Generation, _bufferSize, out int chunkSize))
            {
                return;
            }

            if (!client.TryBeginSocketOperation(clientHandle.Generation, out Socket socket))
            {
                Disconnect(clientHandle, DisconnectReason.StaleHandle);
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
                Disconnect(clientHandle, DisconnectReason.SendFailure);
                return;
            }
            catch (InvalidOperationException)
            {
                client.DecrementPendingOperations();
                Disconnect(clientHandle, DisconnectReason.SendFailure);
                return;
            }
            catch (SocketException exception)
            {
                client.DecrementPendingOperations();
                Logger.Exception(exception);
                _metricsState.RecordSocketSendError(exception.SocketErrorCode);
                Disconnect(clientHandle, DisconnectReason.SendFailure);
                return;
            }

            if (willRaiseEvent)
            {
                return;
            }

            bool continueSending = ProcessSend(
                clientHandle,
                out bool disconnectClient,
                out DisconnectReason disconnectReason
            );
            if (!continueSending)
            {
                if (disconnectClient)
                {
                    Disconnect(clientHandle, disconnectReason);
                }

                return;
            }
        }
    }

    private void SendCompleted(object? sender, SocketAsyncEventArgs eventArgs)
    {
        EnterAsyncCallback();
        try
        {
            if (eventArgs.UserToken is not ClientHandle clientHandle)
            {
                Log(LogLevel.Error, nameof(SendCompleted), "Unexpected user token.");
                return;
            }

            bool continueSending = ProcessSend(
                clientHandle,
                out bool disconnectClient,
                out DisconnectReason disconnectReason
            );
            if (continueSending)
            {
                StartSend(clientHandle);
                return;
            }

            if (disconnectClient)
            {
                Disconnect(clientHandle, disconnectReason);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception(exception);
            if (eventArgs.UserToken is ClientHandle clientHandle)
            {
                Disconnect(clientHandle, DisconnectReason.SendCompletedFailure);
            }
        }
        finally
        {
            ExitAsyncCallback();
        }
    }

    private bool ProcessSend(
        ClientHandle clientHandle,
        out bool disconnectClient,
        out DisconnectReason disconnectReason)
    {
        disconnectClient = false;
        disconnectReason = DisconnectReason.None;

        if (!clientHandle.TryGetClient(out Client client))
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Client handle is stale.");
            disconnectClient = true;
            disconnectReason = DisconnectReason.StaleHandle;
            return false;
        }

        if (!client.IsAlive)
        {
            client.DecrementPendingOperations();
            disconnectClient = true;
            disconnectReason = DisconnectReason.SendFailure;
            return false;
        }

        SocketAsyncEventArgs sendEventArgs = client.SendEventArgs;
        if (sendEventArgs.SocketError != SocketError.Success)
        {
            _metricsState.RecordSocketSendError(sendEventArgs.SocketError);
            Log(LogLevel.Error, nameof(ProcessSend), $"Socket error {sendEventArgs.SocketError}.", client.Identity);
            client.DecrementPendingOperations();
            disconnectClient = true;
            disconnectReason = DisconnectReason.SendFailure;
            return false;
        }

        if (sendEventArgs.BytesTransferred <= 0)
        {
            Log(LogLevel.Error, nameof(ProcessSend), "Send completed with zero bytes transferred.",
                client.Identity);
            client.DecrementPendingOperations();
            disconnectClient = true;
            disconnectReason = DisconnectReason.SendFailure;
            return false;
        }

        try
        {
            client.RecordSend(sendEventArgs.BytesTransferred);
            _metricsState.RecordSend(sendEventArgs.BytesTransferred);
            return client.CompleteSend(sendEventArgs.BytesTransferred);
        }
        finally
        {
            client.DecrementPendingOperations();
        }
    }

    private Client CreateClient(ushort clientId)
    {
        return new Client(
            this,
            clientId,
            _bufferSlab.CreateReceiveEventArgs(clientId, ReceiveCompleted),
            _bufferSlab.CreateSendEventArgs(clientId, SendCompleted),
            _settings.MaxQueuedSendBytes
        );
    }

    internal void Disconnect(
        ClientHandle clientHandle,
        DisconnectReason disconnectReason = DisconnectReason.None)
    {
        if (!clientHandle.TryGetClient(out Client client))
        {
            return;
        }

        client.Close();
        if (client.IsDisconnectCleanupQueued)
        {
            return;
        }

        if (!TryFinalizeDisconnect(clientHandle, disconnectReason) && client.TryMarkDisconnectCleanupQueued())
        {
            lock (_disconnectCleanupSync)
            {
                _disconnectCleanupQueue.Enqueue((clientHandle, disconnectReason));
                _metricsState.EnqueueDisconnectCleanup();
            }
        }
    }

    private bool TryFinalizeDisconnect(
        ClientHandle clientHandle,
        DisconnectReason disconnectReason)
    {
        if (!_clientRegistry.TryDeactivateClient(clientHandle, out ClientSnapshot snapshot))
        {
            return false;
        }

        _metricsState.FinalizeDisconnect(disconnectReason);

        TimeSpan duration = snapshot.ConnectedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : DateTime.UtcNow - snapshot.ConnectedAt;
        string disconnectReasonText = disconnectReason == DisconnectReason.None
            ? string.Empty
            : disconnectReason.ToString();

        Log(
            LogLevel.Info,
            nameof(Disconnect),
            $"Disconnected({disconnectReasonText}){Environment.NewLine}" +
            $"Total Seconds:{duration.TotalSeconds} ({Service.GetHumanReadableDuration(duration)}){Environment.NewLine}" +
            $"Total Bytes Received:{snapshot.BytesReceived} ({Service.GetHumanReadableSize(snapshot.BytesReceived)}){Environment.NewLine}" +
            $"Total Bytes Sent:{snapshot.BytesSent} ({Service.GetHumanReadableSize(snapshot.BytesSent)}){Environment.NewLine}" +
            $"Current Connections:{_clientRegistry.GetAliveClientCount()}",
            snapshot.Identity
        );

        try
        {
            _consumer.OnClientDisconnected(snapshot);
        }
        catch (Exception exception)
        {
            OnConsumerError(
                snapshot,
                exception,
                nameof(Disconnect),
                "Error during consumer code."
            );
        }

        return true;
    }

    private void CleanupDisconnectedClients()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        List<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)> disconnects =
            new List<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)>(
                _settings.MaxConnections
            );

        while (true)
        {
            ProcessDisconnectCleanupQueue(disconnects);

            if (cancellationToken.IsCancellationRequested && IsDisconnectCleanupQueueEmpty())
            {
                return;
            }

            cancellationToken.WaitHandle.WaitOne(DisconnectCleanupDelayMs);
        }
    }

    private void CheckSocketTimeout()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);

        while (!cancellationToken.IsCancellationRequested)
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
                    _metricsState.IncrementTimedOutConnections();
                    Log(
                        LogLevel.Error,
                        nameof(CheckSocketTimeout),
                        $"Client socket timed out after {elapsed.TotalSeconds} seconds. SocketTimeout:{_socketTimeout.TotalSeconds} LastActive(UTC):{lastActiveUtc:yyyy-MM-dd HH:mm:ss}",
                        client.Identity
                    );
                    Disconnect(clientHandle, DisconnectReason.Timeout);
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
        if (Interlocked.CompareExchange(ref _shutdownStarted, 1, 0) != 0)
        {
            _shutdownCompleted.Wait();
            return;
        }

        try
        {
            Socket? listenSocket;
            lock (_lifecycleLock)
            {
                if (_state == ServerState.Running || _state == ServerState.Created)
                {
                    _state = ServerState.Stopping;
                    DisableMetricsCapture();
                }

                listenSocket = _listenSocket;
                _listenSocket = null;
            }

            _metricsCollector.Stop();

            Service.CloseSocket(listenSocket);
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            DisconnectActiveClients();
            Service.JoinThread(_acceptThread, ThreadTimeoutMs);
            WaitForShutdownQuiescence();
            Service.JoinThread(_timeoutThread, ThreadTimeoutMs);
            Service.JoinThread(_disconnectCleanupThread, ThreadTimeoutMs);
            WaitForShutdownQuiescence();

            lock (_lifecycleLock)
            {
                if (_state == ServerState.Stopping)
                {
                    _state = ServerState.Stopped;
                }
            }

            try
            {
                _cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            _shutdownCompleted.Set();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_state == ServerState.Disposed)
            {
                return;
            }

            _state = ServerState.Disposed;
        }

        Shutdown();
        _metricsCollector.Dispose();
        _acceptPool.Dispose();
        _clientRegistry.Dispose();
        _shutdownCompleted.Dispose();
        _asyncCallbacksDrained.Dispose();
        Log(LogLevel.Info, nameof(Dispose), "TcpServer resources disposed.");
    }

    private bool IsRunningState()
    {
        lock (_lifecycleLock)
        {
            return _state == ServerState.Running;
        }
    }

    private void OnConsumerError(ClientHandle clientHandle, Exception exception, string function, string message)
    {
        if (!clientHandle.TrySnapshot(out ClientSnapshot snapshot))
        {
            snapshot = default;
        }

        OnConsumerError(snapshot, exception, function, message);
    }

    private void OnConsumerError(ClientSnapshot clientSnapshot, Exception exception, string function, string message)
    {
        string clientIdentity = string.IsNullOrEmpty(clientSnapshot.Identity)
            ? UnknownIdentity
            : clientSnapshot.Identity;

        Log(LogLevel.Error, function, message, clientIdentity);
        Logger.Exception(exception);
        try
        {
            _consumer.OnError(clientSnapshot, exception, message);
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

    private void EnterAsyncCallback()
    {
        Interlocked.Increment(ref _inFlightAsyncCallbacks);
        _metricsState.EnterAsyncCallback();
        _asyncCallbacksDrained.Reset();
    }

    private void ExitAsyncCallback()
    {
        _metricsState.ExitAsyncCallback();
        if (Interlocked.Decrement(ref _inFlightAsyncCallbacks) <= 0)
        {
            _asyncCallbacksDrained.Set();
        }
    }

    private void DisconnectActiveClients()
    {
        List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);
        _clientRegistry.SnapshotActiveHandles(handles);

        foreach (ClientHandle clientHandle in handles)
        {
            Disconnect(clientHandle, DisconnectReason.Shutdown);
        }
    }

    private void WaitForShutdownQuiescence()
    {
        List<ClientHandle> handles = new List<ClientHandle>(_settings.MaxConnections);
        List<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)> disconnects =
            new List<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)>(
                _settings.MaxConnections
            );

        while (true)
        {
            DisconnectActiveClients();
            ProcessDisconnectCleanupQueue(disconnects);

            if (IsShutdownQuiescent(handles))
            {
                return;
            }

            _asyncCallbacksDrained.Wait(DisconnectCleanupDelayMs);
        }
    }

    private bool IsShutdownQuiescent(List<ClientHandle> handles)
    {
        _clientRegistry.SnapshotActiveHandles(handles);
        if (handles.Count > 0)
        {
            return false;
        }

        if (Volatile.Read(ref _inFlightAsyncCallbacks) != 0)
        {
            return false;
        }

        if (_acceptPool.CurrentCount != _acceptPool.Capacity)
        {
            return false;
        }

        return IsDisconnectCleanupQueueEmpty();
    }

    private bool IsDisconnectCleanupQueueEmpty()
    {
        lock (_disconnectCleanupSync)
        {
            return _disconnectCleanupQueue.Count == 0;
        }
    }

    private void ProcessDisconnectCleanupQueue(
        List<(ClientHandle ClientHandle, DisconnectReason DisconnectReason)> disconnects)
    {
        disconnects.Clear();
        lock (_disconnectCleanupSync)
        {
            while (_disconnectCleanupQueue.Count > 0)
            {
                disconnects.Add(_disconnectCleanupQueue.Dequeue());
                _metricsState.DequeueDisconnectCleanup();
            }
        }

        foreach ((ClientHandle clientHandle, DisconnectReason disconnectReason) in disconnects)
        {
            if (!clientHandle.TryGetClient(out Client client))
            {
                continue;
            }

            if (!TryFinalizeDisconnect(clientHandle, disconnectReason))
            {
                lock (_disconnectCleanupSync)
                {
                    _disconnectCleanupQueue.Enqueue((clientHandle, disconnectReason));
                    _metricsState.EnqueueDisconnectCleanup();
                }
            }
        }
    }

    private void RecreateRunResources()
    {
        _cancellation = new CancellationTokenSource();
        _shutdownCompleted.Reset();
        _asyncCallbacksDrained.Set();
        _shutdownStarted = 0;
        _inFlightAsyncCallbacks = 0;
        _metricsState.ResetCurrentGauges();
        lock (_disconnectCleanupSync)
        {
            _disconnectCleanupQueue.Clear();
        }

        _acceptThread = CreateBackgroundThread(Run, AcceptThreadName);
        _timeoutThread = CreateBackgroundThread(CheckSocketTimeout, TimeoutThreadName);
        _disconnectCleanupThread = CreateBackgroundThread(CleanupDisconnectedClients, DisconnectCleanupThreadName);
    }

    private Thread CreateBackgroundThread(ThreadStart threadStart, string name)
    {
        return new Thread(threadStart)
        {
            Name = $"{_identity}{name}",
            IsBackground = true
        };
    }

    private void EnableMetricsCapture()
    {
        _metricsState.EnableCapture();
        _consumerMetrics?.EnableCapture();
    }

    private void DisableMetricsCapture()
    {
        _metricsState.DisableCapture();
        _consumerMetrics?.DisableCapture();
    }

    private bool IsMetricsCaptureEnabled()
    {
        return _metricsState.IsCaptureEnabled();
    }

}
