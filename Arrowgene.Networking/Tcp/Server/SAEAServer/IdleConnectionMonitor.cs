using System;
using System.Collections.Generic;
using System.Threading;
using Arrowgene.Logging;

namespace Arrowgene.Networking.Tcp.Server.SAEAServer;

internal sealed class IdleConnectionMonitor : IDisposable
{
    private static readonly ILogger Logger = LogProvider.Logger(typeof(IdleConnectionMonitor));

    private readonly object _stateLock;
    private readonly TimeSpan _timeout;
    private readonly SaeaSessionRegistry _registry;
    private readonly Action<SaeaSessionHandle> _onIdleSession;
    private readonly string _threadName;

    private Thread? _thread;
    private volatile bool _isRunning;
    private CancellationToken _cancellationToken;

    internal IdleConnectionMonitor(
        TimeSpan timeout,
        SaeaSessionRegistry registry,
        Action<SaeaSessionHandle> onIdleSession,
        string identity)
    {
        _timeout = timeout;
        _registry = registry;
        _onIdleSession = onIdleSession;
        _threadName = string.IsNullOrWhiteSpace(identity)
            ? "SaeaServer_IdleMonitor"
            : $"{identity}_SaeaServer_IdleMonitor";
        _stateLock = new object();
    }

    internal bool IsRunning => _isRunning;

    internal void Start(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("The idle monitor is already running.");
            }

            _cancellationToken = cancellationToken;
            _isRunning = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = _threadName
            };
            _thread.Start();
        }
    }

    internal void Stop(int timeoutMs)
    {
        Thread? threadToJoin;
        lock (_stateLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            threadToJoin = _thread;
            _thread = null;
        }

        if (threadToJoin is null || Thread.CurrentThread == threadToJoin)
        {
            return;
        }

        if (!threadToJoin.Join(timeoutMs))
        {
            Logger.Error($"Failed to stop idle monitor thread within {timeoutMs}ms.");
        }
    }

    public void Dispose()
    {
        Stop(5000);
    }

    private void Run()
    {
        List<SaeaSessionHandle> snapshot = new List<SaeaSessionHandle>();
        int delayMs = (int)Math.Clamp(_timeout.TotalMilliseconds, 1000, 30000);

        while (!_cancellationToken.IsCancellationRequested)
        {
            if (_cancellationToken.WaitHandle.WaitOne(delayMs))
            {
                break;
            }

            _registry.Snapshot(snapshot);
            long now = Environment.TickCount64;
            foreach (SaeaSessionHandle handle in snapshot)
            {
                if (!handle.TryGetSession(out SaeaSession? session) || session is null || !session.IsConnected)
                {
                    continue;
                }

                long lastActivity = Math.Max(session.LastReceiveTicks, session.LastSendTicks);
                if (now - lastActivity >= _timeout.TotalMilliseconds)
                {
                    _onIdleSession(handle);
                }
            }
        }
    }
}
