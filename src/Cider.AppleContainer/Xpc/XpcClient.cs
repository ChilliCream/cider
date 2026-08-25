using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Per-call knobs for <see cref="XpcClient.SendAsync"/>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.4's per-route timeout table).
/// </summary>
internal readonly record struct XpcCallOptions
{
    /// <summary>Client-side deadline; <c>null</c> means no timeout — the call blocks until the
    /// daemon replies, exactly like the Swift client's <c>responseTimeout: nil</c> calls
    /// (<c>containerWait/Stop/Kill/Delete/Logs/Dial/Stats/Export/StartProcess/Resize</c>, §1.4).</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>When <c>true</c>, the blocking sync send runs on a dedicated thread instead of a
    /// thread-pool thread, so a long call never starves the pool. Only <c>containerWait</c> needs
    /// this today — it both blocks until the process exits and, per <see cref="Timeout"/>, carries
    /// no client-side budget either.</summary>
    public bool LongRunning { get; init; }

    /// <summary>The Swift client's default for everything on <c>ContainerClient</c> (§1.4).</summary>
    public static XpcCallOptions Default { get; } = new() { Timeout = XpcClient.DefaultTimeout };

    /// <summary><c>containerList</c>'s own override (§1.4).</summary>
    public static XpcCallOptions List { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>No client-side timeout at all (§1.4's "no timeout" row).</summary>
    public static XpcCallOptions NoTimeout { get; } = new() { Timeout = null };

    /// <summary><c>containerWait</c>: no timeout, dedicated thread.</summary>
    public static XpcCallOptions LongRunningNoTimeout { get; } = new() { Timeout = null, LongRunning = true };
}

/// <summary>
/// One XPC client connection to a single mach service — one instance per service, as the task's
/// fix direction calls for. Connections are created lazily on first send, are safe to call
/// concurrently from multiple callers, and self-heal: the event handler block notices
/// <c>XPC_ERROR_CONNECTION_INTERRUPTED</c>/<c>XPC_ERROR_CONNECTION_INVALID</c> and marks the
/// connection stale, so the next <see cref="SendAsync"/> transparently recreates it (the apiserver
/// restarting is invisible to the caller past one call) rather than reusing a dead connection.
/// </summary>
internal sealed class XpcClient : IDisposable
{
    /// <summary>The Swift client's default for everything on <c>ContainerClient</c>
    /// (<c>XPCClient.swift:27</c>, §1.4).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One generation of the underlying native connection. Kept as its own object — rather than a
    /// bare <c>nint</c> handle plus a client-wide "broken" flag — so that the terminal
    /// <c>XPC_ERROR_CONNECTION_INVALID</c> event for a stale connection (which libxpc guarantees to
    /// deliver exactly once, asynchronously, some time after <c>xpc_connection_cancel</c>) can only
    /// ever mark *this* generation broken, never whatever generation happens to be current by the
    /// time it arrives. Without that, a stale C1's guaranteed-terminal event landing after
    /// <see cref="EnsureConnection"/> has already replaced it with a healthy C2 would tear C2 down
    /// too — the same failure mode a timed-out/abandoned <see cref="SendSync"/> that finally returns
    /// against a since-replaced connection would trigger.
    /// </summary>
    private sealed class Connection
    {
        public nint Handle;
        public nint Block;
        public volatile bool Broken;
    }

    private readonly string _serviceName;
    private readonly ILogger _logger;
    private readonly Lock _connectionLock = new();

    private Connection? _current;
    private volatile bool _disposed;

    public XpcClient(string serviceName, ILogger logger)
    {
        _serviceName = serviceName;
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="request"/> and returns its reply. Always takes ownership of
    /// <paramref name="request"/> — it is disposed before this returns, on every path, so callers
    /// never need their own <c>using</c> for it.
    /// </summary>
    /// <exception cref="XpcException">
    /// The connection was interrupted/invalid, the call timed out, or the daemon rejected it —
    /// see <see cref="XpcException.ToRuntimeException"/> to cross the <c>IContainerRuntime</c> seam.
    /// </exception>
    public async Task<XpcMessage> SendAsync(XpcMessage request, XpcCallOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var route = request.Route;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Checked before EnsureConnection: EnsureConnection hands out a caller-owned retain
            // that only SendSync ever releases (via RunWithTimeoutAsync/RunOnDedicatedThreadAsync
            // below). If ct were already cancelled, bailing out here means that retain is never
            // taken in the first place, so there is nothing for SendSync to have to balance.
            ct.ThrowIfCancellationRequested();
            var connection = EnsureConnection();

            var replyDict = options.LongRunning
                ? await RunOnDedicatedThreadAsync(connection, request, ct).ConfigureAwait(false)
                : await RunWithTimeoutAsync(connection, request, options.Timeout, ct).ConfigureAwait(false);

            return new XpcMessage(replyDict);
        }
        finally
        {
            request.Dispose();
            _logger.LogDebug("xpc {Service}/{Route} {Ms}ms", _serviceName, route, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<XpcDictionary> RunWithTimeoutAsync(
        Connection connection, XpcMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        // CancellationToken.None, not ct: SendSync is the only place that releases the
        // caller-owned retain EnsureConnection handed out, so it must always actually run. Passing
        // ct here would let an already-cancelled (or concurrently cancelled) token make Task.Run
        // return a cancelled task without ever calling SendSync, leaking that retain. ct still
        // governs the wait below, exactly like the Swift client's purely client-side timeout (§1.4).
        var sendTask = Task.Run(() => SendSync(connection, request), CancellationToken.None);
        if (timeout is not { } budget)
        {
            return await sendTask.ConfigureAwait(false);
        }

        try
        {
            // WaitAsync races the budget (and ct) against the already-running sendTask without an
            // extra Task.Delay allocation per call — a real cost at the sub-millisecond scale the
            // raw sync send runs at (docs/spikes/xpc/04-dotnet-xpc-probe-report.md).
            return await sendTask.WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Purely client-side, exactly like the Swift client (§1.4): the underlying sync send is
            // still blocked in native code. Cancelling the connection here releases that blocked
            // thread promptly with an XPC_ERROR_CONNECTION_INVALID reply instead of leaving it
            // hanging until the daemon eventually replies (or never does); EnsureConnection then
            // transparently reconnects on the next call. The abandoned task is only observed, never
            // awaited further, so its eventual fault (or success, which must still be disposed
            // rather than leaked) can never surface as an unobserved exception, or leaked reply,
            // once the caller has already moved on with a Timeout.
            connection.Broken = true;
            XpcNative.xpc_connection_cancel(connection.Handle);
            _ = sendTask.ContinueWith(
                static t =>
                {
                    if (t.IsFaulted)
                    {
                        t.Exception!.Handle(_ => true);
                    }
                    else if (t.IsCompletedSuccessfully)
                    {
                        t.Result.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw XpcException.Timeout($"XPC timeout for request to {_serviceName}/{request.Route}");
        }
    }

    private Task<XpcDictionary> RunOnDedicatedThreadAsync(Connection connection, XpcMessage request, CancellationToken ct) =>
        // CancellationToken.None, not ct, for the same reason as RunWithTimeoutAsync above: SendSync
        // must always run so it always releases EnsureConnection's caller-owned retain. Cancellation
        // is not supported on this long-running path anyway — the sync send itself is never
        // interrupted by ct.
        Task.Factory.StartNew(
            () => SendSync(connection, request),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>The blocking call itself — always run off whatever thread called <see cref="SendAsync"/>
    /// (see <see cref="RunWithTimeoutAsync"/>/<see cref="RunOnDedicatedThreadAsync"/>). Runs against
    /// a <paramref name="connection"/> that already carries a caller-owned native retain, taken by
    /// <see cref="EnsureConnection"/> under <c>_connectionLock</c> at hand-out time — not here — so
    /// the retain and the lookup that hands the connection out can never be split by a concurrent
    /// <see cref="Dispose"/> or reconnect landing in between. This method releases that retain when
    /// the blocking call completes: <see cref="RunWithTimeoutAsync"/> can abandon this call (on
    /// timeout) and the client can be disposed concurrently, either of which would otherwise drop
    /// the connection's only remaining reference — via <see cref="CancelAndReleaseConnection"/> —
    /// while <c>xpc_connection_send_message_with_reply_sync</c> is still blocked on it. On a
    /// client-side timeout, <see cref="RunWithTimeoutAsync"/> now also cancels the connection so
    /// this blocked thread is released promptly with an <c>XPC_ERROR_CONNECTION_INVALID</c> reply.</summary>
    private XpcDictionary SendSync(Connection connection, XpcMessage request)
    {
        try
        {
            var replyPtr = request.Dictionary.Use(handle =>
                XpcNative.xpc_connection_send_message_with_reply_sync(connection.Handle, handle));

            if (replyPtr == 0)
            {
                connection.Broken = true;
                throw XpcException.Interrupted(
                    $"xpc_connection_send_message_with_reply_sync returned NULL for {_serviceName}/{request.Route}");
            }

            var type = XpcObject.TypeNameOf(replyPtr);
            if (type == "error")
            {
                var description = XpcObject.DescribeOf(replyPtr);
                var invalid = replyPtr == XpcErrorSentinels.ConnectionInvalid;
                XpcNative.xpc_release(replyPtr);
                connection.Broken = true;
                throw invalid ? XpcException.Invalid(description) : XpcException.Interrupted(description);
            }

            if (type != "dictionary")
            {
                var description = XpcObject.DescribeOf(replyPtr);
                XpcNative.xpc_release(replyPtr);
                throw XpcException.ApiServer("unknown", $"unexpected XPC reply type '{type}': {description}");
            }

            var reply = new XpcDictionary(replyPtr);

            // §1.3: apiserver errors ride inside an ordinary reply dictionary, never as a separate
            // XPC error object — every dictionary reply must be checked for the envelope. The route
            // ran and the daemon rejected the call, so unlike the two branches above this leaves the
            // connection itself un-broken; only this one call failed.
            var envelope = reply.GetData(XpcMessage.ErrorKey);
            if (envelope is not null)
            {
                reply.Dispose();
                throw XpcErrorMapper.Decode(envelope);
            }

            return reply;
        }
        finally
        {
            XpcNative.xpc_release(connection.Handle);
        }
    }

    /// <summary>Returns the connection to send on, creating one if the current one is missing or
    /// broken. The returned <see cref="Connection"/> carries a caller-owned native retain — taken
    /// here, under <see cref="_connectionLock"/>, on top of the client's own reference — so the
    /// hand-out and the retain can never be split by a concurrent <see cref="Dispose"/> or reconnect.
    /// <see cref="SendSync"/> is the one place that releases it.</summary>
    private Connection EnsureConnection()
    {
        lock (_connectionLock)
        {
            if (_current is { Broken: false } c)
            {
                XpcNative.xpc_retain(c.Handle);
                return c;
            }

            if (_current is { } stale)
            {
                CancelAndReleaseConnection(stale.Handle);
            }

            var handle = XpcNative.xpc_connection_create_mach_service(_serviceName, 0, 0);
            if (handle == 0)
            {
                throw XpcException.Invalid($"xpc_connection_create_mach_service({_serviceName}) returned NULL");
            }

            var connection = new Connection { Handle = handle };
            var block = XpcBlock.CreateEventHandler((b, xpcObject) => OnConnectionEvent(connection, b, xpcObject));
            connection.Block = block;

            // Order matters: the handler must be installed before activation, or an event racing
            // activation is dropped (and cancelling a connection whose handler was never installed
            // is unsafe — confirmed live while building this client).
            XpcNative.xpc_connection_set_event_handler(handle, block);
            XpcNative.xpc_connection_activate(handle);

            // The caller-owned retain on top of the client's own reference from
            // xpc_connection_create_mach_service above — see this method's doc comment.
            XpcNative.xpc_retain(handle);
            _current = connection;
            return connection;
        }
    }

    /// <summary>Marks only <paramref name="connection"/> broken — never touches <see cref="_current"/>
    /// or any other generation, which is exactly what makes a stale connection's guaranteed-terminal
    /// event, arriving after <see cref="EnsureConnection"/> has already moved on to a fresh
    /// generation, harmless (see <see cref="Connection"/>'s own doc comment).</summary>
    private void OnConnectionEvent(Connection connection, nint block, nint xpcObject)
    {
        if (xpcObject != XpcErrorSentinels.ConnectionInterrupted && xpcObject != XpcErrorSentinels.ConnectionInvalid)
        {
            return;
        }

        var invalid = xpcObject == XpcErrorSentinels.ConnectionInvalid;
        _logger.LogDebug("xpc {Service} connection {State}", _serviceName, invalid ? "invalid" : "interrupted");
        connection.Broken = true;

        if (invalid)
        {
            // XPC_ERROR_CONNECTION_INVALID is Apple's documented "safe to release everything now"
            // signal: it is guaranteed to be the last event this connection's handler ever
            // receives, which is exactly what makes freeing the block from inside its own callback
            // safe here and unsafe everywhere else (see XpcBlock.Free's doc comment).
            XpcBlock.Free(block);
        }
    }

    /// <summary>
    /// Test-only: cancels the live connection out from under this client — the same libxpc call
    /// the verification scenario for this task drives directly — without disposing the client. The
    /// next <see cref="SendAsync"/> must recreate the connection and succeed transparently, exactly
    /// as it would after a real apiserver restart.
    /// </summary>
    internal void DebugCancelConnection()
    {
        lock (_connectionLock)
        {
            if (_current is { } connection)
            {
                XpcNative.xpc_connection_cancel(connection.Handle);
            }
        }
    }

    /// <summary>Test-only: identifies the current connection generation, purely by reference — does
    /// not dereference the handle. Used to assert a reconnect only happened once (or not at all)
    /// across a sequence of sends, without relying on native pointer values that could in principle
    /// be reused after a release.</summary>
    internal object? DebugConnectionGeneration
    {
        get
        {
            lock (_connectionLock)
            {
                return _current;
            }
        }
    }

    /// <summary>Cancels and releases <paramref name="handle"/> without touching its event-handler
    /// block — that block frees itself from <see cref="OnConnectionEvent"/> once the asynchronous,
    /// guaranteed-terminal <c>XPC_ERROR_CONNECTION_INVALID</c> actually arrives (see
    /// <see cref="XpcBlock.Free"/>'s doc comment for why freeing it here, synchronously, would race
    /// that event and segfault).</summary>
    private static void CancelAndReleaseConnection(nint handle)
    {
        XpcNative.xpc_connection_cancel(handle);
        XpcNative.xpc_release(handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_connectionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_current is { } connection)
            {
                CancelAndReleaseConnection(connection.Handle);
                _current = null;
            }
        }
    }
}
