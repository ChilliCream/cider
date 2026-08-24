using System.Diagnostics;
using System.Text;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// The real <c>docker</c> CLI driven against the in-process daemon (fake engine underneath).
/// Every test is a no-op when <c>docker</c> is not installed.
/// </summary>
public sealed class DockerCliIntegrationTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    private static string? DockerPath => Environment
        .GetEnvironmentVariable("PATH")?
        .Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory, "docker"))
        .FirstOrDefault(File.Exists);

    [Fact]
    public async Task Docker_version_reports_the_daemon()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var result = await cli.RunAsync("version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("29.0.0", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("1.47", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_info_reports_the_apple_container_driver()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var result = await cli.RunAsync("info", "--format", "{{.Driver}} {{.OSType}}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("apple-container linux", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_run_prints_container_output()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var result = await cli.RunAsync("run", "--rm", "alpine", "sh", "-c", "echo hi");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(result.Stdout.Contains("hi", StringComparison.Ordinal), result.ToString());

    }

    [Fact]
    public async Task Docker_run_detached_then_ps_exec_logs_stop_and_rm()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var run = await cli.RunAsync("run", "--name", "t1", "-d", "alpine", "sleep", "30");
        Assert.True(run.ExitCode == 0, run.Stderr);

        var ps = await cli.RunAsync("ps", "--format", "{{.Names}}");
        Assert.Equal(0, ps.ExitCode);
        Assert.Contains("t1", ps.Stdout, StringComparison.Ordinal);

        var exec = await cli.RunAsync("exec", "t1", "sh", "-c", "echo out; echo err 1>&2; exit 3");
        Assert.Equal(3, exec.ExitCode);
        Assert.Contains("out", exec.Stdout, StringComparison.Ordinal);
        Assert.Contains("err", exec.Stderr, StringComparison.Ordinal);

        var stdin = await cli.RunAsync(["exec", "-i", "t1", "cat"], stdin: "abc\n");
        Assert.Equal(0, stdin.ExitCode);
        Assert.Contains("abc", stdin.Stdout, StringComparison.Ordinal);

        var logs = await cli.RunAsync("logs", "t1");
        Assert.Equal(0, logs.ExitCode);

        var stop = await cli.RunAsync("stop", "t1");
        Assert.True(stop.ExitCode == 0, stop.Stderr);

        var remove = await cli.RunAsync("rm", "t1");
        Assert.True(remove.ExitCode == 0, remove.Stderr);
    }

    [Fact]
    public async Task Docker_pull_prints_a_status_line()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var result = await cli.RunAsync("pull", "busybox");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("busybox", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Status:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docker_network_create_list_and_remove()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var create = await cli.RunAsync("network", "create", "n1");
        Assert.True(create.ExitCode == 0, create.Stderr);

        var list = await cli.RunAsync("network", "ls", "--format", "{{.Name}}");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("n1", list.Stdout, StringComparison.Ordinal);
        Assert.Contains("bridge", list.Stdout, StringComparison.Ordinal);

        var remove = await cli.RunAsync("network", "rm", "n1");
        Assert.True(remove.ExitCode == 0, remove.Stderr);
    }

    [Fact]
    public async Task Docker_volume_create_list_and_remove()
    {
        if (DockerPath is null)
        {
            return;
        }

        await using var cli = await DockerCli.StartAsync();

        var create = await cli.RunAsync("volume", "create", "v1");
        Assert.True(create.ExitCode == 0, create.Stderr);

        var list = await cli.RunAsync("volume", "ls", "--format", "{{.Name}}");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("v1", list.Stdout, StringComparison.Ordinal);

        var remove = await cli.RunAsync("volume", "rm", "v1");
        Assert.True(remove.ExitCode == 0, remove.Stderr);
    }

    /// <summary>A daemon plus an isolated <c>docker</c> CLI environment pointed at it.</summary>
    private sealed class DockerCli : IAsyncDisposable
    {
        private readonly DaemonTestHost _host;
        private readonly string _home;

        private DockerCli(DaemonTestHost host, string home)
        {
            _host = host;
            _home = home;
        }

        public static async Task<DockerCli> StartAsync()
        {
            var host = await DaemonTestHost.StartAsync();
            var home = Path.Combine(Path.GetTempPath(), "ad-docker-home", Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(Path.Combine(home, ".docker"));
            return new DockerCli(host, home);
        }

        public Task<CommandResult> RunAsync(params string[] args) => RunAsync(args, null);

        public async Task<CommandResult> RunAsync(string[] args, string? stdin)
        {
            var info = new ProcessStartInfo(DockerPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["DOCKER_HOST"] = "unix://" + _host.SocketPath;
            info.Environment["HOME"] = _home;
            info.Environment["DOCKER_CONFIG"] = Path.Combine(_home, ".docker");
            info.Environment.Remove("DOCKER_CONTEXT");
            info.Environment["DOCKER_BUILDKIT"] = "0";

            using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start docker");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
            }

            process.StandardInput.Close();

            using var cts = new CancellationTokenSource(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException($"docker {string.Join(' ', args)} did not finish in {CommandTimeout}");
            }

            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();

            try
            {
                if (Directory.Exists(_home))
                {
                    Directory.Delete(_home, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() =>
            new StringBuilder().Append("exit ").Append(ExitCode).Append('\n').Append(Stdout).Append(Stderr).ToString();
    }
}
