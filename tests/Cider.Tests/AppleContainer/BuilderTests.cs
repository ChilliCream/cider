using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// The Apple builder VM seam (<c>container builder status|start</c> and the
/// <c>exec -i buildkit buildctl dial-stdio</c> dial), driven through the <see cref="ContainerCli"/>
/// seam instead of a real <c>container</c> binary.
/// </summary>
public sealed class BuilderTests
{
    private const string RunningRow =
        "buildkit  ghcr.io/apple/container-builder-shim/builder:0.13.1  running  192.168.64.7/24  2  2048 MB\n";

    private const string StoppedRow =
        "buildkit  ghcr.io/apple/container-builder-shim/builder:0.13.1  stopped\n";

    // ---- GetBuilderStatusAsync / ParseBuilderStatus ------------------------

    [Fact]
    public async Task GetBuilderStatusAsync_parses_a_running_row()
    {
        var (runtime, cli) = CreateRuntime(new CliResult(0, RunningRow, ""));

        var status = await runtime.GetBuilderStatusAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("buildkit", status!.Name);
        Assert.Equal("ghcr.io/apple/container-builder-shim/builder:0.13.1", status.Image);
        Assert.True(status.Running);
        Assert.Equal("192.168.64.7/24", status.Address);
        Assert.Equal(2, status.Cpus);
        Assert.Equal(2048L * 1024 * 1024, status.MemoryBytes);
        Assert.Equal(new[] { "builder", "status" }, cli.LastArgs);
    }

    [Fact]
    public async Task GetBuilderStatusAsync_parses_a_stopped_row_with_no_address_cpus_or_memory()
    {
        var (runtime, _) = CreateRuntime(new CliResult(0, StoppedRow, ""));

        var status = await runtime.GetBuilderStatusAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.False(status!.Running);
        Assert.Null(status.Address);
        Assert.Null(status.Cpus);
        Assert.Null(status.MemoryBytes);
    }

