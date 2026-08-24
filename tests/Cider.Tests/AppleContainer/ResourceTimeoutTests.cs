using System.Diagnostics;
using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// What the daemon does when the runtime takes a resource call and never answers.
/// The wedged runtime is simulated at the CLI seam — a stand-in process that never exits — so the
/// real timeout machinery in <see cref="ContainerCli.RunAsync"/> is what is under test, and no
/// actual <c>container</c> runtime is disturbed.
/// </summary>
public sealed class ResourceTimeoutTests
{
    /// <summary>Enough to catch a call that fell back to the five-minute <c>CommandTimeout</c>,
    /// without waiting anywhere near it: the test fails fast instead of stalling the suite.</summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    [Fact]
    public void Resource_operations_get_a_bound_a_docker_client_can_wait_out()
    {
        var options = new AppleContainerOptions();

        // dockerd answers these in milliseconds; the bound only has to cover a loaded-but-healthy
        // runtime, and must stay under docker-py's/compose's own 60 s HTTP read timeout so the
        // client renders our error rather than its own connection failure.
        Assert.Equal(TimeSpan.FromSeconds(30), options.ResourceTimeout);
        Assert.True(options.ResourceTimeout < TimeSpan.FromSeconds(60));
        Assert.True(options.ResourceTimeout < options.CommandTimeout);
    }

    [Theory]
    [InlineData("network-create", "network create")]
    [InlineData("network-delete", "network delete")]
    [InlineData("volume-create", "volume create")]
    [InlineData("volume-delete", "volume delete")]
    public async Task A_runtime_that_never_answers_fails_within_the_bound(string operation, string expectedVerb)
    {
        var options = new AppleContainerOptions { ResourceTimeout = Bound };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new HangingContainerCli(options));

        using var cts = new CancellationTokenSource(TestBudget);
        var started = Stopwatch.GetTimestamp();

        var exception = await Assert.ThrowsAsync<RuntimeException>(() => operation switch
        {
            "network-create" => runtime.CreateNetworkAsync(new NetworkSpec { Name = "kfk-probe" }, cts.Token),
            "network-delete" => runtime.RemoveNetworkAsync("kfk-probe", cts.Token),
            "volume-create" => runtime.CreateVolumeAsync(new VolumeSpec { Name = "kfk-probe" }, cts.Token),
            _ => runtime.RemoveVolumeAsync("kfk-probe", force: false, cts.Token),
        });

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"{operation} took {elapsed.TotalSeconds:0.#}s on a 1 s bound — it is not running on ResourceTimeout");

        // Docker-shaped: a 500-mapped Timeout naming the runtime as the cause, with the recovery
        // pointer — not the raw `'container … ' timed out after 300s` CLI string.
        Assert.Equal(RuntimeErrorKind.Timeout, exception.Kind);
        Assert.Contains("Apple container runtime did not answer", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedVerb, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Troubleshooting", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("timed out after", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_who_gives_up_first_is_still_cancelled_not_timed_out()
    {
        var options = new AppleContainerOptions { ResourceTimeout = TestBudget };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new HangingContainerCli(options));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.CreateNetworkAsync(new NetworkSpec { Name = "kfk-probe" }, cts.Token));
    }

    /// <summary>
    /// A CLI whose every invocation is pointed at a process that never exits — the seam-level
    /// stand-in for a wedged runtime. Only the process is replaced: argument building, the timeout
    /// and the kill-on-timeout are the production ones.
    /// </summary>
    private sealed class HangingContainerCli(AppleContainerOptions options)
        : ContainerCli(options, NullLogger.Instance)
    {
        public override ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
        {
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 600");
            return startInfo;
        }
    }
}
