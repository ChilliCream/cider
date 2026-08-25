namespace Cider.Daemon.BuildKit;

/// <summary>
/// Every <see cref="CliSession"/> currently dialed through <c>POST /session</c>, keyed by the id
/// BuildKit's session dialer sent in <c>X-Docker-Expose-Session-Uuid</c>. A build's
/// <c>Control/Solve</c> can name a session id before its <c>/session</c> connection has actually
/// upgraded (buildkit's manager blocks on a condvar waiting for the session to attach — session/
/// manager.go:149-191), so lookups come in two shapes: <see cref="TryGet"/> for "is it here right
/// now" and <see cref="WaitAsync"/> for "block until it shows up, or time out".
/// </summary>
public sealed class CliSessionRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CliSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskCompletionSource<CliSession>>> _waiters = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="session"/>. Throws <see cref="InvalidOperationException"/> when its
    /// id is already registered — buildkit's own wording for the same situation is
    /// <c>"session %s already exists"</c> (session/manager.go).
    /// </summary>
    public void Register(CliSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<TaskCompletionSource<CliSession>>? waiters;
        lock (_gate)
        {
            if (!_sessions.TryAdd(session.Id, session))
            {
                throw new InvalidOperationException($"cider: session {session.Id} already exists");
            }

            _waiters.Remove(session.Id, out waiters);
        }

        if (waiters is not null)
        {
            foreach (var waiter in waiters)
            {
                waiter.TrySetResult(session);
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="CliSession"/> over <paramref name="stream"/> and registers it — the shape
    /// <c>Control.Session</c>'s bake-tunneled bidi path needs, where there is no hijacked
    /// <see cref="Microsoft.AspNetCore.Connections.ConnectionContext"/> to build the session from,
    /// only a raw duplex stream. Throws <see cref="InvalidOperationException"/> on a duplicate id,
    /// same as <see cref="Register"/>, and disposes the session it built in that case.
    /// </summary>
    public CliSession RegisterFromStream(string id, string? sharedKey, IEnumerable<string> methods, Stream stream)
    {
        var session = new CliSession(id, sharedKey, methods, stream);
        try
        {
            Register(session);
        }
        catch
        {
            _ = DisposeQuietlyAsync(session);
            throw;
        }

        return session;
    }

    /// <summary>Removes the session with <paramref name="id"/>, if any, and marks it closed.</summary>
    public void Unregister(string id)
    {
        CliSession? removed;
        lock (_gate)
        {
            _sessions.Remove(id, out removed);
        }

        removed?.Close();
    }

    /// <summary>Looks up a currently-registered session by id.</summary>
    public bool TryGet(string id, out CliSession? session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out session);
        }
    }

    /// <summary>
    /// Resolves once a session with <paramref name="id"/> is registered, or once already registered
    /// when called. Times out (the task faults with <see cref="OperationCanceledException"/>) after
    /// <paramref name="timeout"/>, or earlier if <paramref name="cancellationToken"/> fires.
    /// </summary>
    public Task<CliSession> WaitAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        TaskCompletionSource<CliSession> tcs;
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var existing))
            {
                return Task.FromResult(existing);
            }

            tcs = new TaskCompletionSource<CliSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(id, out var list))
            {
                list = [];
                _waiters[id] = list;
            }

            list.Add(tcs);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }

        var registration = cts.Token.Register(() =>
        {
            RemoveWaiter(id, tcs);
            tcs.TrySetCanceled(cts.Token);
        });

        _ = tcs.Task.ContinueWith(
            static (_, state) =>
            {
                var (source, reg) = ((CancellationTokenSource, CancellationTokenRegistration))state!;
                reg.Dispose();
                source.Dispose();
            },
            (cts, registration),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }

    private void RemoveWaiter(string id, TaskCompletionSource<CliSession> waiter)
    {
        lock (_gate)
        {
            if (_waiters.TryGetValue(id, out var list))
            {
                list.Remove(waiter);
                if (list.Count == 0)
                {
                    _waiters.Remove(id);
                }
            }
        }
    }

    private static async Task DisposeQuietlyAsync(CliSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
    }
}