    [Fact]
    public async Task GetBuilderStatusAsync_is_null_when_no_builder_has_ever_been_created()
    {
        var (runtime, _) = CreateRuntime(new CliResult(1, "", "Error: builder not found"));

        Assert.Null(await runtime.GetBuilderStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetBuilderStatusAsync_is_null_when_the_command_succeeds_with_no_buildkit_row()
    {
        var (runtime, _) = CreateRuntime(new CliResult(0, "", ""));

        Assert.Null(await runtime.GetBuilderStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetBuilderStatusAsync_surfaces_a_genuine_runtime_failure()
    {
        var (runtime, _) = CreateRuntime(new CliResult(1, "", "could not connect to the apiserver"));

        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => runtime.GetBuilderStatusAsync(CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public void ParseBuilderStatus_skips_a_header_row()
    {
        var status = AppleContainerRuntime.ParseBuilderStatus(
            "NAME      IMAGE                                                STATE    ADDRESS          CPUS  MEMORY\n" +
            RunningRow);

        Assert.NotNull(status);
        Assert.Equal(2, status!.Cpus);
    }

    [Fact]
    public void ParseBuilderStatus_is_null_for_blank_output()
    {
        Assert.Null(AppleContainerRuntime.ParseBuilderStatus(""));
        Assert.Null(AppleContainerRuntime.ParseBuilderStatus("   \n  "));
    }

    // ---- StartBuilderAsync --------------------------------------------------

    [Fact]
    public async Task StartBuilderAsync_omits_flags_when_unset()
    {
        var (runtime, cli) = CreateRuntime(new CliResult(0, "", ""));

        await runtime.StartBuilderAsync(null, null, CancellationToken.None);

        Assert.Equal(new[] { "builder", "start" }, cli.LastArgs);
    }

    [Fact]
    public async Task StartBuilderAsync_passes_cpus_and_memory_only_when_set()
    {
        var (runtime, cli) = CreateRuntime(new CliResult(0, "", ""));

        await runtime.StartBuilderAsync(4, 4L * 1024 * 1024 * 1024, CancellationToken.None);

        Assert.Equal(new[] { "builder", "start", "-c", "4", "-m", "4096M" }, cli.LastArgs);
    }

    [Fact]
    public async Task StartBuilderAsync_tolerates_already_running()
    {
        var (runtime, _) = CreateRuntime(new CliResult(1, "", "Error: builder is already running"));

        // Must not throw.
        await runtime.StartBuilderAsync(null, null, CancellationToken.None);
    }

    [Fact]
    public async Task StartBuilderAsync_surfaces_a_genuine_failure()
    {
        var (runtime, _) = CreateRuntime(new CliResult(1, "", "could not connect to the apiserver"));

        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => runtime.StartBuilderAsync(null, null, CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.Unavailable, ex.Kind);
    }

    // ---- DialBuilderAsync -----------------------------------------------------

    [Fact]
    public async Task DialBuilderAsync_execs_buildctl_dial_stdio_on_the_buildkit_container()
    {
        var options = new AppleContainerOptions();
        var cli = new RecordingArgsCli(options);
        var runtime = new AppleContainerRuntime(options, NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var process = await runtime.DialBuilderAsync(CancellationToken.None);

        Assert.Equal(new[] { "exec", "-i", "buildkit", "buildctl", "dial-stdio" }, cli.LastArgs);
        Assert.False(process.HasTty);
        Assert.NotNull(process.Stdin);
        Assert.NotNull(process.Stderr);
    }

    /// <summary>
    /// The seam-level stand-in for the manual verification the ticket calls for (a real HTTP/2 preface
    /// round trip against buildkitd): proves the exec pipe is a raw, binary-safe duplex byte stream —
    /// arbitrary bytes in, the same bytes out, untouched — and that disposing it ends the process.
    /// </summary>
    [Fact]
    public async Task DialBuilderAsync_is_a_binary_safe_duplex_pipe_that_ends_on_dispose()
    {
        var options = new AppleContainerOptions();
        var cli = new RecordingArgsCli(options);
        var runtime = new AppleContainerRuntime(options, NullLogger<AppleContainerRuntime>.Instance, cli);

        var process = await runtime.DialBuilderAsync(CancellationToken.None);
        try
        {
            // An HTTP/2 client preface tail plus an empty SETTINGS frame — arbitrary non-UTF8-safe
            // bytes, exactly what a real dial-stdio session would carry.
            byte[] payload =
            [
                (byte)'S', (byte)'M', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n',
                0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
            ];

            await process.Stdin!.WriteAsync(payload, CancellationToken.None);
            await process.Stdin.FlushAsync(CancellationToken.None);
            await process.CloseStdinAsync();

            var buffer = new byte[payload.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await process.Stdout.ReadAsync(buffer.AsMemory(read), CancellationToken.None);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            Assert.Equal(payload, buffer);
            Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    private static (AppleContainerRuntime Runtime, ScriptedRunCli Cli) CreateRuntime(CliResult result)
    {
        var options = new AppleContainerOptions();
        var cli = new ScriptedRunCli(options, result);
        return (new AppleContainerRuntime(options, NullLogger<AppleContainerRuntime>.Instance, cli), cli);
    }

    /// <summary>A <see cref="ContainerCli"/> that answers every <see cref="RunAsync"/> call with a
    /// canned result and remembers the args it was called with — for the pure-argv/status paths that
    /// never spawn a process (<c>builder status</c>, <c>builder start</c>).</summary>
    private sealed class ScriptedRunCli(AppleContainerOptions options, CliResult result)
        : ContainerCli(options, NullLogger.Instance)
    {
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            LastArgs = args;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A <see cref="ContainerCli"/> that records the argv <see cref="ProcessLauncher"/> was asked to
    /// run and redirects the actual process to <c>/bin/sh -c cat</c> — a stand-in for
    /// <c>buildctl dial-stdio</c> good enough to prove the pipe is a real, binary-safe duplex byte
    /// stream without depending on the real <c>container</c> binary or buildkitd being present.
    /// </summary>
    private sealed class RecordingArgsCli(AppleContainerOptions options) : ContainerCli(options, NullLogger.Instance)
    {
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public override System.Diagnostics.ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
        {
            LastArgs = args;
            var startInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("cat");
            return startInfo;
        }
    }
}
