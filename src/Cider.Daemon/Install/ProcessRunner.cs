using System.Diagnostics;
using System.Text;

namespace Cider.Daemon.Install;

/// <summary>
/// Small wrapper around <see cref="Process"/> for running short-lived CLI commands
/// (launchctl, docker, sudo, id) and capturing their output. Never throws for a
/// nonzero exit code or missing executable; callers inspect <see cref="Result"/>.
/// </summary>
internal static class ProcessRunner
{
    internal readonly record struct Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdOut.Append(e.Data).Append('\n');
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdErr.Append(e.Data).Append('\n');
            }
        };

        try
        {
            if (!process.Start())
            {
                return new Result(-1, string.Empty, $"Failed to start '{fileName}'.", TimedOut: false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return new Result(-1, string.Empty, ex.Message, TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        if (timeout is { } t)
        {
            timeoutCts.CancelAfter(t);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        if (timedOut)
        {
            return new Result(-1, stdOut.ToString(), stdErr.ToString(), TimedOut: true);
        }

        return new Result(process.ExitCode, stdOut.ToString(), stdErr.ToString(), TimedOut: false);
    }

    /// <summary>True if <paramref name="exeName"/> resolves to an executable file on PATH.</summary>
    public static bool ExistsOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort; the process may have exited concurrently.
        }
    }
}
