using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Arrowgene.Logging;
using Arrowgene.Networking;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class SaeaAcceptLoop : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(SaeaAcceptLoop));

    private readonly object _stateLock;
    private readonly SocketAsyncEventArgs[] _acceptEventArgs;
    private readonly IPAddress _ipAddress;
    private readonly ushort _port;
    private readonly SaeaServerOptions _options;
    private readonly ISaeaAcceptHandler _handler;

    private Socket? _listenSocket;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private int _runGeneration;
    private int _pendingAccepts;

    internal SaeaAcceptLoop(IPAddress ipAddress, ushort port, SaeaServerOptions options, ISaeaAcceptHandler handler)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        _ipAddress = ipAddress;
        _port = port;
        _options = options;
        _handler = handler;
        _stateLock = new object();
        _acceptEventArgs = new SocketAsyncEventArgs[_options.ConcurrentAccepts];

        for (int index = 0; index < _acceptEventArgs.Length; index++)
        {
            SocketAsyncEventArgs acceptEventArgs = new SocketAsyncEventArgs();
            acceptEventArgs.Completed += AcceptEventArgsOnCompleted;
            _acceptEventArgs[index] = acceptEventArgs;
        }
    }

    internal bool IsRunning => _isRunning;

    internal void Start()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(SaeaAcceptLoop));
            }

            if (_isRunning)
            {
                throw new InvalidOperationException("The accept loop is already running.");
            }

            _listenSocket = BindListenSocket();
            _isRunning = true;
            int runGeneration = Interlocked.Increment(ref _runGeneration);

            foreach (SocketAsyncEventArgs acceptEventArgs in _acceptEventArgs)
            {
                acceptEventArgs.UserToken = runGeneration;
                StartAccept(acceptEventArgs, runGeneration);
            }
        }
    }

    internal void Stop(int timeoutMs)
    {
        Socket? socketToClose;
        lock (_stateLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            Interlocked.Increment(ref _runGeneration);
            socketToClose = _listenSocket;
            _listenSocket = null;
        }

        Service.CloseSocket(socketToClose);
        WaitForDrain(timeoutMs);
    }

    internal bool WaitForDrain(int timeoutMs)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _pendingAccepts) > 0)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop(_options.DrainTimeoutMs);

        foreach (SocketAsyncEventArgs acceptEventArgs in _acceptEventArgs)
        {
            acceptEventArgs.Dispose();
        }
    }

    private Socket BindListenSocket()
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _options.BindRetries; attempt++)
        {
            Socket? listenSocket = null;
            try
            {
                listenSocket = new Socket(_ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                _options.ListenerSocketSettings.ConfigureSocket(listenSocket, Logger);
                _options.ListenerSocketSettings.SetSocketOptions(listenSocket, Logger);
                listenSocket.Bind(new IPEndPoint(_ipAddress, _port));
                listenSocket.Listen(_options.ListenerSocketSettings.Backlog);
                return listenSocket;
            }
            catch (Exception exception)
            {
                lastException = exception;
                Service.CloseSocket(listenSocket);

                if (attempt == _options.BindRetries)
                {
                    break;
                }

                Thread.Sleep(200);
            }
        }

        throw new InvalidOperationException(
            $"Unable to bind {_ipAddress}:{_port} after {_options.BindRetries} attempts.",
            lastException);
    }

    private void StartAccept(SocketAsyncEventArgs acceptEventArgs, int runGeneration)
    {
        if (!_isRunning || runGeneration != Volatile.Read(ref _runGeneration))
        {
            return;
        }

        Socket? listenSocket = _listenSocket;
        if (listenSocket is null)
        {
            return;
        }

        acceptEventArgs.AcceptSocket = null;
        acceptEventArgs.UserToken = runGeneration;
        Interlocked.Increment(ref _pendingAccepts);

        try
        {
            bool pending = listenSocket.AcceptAsync(acceptEventArgs);
            if (!pending)
            {
                ProcessAccept(acceptEventArgs);
            }
        }
        catch (ObjectDisposedException)
        {
            CompleteAccept(acceptEventArgs, null, SocketError.OperationAborted);
        }
        catch (SocketException exception)
        {
            CompleteAccept(acceptEventArgs, null, exception.SocketErrorCode);
        }
    }

    private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
    {
        CompleteAccept(acceptEventArgs, acceptEventArgs.AcceptSocket, acceptEventArgs.SocketError);
    }

    private void CompleteAccept(SocketAsyncEventArgs acceptEventArgs, Socket? acceptedSocket, SocketError socketError)
    {
        int callbackGeneration = acceptEventArgs.UserToken is int generation ? generation : 0;
        int activeGeneration = Volatile.Read(ref _runGeneration);

        try
        {
            if (!_isRunning || callbackGeneration != activeGeneration)
            {
                Service.CloseSocket(acceptedSocket);
                return;
            }

            if (socketError != SocketError.Success || acceptedSocket is null)
            {
                Service.CloseSocket(acceptedSocket);
                if (socketError != SocketError.OperationAborted && socketError != SocketError.Interrupted && _options.DebugMode)
                {
                    Logger.Debug($"Accept failed with {socketError}.");
                }

                return;
            }

            try
            {
                _handler.OnSocketAccepted(acceptedSocket);
            }
            catch (Exception exception)
            {
                Logger.Exception(exception);
                Service.CloseSocket(acceptedSocket);
            }
        }
        finally
        {
            acceptEventArgs.AcceptSocket = null;
            Interlocked.Decrement(ref _pendingAccepts);

            if (_isRunning && callbackGeneration == Volatile.Read(ref _runGeneration))
            {
                StartAccept(acceptEventArgs, callbackGeneration);
            }
        }
    }

    private void AcceptEventArgsOnCompleted(object? sender, SocketAsyncEventArgs acceptEventArgs)
    {
        ProcessAccept(acceptEventArgs);
    }
}
