using System.Collections.Concurrent;
using Grpc.Core;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// One daemon-owned <c>Control/Session</c> bridge toward buildkitd, held for as long as something
/// (a Bake's CLI stream, a Solve) needs it — see <see cref="SessionBridge.AttachAsync"/>, which hands
/// out exactly one of these per <see cref="Cider.Daemon.BuildKit.CliSession.Id"/> no matter how many
/// callers ask, ref-counted so the underlying <c>Control/Session</c> call and its Kestrel-side tunnel
/// connection are torn down only once nobody needs them any more.
/// </summary>
public sealed class SessionBridgeHandle : IAsyncDisposable
{
    private readonly SessionBridge _owner;
    private readonly AsyncDuplexStreamingCall<BytesMessage, BytesMessage> _call;
    private readonly BytesMessageStream _bytesStream;
    private readonly CancellationTokenSource _tunnelCts;
    private readonly Task _serveTask;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ExportResult>> _exports = new();
    private readonly ConcurrentBag<ExportResult> _produced = [];
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _refCount = 1;
    private int _disposed;

    internal SessionBridgeHandle(
        SessionBridge owner,
        CliSession cli,
        AsyncDuplexStreamingCall<BytesMessage, BytesMessage> call,
        BytesMessageStream bytesStream,
        CancellationTokenSource tunnelCts,
        Task serveTask,
        ILogger logger)
    {
        _owner = owner;
        Cli = cli;
        _call = call;
        _bytesStream = bytesStream;
        _tunnelCts = tunnelCts;
        _serveTask = serveTask;
        _logger = logger;

        _ = MonitorAsync();
    }

    /// <summary>The CLI session this bridge relays every unclaimed method to.</summary>
    public CliSession Cli { get; }

    /// <summary>
    /// Exporter ids (<c>buildkit-attachable-exporter-id</c>) whose <c>FileSend/DiffCopy</c> call must
    /// be captured to a tar file instead of forwarded to <see cref="Cli"/> — populated by whoever
    /// rewrote the matching exporter in the Solve this bridge belongs to (T7).
    /// </summary>
    public HashSet<int> CaptureExporterIds { get; } = [];

    /// <summary>Completes once this bridge has fully torn down (ref count reached zero, or a monitored source died).</summary>
    public Task Closed => _closedTcs.Task;

    /// <summary>
    /// The result of the captured <c>FileSend/DiffCopy</c> call for <paramref name="exporterId"/>,
    /// resolving once that call finishes. Safe to call before the export starts — the first caller
    /// (whichever comes first, this or the capture itself) creates the pending slot.
    /// </summary>
    public Task<ExportResult> ExportFor(int exporterId) => Slot(exporterId).Task;

    /// <summary>Bumps the ref count. Paired with <see cref="Release"/>.</summary>
    internal void AddRef() => Interlocked.Increment(ref _refCount);

    /// <summary>
    /// Drops one reference. At zero this bridge tears down: the <c>Control/Session</c> request
    /// stream is completed, the Kestrel-side tunnel connection is aborted, unclaimed export tars are
    /// deleted, and it is unregistered from <see cref="SessionBridge"/>.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) <= 0)
        {
            _ = DisposeAsync();
        }
    }

    internal void CompleteExport(int exporterId, ExportResult result)
    {
        _produced.Add(result);
        Slot(exporterId).TrySetResult(result);
    }

    internal void FailExport(int exporterId, Exception ex) => Slot(exporterId).TrySetException(ex);

    private TaskCompletionSource<ExportResult> Slot(int exporterId) =>
        _exports.GetOrAdd(exporterId, static _ => new TaskCompletionSource<ExportResult>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// Forces teardown (ref count irrelevant) the moment the CLI session disconnects or the tunnel
    /// connection toward buildkitd ends on its own — matching cider-ger.9's fix direction #3
    /// ("or when cli.Closed fires, or the builder link dies").
    /// </summary>
    private async Task MonitorAsync()
    {
        try
        {
            await Task.WhenAny(Cli.Closed, _serveTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
        }

        Volatile.Write(ref _refCount, 0);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await Closed.ConfigureAwait(false);
            return;
        }

        _owner.OnHandleClosed(Cli.Id, this);

        // Abort the Kestrel-side tunnel connection first and wait for it to actually let go of
        // _bytesStream -- only then is it safe to complete/drain the underlying Control/Session call
        // ourselves without racing Kestrel's own concurrent reads/writes on the same reader/writer.
        try
        {
            await _tunnelCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _serveTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
        }

        // Completes the Control/Session request stream and drains whatever is left of the response
        // side (BytesMessageStream.DisposeAsyncCore).
        await _bytesStream.DisposeAsync().ConfigureAwait(false);
        _call.Dispose();
        _tunnelCts.Dispose();

        foreach (var result in _produced)
        {
            if (!result.IsOwned)
            {
                TryDelete(result.TarPath);
            }
        }

        _logger.LogDebug("detached session bridge {SessionId}", Cli.Id);
        _closedTcs.TrySetResult();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "could not delete unclaimed export tar {TarPath}", path);
        }
    }
}
