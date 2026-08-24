using System.Diagnostics;
using System.Text;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using SysProcess = System.Diagnostics.Process;

namespace Cider.AppleContainer.Cli;

/// <summary>
/// Runs the Apple <c>container</c> CLI through <see cref="SysProcess"/> (never a shell),
/// captures UTF-8 stdout/stderr and enforces a timeout.
/// </summary>
/// <remarks>
/// The three verbs that actually spawn a process are <c>virtual</c> so tests can substitute a
/// scripted CLI (see <see cref="AppleContainerRuntime"/>'s internal constructor) and drive adapter
/// logic — pull progress buffering above all — without a real <c>container</c> binary.
/// </remarks>
internal class ContainerCli
{
    private readonly AppleContainerOptions _options;
    private readonly ILogger _logger;

    public ContainerCli(AppleContainerOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public AppleContainerOptions Options => _options;

    public string CliPath => _options.CliPath;

    /// <summary>Runs <c>container &lt;args&gt;</c> to completion and returns its captured output.</summary>
    public virtual async Task<CliResult> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string? stdin = null)
    {
        var effectiveTimeout = timeout ?? _options.CommandTimeout;
        var startInfo = CreateStartInfo(args);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        _logger.LogDebug("container CLI: {Cli} {Args}", _options.CliPath, string.Join(' ', args));

        using var process = new SysProcess { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RuntimeException(
                RuntimeErrorKind.Unavailable,
                $"cannot run '{_options.CliPath}': {ex.Message}",
                ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (effectiveTimeout > TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(effectiveTimeout);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), timeoutCts.Token);
            }

            process.StandardInput.Close();

            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var result = new CliResult(process.ExitCode, stdout, stderr);
            if (!result.Succeeded)
            {
                _logger.LogDebug(
                    "container CLI exit {Exit}: {Args} :: {Stderr}",
                    result.ExitCode,
                    string.Join(' ', args),
                    result.Stderr.Trim());
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(process);
            throw TimedOut(args, effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }
    }

    /// <summary>
    /// Runs the command and reports every stdout/stderr line as it arrives (pull/push/build).
    /// The returned result carries the exit code and the tail of stderr for error reporting.
    /// </summary>
    public virtual async Task<CliResult> RunStreamingAsync(
        IReadOnlyList<string> args,
        Action<string, bool> onLine,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? _options.PullTimeout;
        var startInfo = CreateStartInfo(args);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        _logger.LogDebug("container CLI (streaming): {Cli} {Args}", _options.CliPath, string.Join(' ', args));

        using var process = new SysProcess { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RuntimeException(
                RuntimeErrorKind.Unavailable,
                $"cannot run '{_options.CliPath}': {ex.Message}",
                ex);
        }

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (effectiveTimeout > TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(effectiveTimeout);
        }

        var stderrTail = new List<string>();
        var gate = new object();

        async Task PumpAsync(StreamReader reader, bool isStderr)
        {
            while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (isStderr)
                {
                    lock (gate)
                    {
                        stderrTail.Add(line);
                        if (stderrTail.Count > 100)
                        {
                            stderrTail.RemoveAt(0);
                        }
                    }
                }

                lock (gate)
                {
                    onLine(line, isStderr);
                }
            }
        }

        var stdoutPump = PumpAsync(process.StandardOutput, isStderr: false);
        var stderrPump = PumpAsync(process.StandardError, isStderr: true);

        try
        {
            await Task.WhenAll(stdoutPump, stderrPump);
            await process.WaitForExitAsync(timeoutCts.Token);
            return new CliResult(process.ExitCode, string.Empty, string.Join('\n', stderrTail));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(process);
            throw TimedOut(args, effectiveTimeout);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }
    }

    /// <summary>Runs the command, throws on failure, and deserializes stdout as <typeparamref name="T"/>.</summary>
    public virtual async Task<T?> RunJsonAsync<T>(
        IReadOnlyList<string> args,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var result = await RunAsync(args, ct, timeout);
        ThrowIfFailed(result, string.Join(' ', args));
        return ParseJson<T>(result.Stdout, string.Join(' ', args));
    }

    /// <summary>Deserializes CLI JSON, wrapping malformed output as an internal error.</summary>
    public static T? ParseJson<T>(string json, string context)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return AppleJson.Deserialize<T>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new RuntimeException(
                RuntimeErrorKind.Internal,
                $"could not parse output of '{context}': {ex.Message}",
                ex);
        }
    }

    /// <summary>Throws a classified <see cref="RuntimeException"/> when the invocation failed.</summary>
    public static void ThrowIfFailed(CliResult result, string context)
    {
        if (!result.Succeeded)
        {
            throw CliErrorMapper.ToException(result, context);
        }
    }

    /// <summary>
    /// The error a call that outlived its budget reports. The runtime — not the CLI string the
    /// daemon happened to run — is named as the cause, because that is the only actionable part
    /// for whoever is looking at `Error response from daemon: …`, and it carries the recovery
    /// pointer (README Troubleshooting). <see cref="RuntimeErrorKind.Timeout"/>
    /// is answered as a 500, not the 503 that invites a retry into the same stall.
    /// </summary>
    private static RuntimeException TimedOut(IReadOnlyList<string> args, TimeSpan timeout) =>
        RuntimeException.Timeout(
            $"cider: the Apple container runtime did not answer '{DescribeOperation(args)}' " +
            $"within {timeout.TotalSeconds:0.#}s. The runtime is most likely wedged; " +
            "see Troubleshooting in the cider README to recover it.");

    /// <summary>The verb of an invocation — <c>network create</c> — without its flags and values.</summary>
    private static string DescribeOperation(IReadOnlyList<string> args)
    {
        var verbs = new List<string>(2);
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                break;
            }

            verbs.Add(arg);
            if (verbs.Count == 2)
            {
                break;
            }
        }

        return verbs.Count > 0 ? string.Join(' ', verbs) : "container";
    }

    /// <summary>Builds a <see cref="ProcessStartInfo"/> for <c>container &lt;args&gt;</c> without shell interpretation.</summary>
    /// <remarks>
    /// <c>virtual</c> so a test can point the same invocation at a stand-in process — a CLI that
    /// hangs, above all — and exercise the real timeout machinery in <see cref="RunAsync"/> rather
    /// than a re-implementation of it.
    /// </remarks>
    public virtual ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(_options.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    internal static void KillQuietly(SysProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process is already gone; nothing to clean up.
        }
    }
}
