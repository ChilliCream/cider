using System.Text;
using Cider.AppleContainer;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>Runs only with <c>CIDER_E2E=1</c>; everything else in the suite is CLI-free.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "set CIDER_E2E=1 to exercise the real Apple container CLI";
        }
    }
}

/// <summary>End-to-end checks against the real <c>container</c> CLI on this machine.</summary>
[Collection("apple-container-e2e")]
public class AppleContainerRuntimeE2ETests
{
    private const string Image = "alpine:3.22";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    private static AppleContainerRuntime NewRuntime() =>
        new(
            new AppleContainerOptions { CliPath = ResolveCliPath() },
            NullLogger<AppleContainerRuntime>.Instance);

    private static string ResolveCliPath()
    {
        var configured = Environment.GetEnvironmentVariable("CIDER_CONTAINER_CLI");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        return File.Exists("/usr/local/bin/container") ? "/usr/local/bin/container" : "container";
    }

    private static string NewName(string suffix) => $"adtest-{Guid.NewGuid():N}"[..14] + "-" + suffix;

    [E2EFact]
    public async Task Start_attached_separates_streams_and_propagates_the_exit_code()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var runtime = NewRuntime();

        await runtime.EnsureReadyAsync(ct);
        var info = await runtime.GetInfoAsync(ct);
        Assert.True(info.Ready);
        Assert.StartsWith("1.", info.Version, StringComparison.Ordinal);

        var name = NewName("exit");
        await runtime.CreateContainerAsync(
            new ContainerSpec
            {
                RuntimeId = name,
                Image = Image,
                Args = ["sh", "-c", "echo out; echo err 1>&2; exit 3"],
                Labels = new Dictionary<string, string> { ["com.chillicream.cider.test"] = "1" },
            },
            ct);

