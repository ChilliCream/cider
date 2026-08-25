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

    private readonly string _serviceName;
    private readonly ILogger _logger;
    private readonly Lock _connectionLock = new();

    private nint _connection;
    private nint _eventBlock;
    private volatile bool _connectionBroken;
    private bool _disposed;

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
        nint connection, XpcMessage request, TimeSpan? timeout, CancellationToken ct)
    {
        var sendTask = Task.Run(() => SendSync(connection, request), ct);
        if (timeout is not { } budget)
        {
            return await sendTask.ConfigureAwait(false);
        }

        var winner = await Task.WhenAny(sendTask, Task.Delay(budget, ct)).ConfigureAwait(false);
        if (winner == sendTask)
        {
            return await sendTask.ConfigureAwait(false);
        }

        // The delay can also "win" because the caller's own token fired, not the budget — that is
        // cancellation, not a timeout, and must surface as such.
        ct.ThrowIfCancellationRequested();

        // Purely client-side, exactly like the Swift client (§1.4): the underlying sync send is
        // still blocked in native code and is not cancelled — only observed, so its eventual
        // fault (or success, which is simply discarded) can never surface as an unobserved
        // exception once the caller has already moved on with a Timeout.
        _ = sendTask.ContinueWith(
            static t => t.Exception?.Handle(_ => true),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        throw XpcException.Timeout($"XPC timeout for request to {_serviceName}/{request.Route}");
    }

    private Task<XpcDictionary> RunOnDedicatedThreadAsync(nint connection, XpcMessage request, CancellationToken ct) =>
        Task.Factory.StartNew(
            () => SendSync(connection, request),
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>The blocking call itself — always run off whatever thread called <see cref="SendAsync"/>
    /// (see <see cref="RunWithTimeoutAsync"/>/<see cref="RunOnDedicatedThreadAsync"/>).</summary>
    private XpcDictionary SendSync(nint connection, XpcMessage request)
    {
        var replyPtr = request.Dictionary.Use(handle =>
            XpcNative.xpc_connection_send_message_with_reply_sync(connection, handle));

        if (replyPtr == 0)
        {
            MarkBroken();
            throw XpcException.Interrupted(
                $"xpc_connection_send_message_with_reply_sync returned NULL for {_serviceName}/{request.Route}");
        }

        var type = XpcObject.TypeNameOf(replyPtr);
        if (type == "error")
        {
            var description = XpcObject.DescribeOf(replyPtr);
            var invalid = replyPtr == XpcErrorSentinels.ConnectionInvalid;
            XpcNative.xpc_release(replyPtr);
            MarkBroken();
            throw invalid ? XpcException.Invalid(description) : XpcException.Interrupted(description);
        }

        if (type != "dictionary")
        {
            var description = XpcObject.DescribeOf(replyPtr);
            XpcNative.xpc_release(replyPtr);
            throw XpcException.ApiServer("unknown", $"unexpected XPC reply type '{type}': {description}");
        }

        return new XpcDictionary(replyPtr);
    }

    private nint EnsureConnection()
    {
        lock (_connectionLock)
        {
            if (_connection != 0 && !_connectionBroken)
            {
                return _connection;
            }

            if (_connection != 0)
            {
                CancelAndReleaseConnection(_connection);
            }

            var connection = XpcNative.xpc_connection_create_mach_service(_serviceName, 0, 0);
            if (connection == 0)
            {
                throw XpcException.Invalid($"xpc_connection_create_mach_service({_serviceName}) returned NULL");
            }

            var block = XpcBlock.CreateEventHandler(OnConnectionEvent);

            // Order matters: the handler must be installed before activation, or an event racing
            // activation is dropped (and cancelling a connection whose handler was never installed
            // is unsafe — confirmed live while building this client).
            XpcNative.xpc_connection_set_event_handler(connection, block);
            XpcNative.xpc_connection_activate(connection);

            _connection = connection;
            _eventBlock = block;
            _connectionBroken = false;
            return connection;
        }
    }

    private void OnConnectionEvent(nint block, nint xpcObject)
    {
        if (xpcObject != XpcErrorSentinels.ConnectionInterrupted && xpcObject != XpcErrorSentinels.ConnectionInvalid)
        {
            return;
        }

        var invalid = xpcObject == XpcErrorSentinels.ConnectionInvalid;
        _logger.LogDebug("xpc {Service} connection {State}", _serviceName, invalid ? "invalid" : "interrupted");
        MarkBroken();

        if (invalid)
        {
            // XPC_ERROR_CONNECTION_INVALID is Apple's documented "safe to release everything now"
            // signal: it is guaranteed to be the last event this connection's handler ever
            // receives, which is exactly what makes freeing the block from inside its own callback
            // safe here and unsafe everywhere else (see XpcBlock.Free's doc comment).
            XpcBlock.Free(block);
        }
    }

    private void MarkBroken() => _connectionBroken = true;

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
            if (_connection != 0)
            {
                XpcNative.xpc_connection_cancel(_connection);
            }
        }
    }

    /// <summary>Cancels and releases <paramref name="connection"/> without touching its event-handler
    /// block — that block frees itself from <see cref="OnConnectionEvent"/> once the asynchronous,
    /// guaranteed-terminal <c>XPC_ERROR_CONNECTION_INVALID</c> actually arrives (see
    /// <see cref="XpcBlock.Free"/>'s doc comment for why freeing it here, synchronously, would race
    /// that event and segfault).</summary>
    private static void CancelAndReleaseConnection(nint connection)
    {
        XpcNative.xpc_connection_cancel(connection);
        XpcNative.xpc_release(connection);
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
            if (_connection != 0)
            {
                CancelAndReleaseConnection(_connection);
                _connection = 0;
                _eventBlock = 0;
            }
        }
    }
}
