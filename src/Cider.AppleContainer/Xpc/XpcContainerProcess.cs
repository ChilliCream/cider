using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// A container's init process started over XPC: <c>containerBootstrap</c> +
/// <c>containerStartProcess</c> (task cider-ede.7). Unlike <c>Process.CliProcess</c> this holds no
/// child process at all — stdio is a set of daemon-owned pipe streams the caller already opened, and
/// <see cref="Exited"/>/<see cref="ResizeAsync"/>/<see cref="KillAsync"/> are plain XPC calls, injected
/// as delegates rather than a direct <see cref="XpcClient"/> dependency so this class stays testable
/// against a fake transport (the file scope's own <c>XpcContainerProcessTests</c>) without a live
/// <c>com.apple.container.apiserver</c> connection. <see cref="Pid"/> is always <c>null</c>: there is
/// no host-side process to report a pid for.
/// </summary>
internal sealed class XpcContainerProcess : IContainerProcess
{
    private readonly Func<CancellationToken, Task<(int ExitCode, DateTimeOffset ExitedAt)?>> _waitAsync;
    private readonly Func<int, int, CancellationToken, Task> _resizeAsync;
    private readonly Func<string, CancellationToken, Task> _killAsync;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private Stream? _stdin;
    private volatile bool _stdinClosed;
    private volatile bool _disposed;

    public XpcContainerProcess(
        bool hasTty,
        Stream? stdin,
        Stream stdout,
        Stream? stderr,
        Func<CancellationToken, Task<(int ExitCode, DateTimeOffset ExitedAt)?>> waitAsync,
        Func<int, int, CancellationToken, Task> resizeAsync,
        Func<string, CancellationToken, Task> killAsync,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(waitAsync);
        ArgumentNullException.ThrowIfNull(resizeAsync);
        ArgumentNullException.ThrowIfNull(killAsync);
        ArgumentNullException.ThrowIfNull(logger);

        HasTty = hasTty;
        _stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
        _waitAsync = waitAsync;
        _resizeAsync = resizeAsync;
        _killAsync = killAsync;
        _logger = logger;

        // Issued immediately, on whatever dedicated connection/thread waitAsync itself uses
        // (XpcContainerRuntime.WaitContainerAsync's XpcCallOptions.LongRunning) — task fix direction
        // §1: "Exited = containerWait issued immediately ... (no timeout)". Never throws past this
        // boundary: IContainerProcess.Exited's own contract is "Never throws; -1 when unknown".
        Exited = RunWaitAsync();
    }

    public int? Pid => null;

    public bool HasTty { get; }

    public Stream? Stdin => _stdinClosed ? null : _stdin;

    public Stream Stdout { get; }

    public Stream? Stderr { get; }

    public Task<int> Exited { get; }

    private async Task<int> RunWaitAsync()
    {
        try
        {
            var result = await _waitAsync(CancellationToken.None).ConfigureAwait(false);
            return result?.ExitCode ?? -1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "containerWait failed; exit code will be reported as unknown");
            return -1;
        }
    }

    /// <summary>
    /// Docker's <c>CloseWrite</c>: a real half-close on the underlying pipe, so the guest reads EOF
    /// on its stdin while stdout/stderr keep streaming.
    /// </summary>
    public Task CloseStdinAsync()
    {
        lock (_gate)
        {
            if (_stdinClosed)
            {
                return Task.CompletedTask;
            }

            _stdinClosed = true;
        }

        try
        {
            _stdin?.Flush();
            _stdin?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The guest already went away.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>containerResize{id, processIdentifier, width, height}</c>. No-op once the process has
    /// already exited or this instance was disposed. Never throws — the endpoint contract for a
    /// resize is 200 regardless (docs/ARCHITECTURE.md §3), matching <c>CliProcess.ResizeAsync</c>.
    /// </summary>
    public Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        if (!HasTty || _disposed || Exited.IsCompleted)
        {
            return Task.CompletedTask;
        }

        return _resizeAsync(cols, rows, ct);
    }

    /// <summary>
    /// <c>containerKill{id, processIdentifier, signal}</c> — best-effort, exactly like
    /// <c>CliProcess.KillAsync</c>'s own contract ("Best-effort signal delivery to the process"):
    /// a failure is logged at Debug and swallowed, never thrown.
    /// </summary>
    public async Task KillAsync(string signal, CancellationToken ct)
    {
        if (_disposed || Exited.IsCompleted)
        {
            return;
        }

        try
        {
            await _killAsync(signal, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "signalling the container with {Signal} failed", signal);
        }
    }

    /// <summary>Closes the stdio streams only — never sends a signal, never stops or deletes the
    /// container (task fix direction §1: "Dispose closes fds; never kills the container"). The
    /// container keeps running on the apiserver's own VM regardless of whether anything is still
    /// attached to its stdio.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        DisposeQuietly(_stdin);
        DisposeQuietly(Stdout);
        DisposeQuietly(Stderr);

        return ValueTask.CompletedTask;
    }

    private static void DisposeQuietly(Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing to do.
        }
    }
}
