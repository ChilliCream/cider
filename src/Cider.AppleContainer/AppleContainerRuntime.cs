using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Cider.AppleContainer.Process;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cider.AppleContainer;

/// <summary>
/// <see cref="IContainerRuntime"/> on top of the Apple <c>container</c> CLI (1.2.x).
/// Everything the CLI reports is parsed from JSON; failures are classified from stderr text
/// because the CLI always exits with 1 (docs/apple-container-notes.md §12).
/// </summary>
public sealed partial class AppleContainerRuntime : IContainerRuntime
{
    /// <summary>How long after a container's <c>start</c> its <c>exec</c> is allowed to race the VM
    /// coming up: <c>container exec</c> can reject with "is not running" for a couple of seconds
    /// after <c>container start -a</c> already holds the init process (notes §12).</summary>
    private static readonly TimeSpan ExecRaceWindow = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="ExecAsync"/> waits to see whether a freshly launched exec fails
    /// immediately, before deciding it is genuinely running and handing it to the caller.</summary>
    private static readonly TimeSpan ExecEarlyExitProbe = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan ExecNotRunningBackoff = TimeSpan.FromMilliseconds(300);
    private const int ExecNotRunningMaxAttempts = 10;

    private readonly AppleContainerOptions _options;
    private readonly ILogger _logger;
    private readonly ContainerCli _cli;
    private readonly ProcessLauncher _launcher;

    /// <summary>Remembers whether a container was created with <c>-t</c>, to avoid an inspect on start.</summary>
    private readonly ConcurrentDictionary<string, bool> _ttyByContainer = new(StringComparer.Ordinal);

    /// <summary>When each container was last started; drives the exec race-retry window above.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _startedAt = new(StringComparer.Ordinal);

    public AppleContainerRuntime(AppleContainerOptions options, ILogger<AppleContainerRuntime> logger)
        : this(options, logger, cli: null)
    {
    }

