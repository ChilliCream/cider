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

    /// <summary>When <c>true</c>, this call runs on its own single-use native XPC connection and a
    /// dedicated managed thread, instead of the client's shared connection and the thread pool.
    /// Reserved for <c>containerWait</c>/<c>Logs</c>/<c>Dial</c> — the routes that can legitimately
    /// block indefinitely (§1.4's "no timeout" row) — so that a per-call timeout or cancellation on
    /// one of these calls only ever tears down *that* call's own throwaway connection, never the
    /// shared connection every other concurrent call (list, ping, kill, stop, …) depends on. Task's
    /// binding ruling (2026-08-25): a per-call timeout/cancel must never tear down the shared
    /// connection — only a real connection-level error, observed reactively, may do that; see
    /// <see cref="RunWithTimeoutAsync"/> vs. <see cref="SendOnDedicatedConnectionAsync"/>.</summary>
    public bool DedicatedConnection { get; init; }

    /// <summary>The Swift client's default for everything on <c>ContainerClient</c> (§1.4).</summary>
    public static XpcCallOptions Default { get; } = new() { Timeout = XpcClient.DefaultTimeout };

    /// <summary><c>containerList</c>'s own override (§1.4).</summary>
    public static XpcCallOptions List { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>No client-side timeout at all (§1.4's "no timeout" row), on the shared connection.</summary>
    public static XpcCallOptions NoTimeout { get; } = new() { Timeout = null };

    /// <summary><c>containerWait</c>/<c>Logs</c>/<c>Dial</c>: no client-side timeout (they block
    /// until the process exits / the stream ends / the dial completes) and a dedicated per-call
    /// connection + thread (see <see cref="DedicatedConnection"/>).</summary>
    public static XpcCallOptions LongRunning { get; } = new() { Timeout = null, DedicatedConnection = true };
}

/// <summary>
/// One XPC client connection to a single mach service — one instance per service, as the task's
/// fix direction calls for. The shared connection is created lazily on first send, is safe to call
/// concurrently from multiple callers, and self-heals: the event handler block notices
/// <c>XPC_ERROR_CONNECTION_INTERRUPTED</c>/<c>XPC_ERROR_CONNECTION_INVALID</c> and marks the
/// connection stale, so the next <see cref="SendAsync"/> transparently recreates it (the apiserver
/// restarting is invisible to the caller past one call) rather than reusing a dead connection.
/// Calls flagged <see cref="XpcCallOptions.DedicatedConnection"/> bypass the shared connection
/// entirely — see <see cref="SendOnDedicatedConnectionAsync"/>.
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
    /// too. Also doubles as the single-use handle+block pair for a
    /// <see cref="SendOnDedicatedConnectionAsync"/> call, where <see cref="Broken"/> is never read.
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
            // Checked up front: for the shared-connection path, EnsureConnection hands out a
            // caller-owned retain that only SendSync ever releases (via RunWithTimeoutAsync below).
            // If ct were already cancelled, bailing out here means that retain is never taken in
            // the first place, so there is nothing to have to balance.
            ct.ThrowIfCancellationRequested();

            var replyDict = options.DedicatedConnection
                ? await SendOnDedicatedConnectionAsync(request, options.Timeout, ct).ConfigureAwait(false)
                : await RunWithTimeoutAsync(EnsureConnection(), request, options.Timeout, ct).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
        {
            // Purely client-side, exactly like the Swift client (§1.4): the underlying sync send is
            // still blocked in native code, on the SHARED connection every other concurrent call may
            // depend on. Task's binding ruling (2026-08-25): a per-call timeout/cancel must NEVER
            // tear that connection down — only a real connection-level error, observed reactively in
            // SendSync/OnConnectionEvent, may mark it broken. So unlike
            // SendOnDedicatedConnectionAsync below, this deliberately leaves the connection alone:
            // the abandoned send stays blocked until the daemon eventually replies (or the connection
            // is later torn down for an unrelated reason), pinning one thread-pool thread for that
            // long — unbounded over the process lifetime across repeated abandoned timeouts, an
            // accepted cost of this path's ruling (pre-cb184d0 behavior). The abandoned
            // task is only observed, never awaited further, so its eventual fault (or success, which must still
            // be disposed rather than leaked) can never surface as an unobserved exception, or leaked
            // reply, once the caller has already moved on with an exception of its own. Rethrowing
            // (not wrapping) preserves the original exception type/identity for the caller.
            ObserveAbandoned(sendTask);

            if (ex is TimeoutException)
            {
                throw XpcException.Timeout($"XPC timeout for request to {_serviceName}/{request.Route}");
            }

            throw;
        }
    }

    /// <summary>Sends on a fresh, single-use native connection — never <see cref="_current"/> —
    /// created and torn down entirely within this one call. Because nothing else ever touches this
    /// connection, cancelling it on timeout/<paramref name="ct"/> cancellation is always safe: there
    /// is no shared connection to collateral-damage, unlike <see cref="RunWithTimeoutAsync"/>. This
    /// is what <see cref="XpcCallOptions.DedicatedConnection"/> requests for
    /// <c>containerWait</c>/<c>Logs</c>/<c>Dial</c> (task's binding ruling, 2026-08-25).</summary>
    private async Task<XpcDictionary> SendOnDedicatedConnectionAsync(XpcMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        var handle = XpcNative.xpc_connection_create_mach_service(_serviceName, 0, 0);
        if (handle == 0)
        {
            throw XpcException.Invalid($"xpc_connection_create_mach_service({_serviceName}) returned NULL");
        }

        // Global/immortal block, no captured state (XpcBlock's own doc comment) — safe to build
        // with a static lambda that only ever touches the sentinel + the block's own address it is
        // handed back, never this Connection or client instance.
        var block = XpcBlock.CreateEventHandler(static (self, xpcObject) =>
        {
            if (xpcObject == XpcErrorSentinels.ConnectionInvalid)
            {
                // Guaranteed terminal, guaranteed last event for this connection — safe to stop
                // routing now. Leaked, not freed: see XpcBlock.Detach's doc comment.
                XpcBlock.Detach(self);
            }
        });

        // Order matters, same as EnsureConnection below: the handler must be installed before
        // activation, or an event racing activation is dropped.
        XpcNative.xpc_connection_set_event_handler(handle, block);
        XpcNative.xpc_connection_activate(handle);

        // Awaiting-scope retain, on top of the one reference xpc_connection_create_mach_service
        // handed out above. SendSyncDedicated's finally (via CancelAndReleaseConnection) only ever
        // releases the send task's own reference, never this one, so cancelling below on
        // timeout/ct always runs against a live handle regardless of whether the send task's
        // finally has already run — see the finally right below and this method's own doc comment.
        XpcNative.xpc_retain(handle);

        var connection = new Connection { Handle = handle, Block = block };

        try
        {
            // LongRunning, not the thread pool: these calls (wait/logs/dial) can legitimately block
            // for a long time, and unlike RunWithTimeoutAsync's Task.Run there is no shared-retain
            // balancing concern forcing CancellationToken.None here either way — SendSyncDedicated
            // always cancels and releases its own reference to this single-use connection in its
            // own finally, however it completes.
            var sendTask = Task.Factory.StartNew(
                () => SendSyncDedicated(connection, request),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            if (timeout is null && !ct.CanBeCanceled)
            {
                return await sendTask.ConfigureAwait(false);
            }

            try
            {
                return timeout is { } budget
                    ? await sendTask.WaitAsync(budget, ct).ConfigureAwait(false)
                    : await sendTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                // Safe unconditionally, unlike RunWithTimeoutAsync's shared connection: nothing else
                // ever sends on this connection, so cancelling it here can never race or
                // collateral-damage a concurrent call. Also safe regardless of whether the abandoned
                // send task's own finally has already run: this call holds the awaiting-scope retain
                // taken above, which SendSyncDedicated's finally never touches, so the handle is
                // guaranteed live here even if the send task raced ahead and already released its
                // own reference.
                XpcNative.xpc_connection_cancel(handle);
                ObserveAbandoned(sendTask);

                if (ex is TimeoutException)
                {
                    throw XpcException.Timeout($"XPC timeout for request to {_serviceName}/{request.Route}");
                }

                throw;
            }
        }
        finally
        {
            // Balances the awaiting-scope retain taken above — released on every exit path (fast
            // path, successful wait, and the timeout/ct catch above) regardless of what the
            // abandoned send task's own finally does with its own separate reference.
            XpcNative.xpc_release(handle);
        }
    }

    /// <summary>Fires and forgets an abandoned (timed-out/cancelled) send task: observes its fault
    /// so it can never surface as an unobserved-task-exception, and disposes its reply if it ends up
    /// completing successfully after the caller already moved on — used by both
    /// <see cref="RunWithTimeoutAsync"/> and <see cref="SendOnDedicatedConnectionAsync"/>.</summary>
    private static void ObserveAbandoned(Task<XpcDictionary> sendTask) =>
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

    /// <summary>The blocking call itself — always run off whatever thread called <see cref="SendAsync"/>
    /// (see <see cref="RunWithTimeoutAsync"/>). Runs against a <paramref name="connection"/> that
    /// already carries a caller-owned native retain, taken by <see cref="EnsureConnection"/> under
    /// <c>_connectionLock</c> at hand-out time — not here — so the retain and the lookup that hands
    /// the connection out can never be split by a concurrent <see cref="Dispose"/> or reconnect
    /// landing in between. This method releases that retain when the blocking call completes:
    /// <see cref="RunWithTimeoutAsync"/> can abandon this call (on timeout/cancel, deliberately
    /// without touching the connection — see its own doc comment) and the client can be disposed
    /// concurrently, either of which would otherwise drop the connection's only remaining reference
    /// — via <see cref="CancelAndReleaseConnection"/> — while
    /// <c>xpc_connection_send_message_with_reply_sync</c> is still blocked on it.</summary>
    private XpcDictionary SendSync(Connection connection, XpcMessage request)
    {
        try
        {
            var replyPtr = request.Dictionary.Use(handle =>
                XpcNative.xpc_connection_send_message_with_reply_sync(connection.Handle, handle));
            return DecodeReply(replyPtr, request.Route, connection);
        }
        finally
        {
            XpcNative.xpc_release(connection.Handle);
        }
    }

    /// <summary>The dedicated-connection counterpart of <see cref="SendSync"/>: same blocking call
    /// and the same reply decoding (<see cref="DecodeReply"/>), but <paramref name="connection"/> is
    /// single-use, so unlike <see cref="SendSync"/> this always cancels it — not just releases the
    /// retain — regardless of outcome, since nothing will ever send on it again. This releases only
    /// the send task's own reference: <see cref="SendOnDedicatedConnectionAsync"/> holds a second,
    /// awaiting-scope reference of its own (taken right after activation) that this method never
    /// touches, so the handle stays live for that caller's own cancel/release even if this method's
    /// finally has already run.</summary>
    private XpcDictionary SendSyncDedicated(Connection connection, XpcMessage request)
    {
        try
        {
            var replyPtr = request.Dictionary.Use(handle =>
                XpcNative.xpc_connection_send_message_with_reply_sync(connection.Handle, handle));
            return DecodeReply(replyPtr, request.Route, connection);
        }
        finally
        {
            CancelAndReleaseConnection(connection.Handle);
        }
    }

    /// <summary>Interprets a raw <c>xpc_connection_send_message_with_reply_sync</c> reply — shared
    /// by <see cref="SendSync"/> and <see cref="SendSyncDedicated"/>. Marks
    /// <paramref name="connection"/> broken on a transport-level failure (meaningless for a
    /// single-use dedicated connection, but harmless: it is discarded either way).</summary>
    private XpcDictionary DecodeReply(nint replyPtr, string route, Connection connection)
    {
        if (replyPtr == 0)
        {
            connection.Broken = true;
            throw XpcException.Interrupted(
                $"xpc_connection_send_message_with_reply_sync returned NULL for {_serviceName}/{route}");
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

    /// <summary>Returns the shared connection to send on, creating one if the current one is missing
    /// or broken. The returned <see cref="Connection"/> carries a caller-owned native retain — taken
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
            // receives. This runs on the block's own currently-executing invoke, which is exactly
            // what makes Detach (leak, not free) the right call here — see its doc comment.
            XpcBlock.Detach(block);
        }
    }

    /// <summary>
    /// Test-only: cancels the live shared connection out from under this client — the same libxpc
    /// call the verification scenario for this task drives directly — without disposing the client.
    /// The next <see cref="SendAsync"/> must recreate the connection and succeed transparently,
    /// exactly as it would after a real apiserver restart.
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

    /// <summary>Test-only: identifies the current shared-connection generation, purely by
    /// reference — does not dereference the handle. Used to assert a reconnect only happened once
    /// (or not at all) across a sequence of sends, without relying on native pointer values that
    /// could in principle be reused after a release.</summary>
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
    /// block — that block detaches itself from <see cref="OnConnectionEvent"/> (shared connection)
    /// or the dedicated-connection handler above once the asynchronous, guaranteed-terminal
    /// <c>XPC_ERROR_CONNECTION_INVALID</c> actually arrives (see <see cref="XpcBlock.Detach"/>'s doc
    /// comment for why detaching here, synchronously, before that event fires would be premature —
    /// cancellation is asynchronous and the event can still be in flight).</summary>
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