        try
        {
            await using var process = await runtime.StartContainerAsync(name, new StartOptions(), ct);

            Assert.False(process.HasTty);
            Assert.NotNull(process.Stderr);

            var stdout = new StreamReader(process.Stdout).ReadToEndAsync(ct);
            var stderr = new StreamReader(process.Stderr!).ReadToEndAsync(ct);
            await Task.WhenAll(stdout, stderr);

            Assert.Equal("out", (await stdout).Trim());
            Assert.Contains("err", await stderr, StringComparison.Ordinal);
            Assert.Equal(3, await process.Exited);
        }
        finally
        {
            await runtime.RemoveContainerAsync(name, force: true, CancellationToken.None);
        }
    }

    [E2EFact]
    public async Task Exec_pty_and_inspect_work_against_a_running_container()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var runtime = NewRuntime();

        await runtime.EnsureReadyAsync(ct);

        var name = NewName("live");
        await runtime.CreateContainerAsync(
            new ContainerSpec
            {
                RuntimeId = name,
                Image = Image,
                Args = ["sleep", "300"],
                Env = ["E2E=yes"],
                WorkingDir = "/tmp",
                Labels = new Dictionary<string, string> { ["com.chillicream.cider.test"] = "1" },
            },
            ct);

        IContainerProcess? held = null;
        try
        {
            held = await runtime.StartContainerAsync(name, new StartOptions(), ct);
            await WaitForRunningAsync(runtime, name, ct);

            // ---- inspect + list mapping ----
            var inspected = await runtime.InspectContainerAsync(name, ct);
            Assert.NotNull(inspected);
            Assert.Equal(RuntimeContainerState.Running, inspected!.State);
            Assert.Equal(new[] { "sleep", "300" }, inspected.Argv);
            Assert.Contains("E2E=yes", inspected.Env);
            Assert.Equal("/tmp", inspected.WorkingDir);
            Assert.Equal("1", inspected.Labels["com.chillicream.cider.test"]);
            Assert.Contains("alpine", inspected.ImageReference, StringComparison.Ordinal);
            Assert.NotNull(inspected.StartedAt);

            var attachment = Assert.Single(inspected.Networks);
            Assert.Equal("default", attachment.Network);
            Assert.NotNull(attachment.IPv4Address);
            Assert.DoesNotContain("/", attachment.IPv4Address!, StringComparison.Ordinal);

            var all = await runtime.ListContainersAsync(ct);
            Assert.Contains(all, c => c.RuntimeId == name && c.State == RuntimeContainerState.Running);

            Assert.Null(await runtime.InspectContainerAsync("adtest-definitely-missing", ct));

            // ---- pipe exec with stdin ----
            await using (var exec = await runtime.ExecAsync(
                name,
                new ExecSpec { Argv = ["cat"], OpenStdin = true },
                ct))
            {
                Assert.NotNull(exec.Stdin);
                await exec.Stdin!.WriteAsync("hello-from-stdin\n"u8.ToArray(), ct);
                await exec.Stdin.FlushAsync(ct);
                await exec.CloseStdinAsync();

                var echoed = await new StreamReader(exec.Stdout).ReadToEndAsync(ct);
                Assert.Equal("hello-from-stdin", echoed.Trim());
                Assert.Equal(0, await exec.Exited);
            }

            // ---- pty exec with a resize ----
            await using (var pty = await runtime.ExecAsync(
                name,
                new ExecSpec { Argv = ["sh"], Tty = true, OpenStdin = true },
                ct))
            {
                Assert.True(pty.HasTty);
                Assert.Null(pty.Stderr);

                var output = new StringBuilder();
                var reader = Task.Run(
                    async () =>
                    {
                        var buffer = new byte[4096];
                        int read;
                        while ((read = await pty.Stdout.ReadAsync(buffer, CancellationToken.None)) > 0)
                        {
                            lock (output)
                            {
                                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            }
                        }
                    },
                    CancellationToken.None);

                await SendAsync(pty, "tty; stty size\n", ct);

                // openpty was given the default 80x24 window.
                await WaitForAsync(output, "/dev/pts", ct);
                await WaitForAsync(output, "24 80", ct);

                await pty.ResizeAsync(120, 50, ct);
                await SendAsync(pty, "stty size\n", ct);
                await WaitForAsync(output, "50 120", ct);

                await SendAsync(pty, "exit\n", ct);
                Assert.Equal(0, await pty.Exited);
                await reader.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }

            // ---- stats ----
            var stats = await runtime.GetStatsAsync(name, ct);
            Assert.NotNull(stats);
            Assert.True(stats!.MemoryUsageBytes > 0);

            // ---- logs ----
            await using (var logs = await runtime.OpenLogsAsync(name, follow: true, tail: 10, ct))
            {
                // `logs -f` never ends by itself: disposing the stream must kill the child.
                var buffer = new byte[256];
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    var read = await logs.ReadAsync(buffer, readCts.Token);
                    Assert.True(read >= 0);
                }
                catch (OperationCanceledException)
                {
                    // Expected: nothing more is coming and the CLI keeps the pipe open.
                }
            }

            // ---- stop ----
            await runtime.StopContainerAsync(name, timeoutSeconds: 2, signal: null, ct);
            var heldExit = await held.Exited.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.True(heldExit >= 0, $"held start -a exited with {heldExit}");

            var stopped = await runtime.InspectContainerAsync(name, ct);
            Assert.Equal(RuntimeContainerState.Stopped, stopped!.State);
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }

            await runtime.RemoveContainerAsync(name, force: true, CancellationToken.None);
        }
    }

    [E2EFact]
    public async Task Images_networks_and_volumes_round_trip()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var runtime = NewRuntime();

        await runtime.EnsureReadyAsync(ct);

        var images = await runtime.ListImagesAsync(ct);
        Assert.Contains(images, i => i.References.Any(r => r.Contains("alpine", StringComparison.Ordinal)));
        Assert.All(images, i => Assert.StartsWith("sha256:", i.Id, StringComparison.Ordinal));

        var detail = await runtime.InspectImageAsync(Image, ct);
        Assert.NotNull(detail);
        Assert.Equal("arm64", detail!.Architecture);
        Assert.Equal("linux", detail.Os);
        Assert.NotEmpty(detail.Config.Cmd);
        Assert.NotEmpty(detail.Layers);
        Assert.Null(await runtime.InspectImageAsync("adtest-missing-image:9", ct));

        var networks = await runtime.ListNetworksAsync(ct);
        Assert.Contains(networks, n => n.Name == "default" && n.Gateway is { Length: > 0 });
        Assert.NotNull(await runtime.InspectNetworkAsync("default", ct));

        var volumeName = NewName("vol");
        await runtime.CreateVolumeAsync(
            new VolumeSpec { Name = volumeName, Labels = new Dictionary<string, string> { ["x"] = "y" } },
            ct);

        try
        {
            var volume = await runtime.InspectVolumeAsync(volumeName, ct);
            Assert.NotNull(volume);
            Assert.Equal("y", volume!.Labels["x"]);
            Assert.Contains(await runtime.ListVolumesAsync(ct), v => v.Name == volumeName);
        }
        finally
        {
            await runtime.RemoveVolumeAsync(volumeName, force: false, CancellationToken.None);
        }

        var missing = await Assert.ThrowsAsync<RuntimeException>(
            () => runtime.RemoveVolumeAsync("adtest-missing-volume", force: false, ct));
        Assert.Equal(RuntimeErrorKind.NotFound, missing.Kind);

        var usage = await runtime.GetDiskUsageAsync(ct);
        Assert.True(usage.ImagesBytes > 0);
    }

    private static async Task SendAsync(IContainerProcess process, string text, CancellationToken ct)
    {
        await process.Stdin!.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await process.Stdin.FlushAsync(ct);
    }

    private static async Task WaitForAsync(StringBuilder output, string expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(100, ct);
        }

        string dump;
        lock (output)
        {
            dump = output.ToString();
        }

        Assert.Fail($"pty output never contained '{expected}'. Got:\n{dump}");
    }

    private static async Task WaitForRunningAsync(IContainerRuntime runtime, string name, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var container = await runtime.InspectContainerAsync(name, ct);
            if (container?.State == RuntimeContainerState.Running &&
                container.Networks.Any(n => n.IPv4Address is { Length: > 0 }))
            {
                return;
            }

            await Task.Delay(250, ct);
        }

        Assert.Fail($"container {name} never reached the running state");
    }
}
