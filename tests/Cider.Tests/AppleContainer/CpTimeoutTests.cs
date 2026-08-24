using System.Diagnostics;
using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// Apple <c>container cp</c> hangs — no exit, ever — when its source path does not
/// exist in the guest (reproduced live against a running container; still wedged after 90+ s, and it
/// leaves that container's own exec/rm channel wedged behind it too). These bound the adapter's two
/// <c>cp</c> call sites at the CLI seam — a stand-in process that never exits, so the real timeout
/// machinery in <see cref="AppleContainerRuntime"/> and <see cref="ContainerCli"/> is what is under
/// test — without disturbing a real <c>container</c> runtime.
/// </summary>
public sealed class CpTimeoutTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public void Copy_bounds_are_generous_but_not_infinite_and_the_idle_check_is_far_tighter()
    {
        var options = new AppleContainerOptions();

        // The ceiling mirrors PullTimeout's "large payload, no progress signal, be generous" call;
        // the idle check is what actually answers a missing path quickly.
        Assert.Equal(TimeSpan.FromMinutes(30), options.CopyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.CopyIdleGrace);
        Assert.True(options.CopyIdleGrace < options.CopyTimeout);
        Assert.True(options.CopyIdleGrace < options.CommandTimeout);
    }

    [Fact]
    public async Task CopyFromContainer_of_a_source_that_never_produces_anything_fails_within_the_idle_grace()
    {
        // A five-minute (CommandTimeout) or half-hour (CopyTimeout) wait here is exactly the
        // pre-fix behaviour this test must not tolerate: it proves the call fails on the much
        // tighter CopyIdleGrace bound, not the generous overall ceiling.
        var options = new AppleContainerOptions
        {
            CopyIdleGrace = TimeSpan.FromMilliseconds(300),
            CopyTimeout = TimeSpan.FromMinutes(30),
        };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new HangingContainerCli(options));

        using var cts = new CancellationTokenSource(TestBudget);
        var dest = Directory.CreateTempSubdirectory("q1m-cp-").FullName;
        try
        {
            var started = Stopwatch.GetTimestamp();

            var exception = await Assert.ThrowsAsync<RuntimeException>(
                () => runtime.CopyFromContainerAsync("probe", "/does/not/exist", dest, cts.Token));

            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.True(
                elapsed < TimeSpan.FromSeconds(5),
                $"took {elapsed.TotalSeconds:0.#}s on a 300ms idle grace — it fell back to a bigger bound");

            Assert.Equal(RuntimeErrorKind.Timeout, exception.Kind);
            Assert.Contains("produced nothing", exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Troubleshooting in the cider README", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task CopyFromContainer_that_is_actively_writing_is_never_killed_by_the_idle_check()
    {
        // The idle CLI writes a file into the destination almost immediately and then hangs past the
        // idle grace, deep into the overall ceiling — proving the idle watch disarmed itself instead
        // of killing a transfer that had visibly started. The ceiling itself (not the idle check) is
        // what eventually stops it; that failure carries the *other* message ("did not answer"), which
        // this asserts to make the distinction load-bearing rather than incidental.
        var options = new AppleContainerOptions
        {
            CopyIdleGrace = TimeSpan.FromMilliseconds(200),
            CopyTimeout = TimeSpan.FromSeconds(1),
        };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new WritesThenHangsContainerCli(options));

        using var cts = new CancellationTokenSource(TestBudget);
        var dest = Directory.CreateTempSubdirectory("q1m-cp-").FullName;
        try
        {
            var exception = await Assert.ThrowsAsync<RuntimeException>(
                () => runtime.CopyFromContainerAsync("probe", "/big/file", dest, cts.Token));

            Assert.Equal(RuntimeErrorKind.Timeout, exception.Kind);
            Assert.Contains("did not answer", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("produced nothing", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task CopyToContainer_never_answering_fails_on_the_generous_ceiling_not_the_five_minute_default()
    {
        var options = new AppleContainerOptions { CopyTimeout = TimeSpan.FromMilliseconds(300) };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new HangingContainerCli(options));

        using var cts = new CancellationTokenSource(TestBudget);
        var src = Path.GetTempFileName();
        try
        {
            var started = Stopwatch.GetTimestamp();

            var exception = await Assert.ThrowsAsync<RuntimeException>(
                () => runtime.CopyToContainerAsync("probe", src, "/dest", cts.Token));

            var elapsed = Stopwatch.GetElapsedTime(started);
            Assert.True(
                elapsed < TimeSpan.FromSeconds(5),
                $"took {elapsed.TotalSeconds:0.#}s on a 300ms CopyTimeout — it is not running on that bound");

            Assert.Equal(RuntimeErrorKind.Timeout, exception.Kind);
            Assert.Contains("Apple container runtime did not answer", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(src);
        }
    }

    [Fact]
    public async Task A_caller_who_gives_up_first_is_still_cancelled_not_timed_out()
    {
        var options = new AppleContainerOptions { CopyIdleGrace = TestBudget, CopyTimeout = TestBudget };
        var runtime = new AppleContainerRuntime(
            options,
            NullLogger<AppleContainerRuntime>.Instance,
            new HangingContainerCli(options));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var dest = Directory.CreateTempSubdirectory("q1m-cp-").FullName;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runtime.CopyFromContainerAsync("probe", "/does/not/exist", dest, cts.Token));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    /// <summary>A CLI whose every invocation is pointed at a process that never exits and never
    /// produces output — the seam-level stand-in for the confirmed Apple `cp` hang.</summary>
    private sealed class HangingContainerCli(AppleContainerOptions options)
        : ContainerCli(options, NullLogger.Instance)
    {
        public override ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
        {
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 600");
            return startInfo;
        }
    }

    /// <summary>A CLI stand-in for a real, in-progress transfer: it writes into the destination
    /// directory (the last argument) almost immediately, then hangs — the shape a legitimate large
    /// copy could plausibly present if it stalled partway through, which the idle check must leave
    /// alone once it has seen the first byte land.</summary>
    private sealed class WritesThenHangsContainerCli(AppleContainerOptions options)
        : ContainerCli(options, NullLogger.Instance)
    {
        public override ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
        {
            var destination = args[^1];
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"echo partial > '{destination.TrimEnd('/')}/in-progress' && sleep 600");
            return startInfo;
        }
    }
}