    /// <summary>
    /// Test seam: drives the adapter through a scripted <see cref="ContainerCli"/> instead of the
    /// real <c>container</c> binary, so logic that lives in the adapter itself — the pull progress
    /// buffering above all — is testable. <paramref name="cli"/> <c>null</c> means the production one.
    /// </summary>
    internal AppleContainerRuntime(AppleContainerOptions options, ILogger<AppleContainerRuntime>? logger, ContainerCli? cli)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger ?? NullLogger<AppleContainerRuntime>.Instance;
        _cli = cli ?? new ContainerCli(options, _logger);
        _launcher = new ProcessLauncher(_cli, _logger);
    }

    [GeneratedRegex(@"(?<version>\d+\.\d+(\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"vmlinux-(?<kernel>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex KernelRegex();

    // ---- system -----------------------------------------------------------

    public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var status = await TryReadStatusAsync(ct);

        var version = "";
        var versionResult = await _cli.RunAsync(["--version"], ct, TimeSpan.FromSeconds(30));
        if (versionResult.Succeeded)
        {
            var match = VersionRegex().Match(versionResult.Stdout);
            version = match.Success ? match.Groups["version"].Value : versionResult.Stdout.Trim();
        }
        else if (status?.ApiServerVersion is { Length: > 0 } apiVersion)
        {
            var match = VersionRegex().Match(apiVersion);
            version = match.Success ? match.Groups["version"].Value : apiVersion;
        }

        return new RuntimeInfo
        {
            Name = "apple-container",
            Version = version,
            KernelVersion = await TryReadKernelVersionAsync(ct),
            Ready = status?.IsRunning ?? false,
            AppRoot = status?.AppRoot,
        };
    });

    public Task EnsureReadyAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        // Before anything else: sweep held `container start -a` children orphaned by a daemon that
        // died without disposing them. Their parent is launchd (ppid 1) by then,
        // so live daemons' children are never touched, and killing the CLI child does not stop its
        // container — reconcile adopts it as running right after this.
        var reaped = new OrphanReaper(_logger, _options.CliPath).ReapOrphanedHeldProcesses();
        if (reaped > 0)
        {
            _logger.LogInformation("startup sweep reaped {Count} orphaned held process(es)", reaped);
        }

        var status = await TryReadStatusAsync(ct);
        if (status?.IsRunning == true)
        {
            return;
        }

        _logger.LogInformation("Apple container services are not running; starting them");
        var result = await _cli.RunAsync(
            ["system", "start", "--enable-kernel-install"],
            ct,
            TimeSpan.FromSeconds(300));

        ContainerCli.ThrowIfFailed(result, "container system start");
    });

    private async Task<AppleSystemStatus?> TryReadStatusAsync(CancellationToken ct)
    {
        var result = await _cli.RunAsync(["system", "status", "--format", "json"], ct, TimeSpan.FromSeconds(60));
        if (!result.Succeeded)
        {
            return null;
        }

        return ContainerCli.ParseJson<AppleSystemStatus>(result.Stdout, "container system status");
    }

    private async Task<string?> TryReadKernelVersionAsync(CancellationToken ct)
    {
        var result = await _cli.RunAsync(["system", "property", "list"], ct, TimeSpan.FromSeconds(30));
        if (!result.Succeeded)
        {
            return null;
        }

        var match = KernelRegex().Match(result.Stdout);
        return match.Success ? match.Groups["kernel"].Value : null;
    }

    // ---- containers -------------------------------------------------------

    public Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Hostname is { Length: > 0 } hostname &&
            !string.Equals(hostname, spec.RuntimeId, StringComparison.Ordinal))
        {
            // Apple's CLI derives the guest hostname from the container id; there is no --hostname flag.
            _logger.LogDebug("ignoring hostname '{Hostname}': not settable through the container CLI", hostname);
        }

        var args = ArgBuilder.Create(spec);
        var result = await _cli.RunAsync(args, ct);
        ContainerCli.ThrowIfFailed(result, $"create {spec.RuntimeId}");
        _ttyByContainer[spec.RuntimeId] = spec.Tty;
    });

    public Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrEmpty(runtimeId);
            ArgumentNullException.ThrowIfNull(options);

            var tty = await HasTtyAsync(runtimeId, ct);
            var attachStdin = options.AttachStdin || tty;
            var args = ArgBuilder.Start(runtimeId, attachStdin);

            Func<string, CancellationToken, Task> signal = (name, token) =>
                KillContainerAsync(runtimeId, name, token);

            var process = tty
                ? (IContainerProcess)_launcher.StartPty(args, 0, 0, signal)
                : _launcher.StartPipe(args, options.AttachStdin, signal);

            // `container exec` can still reject with "is not running" for a couple of seconds even
            // though the init process is already held above; ExecAsync uses this timestamp to know
            // when it is worth retrying that specific race instead of surfacing it immediately.
            _startedAt[runtimeId] = DateTimeOffset.UtcNow;
            return process;
        });

    public Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrEmpty(runtimeId);

            var args = new List<string> { "stop" };
            if (timeoutSeconds is >= 0)
            {
                args.Add("-t");
                args.Add(timeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(signal))
            {
                args.Add("-s");
                args.Add(ArgBuilder.NormalizeSignal(signal));
            }

            args.Add(runtimeId);

            // Apple waits out the full grace period when PID 1 ignores the signal (notes §4).
            var budget = _options.CommandTimeout;
            if (timeoutSeconds is > 0)
            {
                budget = TimeSpan.FromSeconds(timeoutSeconds.Value) + TimeSpan.FromSeconds(30);
            }

            var result = await _cli.RunAsync(args, ct, budget);
            ContainerCli.ThrowIfFailed(result, $"stop {runtimeId}");
        });

    public Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        var result = await _cli.RunAsync(
            ["kill", "-s", ArgBuilder.NormalizeSignal(signal), runtimeId],
            ct);

        ContainerCli.ThrowIfFailed(result, $"kill {runtimeId}");
    });

    public Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        var args = new List<string> { "delete" };
        if (force)
        {
            args.Add("-f");
        }

        args.Add(runtimeId);

        var result = await _cli.RunAsync(args, ct);
        ContainerCli.ThrowIfFailed(result, $"delete {runtimeId}");
        _ttyByContainer.TryRemove(runtimeId, out _);
        _startedAt.TryRemove(runtimeId, out _);
    });

    public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var containers = await _cli.RunJsonAsync<List<AppleContainerJson>>(
            ["ls", "-a", "--format", "json"],
            ct);

        if (containers is null)
        {
            return (IReadOnlyList<RuntimeContainer>)Array.Empty<RuntimeContainer>();
        }

        var mapped = new List<RuntimeContainer>(containers.Count);
        foreach (var container in containers)
        {
            mapped.Add(RuntimeMapper.ToContainer(container));
        }

        return mapped;
    });

    public Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrEmpty(runtimeId);

            var json = await InspectRawAsync(runtimeId, ct);
            return json is null ? null : RuntimeMapper.ToContainer(json);
        });

    public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrEmpty(runtimeId);
            ArgumentNullException.ThrowIfNull(spec);

            if (spec.Privileged)
            {
                _logger.LogDebug("ignoring privileged exec: the container CLI has no such flag");
            }

            var args = ArgBuilder.Exec(runtimeId, spec);

            IContainerProcess Launch() => spec.Tty
                ? (IContainerProcess)_launcher.StartPty(args, 0, 0, signalDelegate: null)
                : _launcher.StartPipe(args, spec.OpenStdin, signalDelegate: null);

            // Outside the race window right after start, behave exactly as before: launch and hand
            // the process straight to the caller, who discovers any failure through Exited/Stderr
            // (there is no way to detect it up front without consuming the streams it owns).
            if (!_startedAt.TryGetValue(runtimeId, out var startedAt) ||
                DateTimeOffset.UtcNow - startedAt >= ExecRaceWindow)
            {
                return Launch();
            }

            for (var attempt = 1; ; attempt++)
            {
                var process = Launch();

                // A real, still-running exec (an interactive shell, or even a fast `echo`) either
                // keeps running past this probe or exits on its own; only an immediate CLI-side
                // rejection is worth inspecting further, so this never adds latency to the happy path
                // beyond the probe window itself, and only within the first few seconds after start.
                var winner = await Task.WhenAny(process.Exited, Task.Delay(ExecEarlyExitProbe, ct));
                if (winner != process.Exited)
                {
                    return process;
                }

                var exitCode = await process.Exited;
                if (exitCode == 0)
                {
                    return process;
                }

                var (stdout, stderr, message) = await DrainAsync(process, ct);
                await process.DisposeAsync();

                if (!CliErrorMapper.IsContainerNotRunning(message))
                {
                    // A genuine command failure, unrelated to the startup race: replay what was
                    // already read so the caller still sees it through Exited/Stdout/Stderr.
                    return new CompletedExecProcess(process.Pid, process.HasTty, exitCode, stdout, stderr);
                }

                if (attempt >= ExecNotRunningMaxAttempts)
                {
                    // Still a 409 with the same text for the client, but ExecManager's own retry
                    // recognises it by RuntimeErrorReason, not by these words.
                    throw RuntimeException.ContainerNotRunning($"container {runtimeId} is not running");
                }

                _logger.LogDebug(
                    "exec on container {Container} raced the container start (attempt {Attempt}/{Max}): {Message}",
                    runtimeId, attempt, ExecNotRunningMaxAttempts, message.Trim());
                await Task.Delay(ExecNotRunningBackoff, ct);
            }
        });

    /// <summary>Reads a just-exited exec process's streams to classify the failure.</summary>
    private static async Task<(byte[] Stdout, byte[]? Stderr, string Message)> DrainAsync(IContainerProcess process, CancellationToken ct)
    {
        var stdoutBuffer = new MemoryStream();
        try
        {
            await process.Stdout.CopyToAsync(stdoutBuffer, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        byte[]? stderrBytes = null;
        if (process.Stderr is { } stderr)
        {
            var stderrBuffer = new MemoryStream();
            try
            {
                await stderr.CopyToAsync(stderrBuffer, ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            stderrBytes = stderrBuffer.ToArray();
        }

        var stdoutBytes = stdoutBuffer.ToArray();
        var message = stderrBytes is { Length: > 0 }
            ? Encoding.UTF8.GetString(stderrBytes)
            : Encoding.UTF8.GetString(stdoutBytes);

        return (stdoutBytes, stderrBytes, message);
    }

    /// <summary>An already-exited exec process whose (small, already fully drained) output was
    /// buffered while classifying its failure; replays it so the caller's normal Exited/Stdout/Stderr
    /// contract still holds even though <see cref="ExecAsync"/> had to peek first.</summary>
    private sealed class CompletedExecProcess : IContainerProcess
    {
        private readonly MemoryStream _stdout;
        private readonly MemoryStream? _stderr;

        public CompletedExecProcess(int? pid, bool hasTty, int exitCode, byte[] stdout, byte[]? stderr)
        {
            Pid = pid;
            HasTty = hasTty;
            _stdout = new MemoryStream(stdout, writable: false);
            _stderr = stderr is null ? null : new MemoryStream(stderr, writable: false);
            Exited = Task.FromResult(exitCode);
        }

        public int? Pid { get; }
        public bool HasTty { get; }
        public Stream? Stdin => null;
        public Stream Stdout => _stdout;
        public Stream? Stderr => _stderr;
        public Task<int> Exited { get; }
        public Task CloseStdinAsync() => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct) => Task.CompletedTask;
        public Task KillAsync(string signal, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct) =>
        GuardAsync(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(runtimeId);

            var args = new List<string> { "logs" };
            if (follow)
            {
                args.Add("-f");
            }

            if (tail is > 0)
            {
                args.Add("-n");
                args.Add(tail.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            args.Add(runtimeId);

            // `container logs -f` never ends on its own (notes §3): the stream kills the child on dispose.
            return Task.FromResult<Stream>(_launcher.StartStreaming(args));
        });

    public Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        var result = await _cli.RunAsync(
            ["stats", "--format", "json", "--no-stream", runtimeId],
            ct,
            TimeSpan.FromSeconds(60));

        if (!result.Succeeded)
        {
            var kind = CliErrorMapper.Classify(result.Stderr);
            if (kind is RuntimeErrorKind.NotFound or RuntimeErrorKind.Conflict)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, $"stats {runtimeId}");
        }

        var stats = ContainerCli.ParseJson<List<AppleStats>>(result.Stdout, "container stats");
        if (stats is not { Count: > 0 })
        {
            return null;
        }

        return RuntimeMapper.ToStats(stats[0], DateTimeOffset.UtcNow);
    });

    /// <summary>
    /// <c>container cp &lt;container&gt;:&lt;path&gt; &lt;dest&gt;</c>, bounded two ways
    /// (see <see cref="AppleContainerOptions.CopyTimeout"/> and
    /// <see cref="AppleContainerOptions.CopyIdleGrace"/> for why): a generous overall ceiling that
    /// never fires on an ordinary copy, plus a much tighter start-of-transfer idle check that turns
    /// the confirmed "nonexistent source path hangs forever" CLI bug into a fast daemon-authored
    /// error instead of a five-minute (or half-hour) stall. The idle check disarms itself for good
    /// the moment anything shows up at <paramref name="localDestinationDir"/>, so a copy that is
    /// genuinely under way — however large — is never killed by it.
    /// </summary>
    public Task CopyFromContainerAsync(
        string runtimeId,
        string containerPath,
        string localDestinationDir,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        ArgumentException.ThrowIfNullOrEmpty(localDestinationDir);

        Directory.CreateDirectory(localDestinationDir);
        var destination = localDestinationDir.EndsWith('/') ? localDestinationDir : localDestinationDir + "/";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cliTask = _cli.RunAsync(["cp", $"{runtimeId}:{containerPath}", destination], linkedCts.Token, _options.CopyTimeout);
        var idledOut = await WatchForCopyArrivalAsync(destination, cliTask, linkedCts, ct);

        CliResult result;
        try
        {
            result = await cliTask;
        }
        catch (OperationCanceledException) when (idledOut && !ct.IsCancellationRequested)
        {
            throw RuntimeException.Timeout(
                $"cider: the Apple container runtime produced nothing for 'cp {runtimeId}:{containerPath}' " +
                $"within {_options.CopyIdleGrace.TotalSeconds:0.#}s of starting; the source path most likely " +
                "does not exist in the container, or the runtime's copy path is wedged for it. See " +
                "Troubleshooting in the cider README to recover it.");
        }

        ContainerCli.ThrowIfFailed(result, $"cp from {runtimeId}");
    });

    /// <summary>
    /// <c>container cp &lt;src&gt; &lt;container&gt;:&lt;path&gt;</c>, bounded by
    /// <see cref="AppleContainerOptions.CopyTimeout"/> alone: unlike
    /// <see cref="CopyFromContainerAsync"/>, the source here is always a path the daemon itself
    /// staged on disk (<c>ContainerManager.Archive.cs</c>), never one a client supplies directly, so
    /// it cannot be the missing-path shape this ticket is about, and there is no local growth on the
    /// container side for an idle check to key off. The generous ceiling alone still turns a
    /// genuinely wedged runtime into a daemon-authored error rather than an unbounded hang.
    /// </summary>
    public Task CopyToContainerAsync(
        string runtimeId,
        string localSourcePath,
        string containerPath,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentException.ThrowIfNullOrEmpty(localSourcePath);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);

        var result = await _cli.RunAsync(["cp", localSourcePath, $"{runtimeId}:{containerPath}"], ct, _options.CopyTimeout);
        ContainerCli.ThrowIfFailed(result, $"cp to {runtimeId}");
    });

    /// <summary>
    /// Watches <paramref name="destination"/> for the first sign of life while <paramref name="cliTask"/>
    /// runs; cancels <paramref name="linkedCts"/> — and so the CLI invocation <paramref name="cliTask"/>
    /// is bound to — if nothing has appeared by <see cref="AppleContainerOptions.CopyIdleGrace"/>.
    /// Returns whether it did that, which the caller needs to tell its own cancellation apart from the
    /// original caller cancelling <paramref name="callerCt"/> (both surface from
    /// <paramref name="cliTask"/> as a plain <see cref="OperationCanceledException"/>).
    /// </summary>
    private async Task<bool> WatchForCopyArrivalAsync(
        string destination,
        Task cliTask,
        CancellationTokenSource linkedCts,
        CancellationToken callerCt)
    {
        var deadline = DateTime.UtcNow + _options.CopyIdleGrace;
        while (!cliTask.IsCompleted && !callerCt.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (HasArrived(destination))
            {
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), callerCt);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (!cliTask.IsCompleted && !callerCt.IsCancellationRequested && !HasArrived(destination))
        {
            linkedCts.Cancel();
            return true;
        }

        return false;
    }

    /// <summary>Whether anything has shown up in a copy destination yet, tolerating the directory
    /// vanishing or being momentarily unreadable out from under a concurrent writer.</summary>
    private static bool HasArrived(string destination)
    {
        try
        {
            return Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentNullException.ThrowIfNull(tarOutput);

        var tmp = NewTempFile("export", ".tar");
        try
        {
            var result = await _cli.RunAsync(["export", "-o", tmp, runtimeId], ct, _options.PullTimeout);
            ContainerCli.ThrowIfFailed(result, $"export {runtimeId}");

            await using var file = File.OpenRead(tmp);
            await file.CopyToAsync(tarOutput, ct);
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    });

    // ---- helpers ----------------------------------------------------------

    private async Task<AppleContainerJson?> InspectRawAsync(string runtimeId, CancellationToken ct)
    {
        var result = await _cli.RunAsync(["inspect", runtimeId], ct);
        if (!result.Succeeded)
        {
            if (CliErrorMapper.Classify(result.Stderr) == RuntimeErrorKind.NotFound)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, $"inspect {runtimeId}");
        }

        var containers = ContainerCli.ParseJson<List<AppleContainerJson>>(result.Stdout, "container inspect");
        return containers is { Count: > 0 } ? containers[0] : null;
    }

    private async Task<bool> HasTtyAsync(string runtimeId, CancellationToken ct)
    {
        if (_ttyByContainer.TryGetValue(runtimeId, out var cached))
        {
            return cached;
        }

        var json = await InspectRawAsync(runtimeId, ct)
            ?? throw RuntimeException.NotFound($"container not found: {runtimeId}");

        var tty = json.Configuration?.InitProcess?.Terminal ?? false;
        _ttyByContainer[runtimeId] = tty;
        return tty;
    }

    private string NewTempFile(string prefix, string extension)
    {
        Directory.CreateDirectory(_options.TmpDir);
        return Path.Combine(_options.TmpDir, $"cider-{prefix}-{Guid.NewGuid():N}{extension}");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are harmless.
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (RuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeException(RuntimeErrorKind.Internal, ex.Message, ex);
        }
    }

    private static async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeException(RuntimeErrorKind.Internal, ex.Message, ex);
        }
    }
}
