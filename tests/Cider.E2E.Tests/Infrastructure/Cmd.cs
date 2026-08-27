using System.Diagnostics;
using System.Text;

namespace Cider.E2E.Tests.Infrastructure;

/// <summary>The outcome of one child process: exit code plus the two streams, kept apart.</summary>
public sealed record CommandResult(string File, string Arguments, int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    /// <summary>True when the process exited with 0 and was not killed by the timeout.</summary>
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>A dump usable as an xunit assertion message.</summary>
    public override string ToString() =>
        $"$ {File} {Arguments}\n  exit={ExitCode}{(TimedOut ? " (TIMED OUT)" : "")}\n  --- stdout ---\n{Indent(Stdout)}\n  --- stderr ---\n{Indent(Stderr)}";

    private static string Indent(string text) =>
        string.IsNullOrEmpty(text) ? "  (empty)" : string.Join('\n', text.Split('\n').Select(line => "  " + line));
}

/// <summary>Runs child processes with an explicit environment, separate stdout/stderr and a hard timeout.</summary>
public static class Cmd
{
    /// <summary>Default budget for one command; Apple container boots a VM per container.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Runs <paramref name="file"/> and captures both streams; never throws on a non-zero exit.</summary>
    public static async Task<CommandResult> RunAsync(
        string file,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? stdin = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null)
    {
        var argv = arguments.ToArray();
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in argv)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = info };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        var timedOut = false;
        using (var cts = new CancellationTokenSource(timeout ?? DefaultTimeout))
        {
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                Kill(process);
                try
                {
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        var outText = await SafeAsync(stdout);
        var errText = await SafeAsync(stderr);
        var exitCode = timedOut ? -1 : process.ExitCode;

        return new CommandResult(file, string.Join(' ', argv), exitCode, outText, errText, timedOut);
    }

    /// <summary>Starts a long-running child (e.g. <c>docker events</c>) whose output is drained in the background.</summary>
    public static BackgroundProcess Start(
        string file,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var argv = arguments.ToArray();
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in argv)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        return new BackgroundProcess(new Process { StartInfo = info });
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            return "";
        }
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }
}

/// <summary>A child process whose output accumulates while the test does something else.</summary>
public sealed class BackgroundProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _pumps;

    internal BackgroundProcess(Process process)
    {
        _process = process;
        _process.Start();
        _pumps = Task.WhenAll(
            PumpAsync(_process.StandardOutput, _stdout),
            PumpAsync(_process.StandardError, _stderr));
    }

    /// <summary>The child's OS process id (cider-ede.41: lets the cross-process race harness prove
    /// its two daemons really are distinct processes, distinct from the test process itself).</summary>
    public int Pid => _process.Id;

    /// <summary>Whether the child has exited (cider-ede.41: a spawned daemon dying mid-race must be
    /// detected and reported rather than silently turning the rest of the run into no-ops).</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Everything written to stdout so far.</summary>
    public string Stdout
    {
        get
        {
            lock (_stdout)
            {
                return _stdout.ToString();
            }
        }
    }

    /// <summary>Everything written to stderr so far.</summary>
    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    private async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read <= 0)
                {
                    return;
                }

                lock (sink)
                {
                    sink.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Cmd.Kill(_process);
        try
        {
            await _pumps.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
        }

        _process.Dispose();
    }
}
