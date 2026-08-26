using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// cider-ede.31, CLI-transport half: <c>container image delete &lt;ref&gt;</c> sweeps the whole
/// content store as an unavoidable step of its single process invocation (Apple's own
/// <c>ImageDelete.swift</c> — <c>container image delete --help</c> carries no flag to skip it), so on
/// this transport a "sweep" is not something cider chooses to run, it is what
/// <see cref="AppleContainerRuntime.RemoveImageAsync"/> already does every time it is called. These
/// tests prove <see cref="BlobSweepGate"/> genuinely keeps that delete from overlapping this runtime's
/// own concurrent pulls, in both directions — the same shape
/// <c>XpcContainerRuntimeRemoveImageTests</c> proves for the XPC transport. Driven through
/// <see cref="ContainerCli"/>'s own test seam (<see cref="AppleContainerRuntimeImageTests"/>'s
/// <c>ScriptedContainerCli</c> pattern) — no real <c>container</c> binary involved.
/// </summary>
public sealed class AppleContainerRuntimeBlobSweepGateTests
{
    [Fact]
    public async Task PullImageAsync_HeldMidWrite_BlocksAConcurrentDeleteUntilItCompletes()
    {
        var cli = new GatedContainerCli(new AppleContainerOptions());
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        cli.ArmStreamingGate();

        var progress = new NoopProgress();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        await cli.WaitUntilStreamingBlockedAsync();

        var deleteTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);

        var racedAhead = await Task.WhenAny(deleteTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(deleteTask, racedAhead);
        Assert.False(deleteTask.IsCompleted, "RemoveImageAsync must wait for the in-flight pull to finish");

        cli.ReleaseStreaming();
        await pullTask;
        await deleteTask;

        var pullIndex = cli.Calls.IndexOf(cli.Calls.First(c => c.StartsWith("RunStreamingAsync:", StringComparison.Ordinal)));
        var deleteIndex = cli.Calls.IndexOf(cli.Calls.First(c => c.StartsWith("RunAsync:", StringComparison.Ordinal)));
        Assert.True(pullIndex < deleteIndex, $"expected the pull to be recorded before the delete; calls were [{string.Join(", ", cli.Calls)}]");
    }

    [Fact]
    public async Task RemoveImageAsync_InFlight_BlocksAConcurrentPullUntilItCompletes()
    {
        var cli = new GatedContainerCli(new AppleContainerOptions());
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        cli.ArmRunGate();

        var deleteTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);
        await cli.WaitUntilRunBlockedAsync();

        var progress = new NoopProgress();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(pullTask, racedAhead);
        Assert.False(pullTask.IsCompleted, "PullImageAsync must wait for the in-flight delete to finish");

        cli.ReleaseRun();
        await deleteTask;
        await pullTask;

        var deleteIndex = cli.Calls.IndexOf(cli.Calls.First(c => c.StartsWith("RunAsync:", StringComparison.Ordinal)));
        var pullIndex = cli.Calls.IndexOf(cli.Calls.First(c => c.StartsWith("RunStreamingAsync:", StringComparison.Ordinal)));
        Assert.True(deleteIndex < pullIndex, $"expected the delete to be recorded before the pull; calls were [{string.Join(", ", cli.Calls)}]");
    }

    private sealed class NoopProgress : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value)
        {
        }
    }

    /// <summary>Records every call, and lets a test hold <c>RunStreamingAsync</c> (pull/build) or
    /// <c>RunAsync</c> (delete/tag) open on demand. Both return an immediate success with no output
    /// once released — this fake never spawns a real <c>container</c> process.</summary>
    private sealed class GatedContainerCli(AppleContainerOptions options) : ContainerCli(options, NullLogger.Instance)
    {
        private readonly object _sync = new();
        public List<string> Calls { get; } = [];

        private TaskCompletionSource<bool>? _streamingGate;
        private TaskCompletionSource<bool>? _streamingBlockedSignal;

        private TaskCompletionSource<bool>? _runGate;
        private TaskCompletionSource<bool>? _runBlockedSignal;

        public void ArmStreamingGate()
        {
            _streamingGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _streamingBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilStreamingBlockedAsync() => _streamingBlockedSignal!.Task;

        public void ReleaseStreaming() => _streamingGate?.TrySetResult(true);

        public void ArmRunGate()
        {
            _runGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilRunBlockedAsync() => _runBlockedSignal!.Task;

        public void ReleaseRun() => _runGate?.TrySetResult(true);

        private void Record(string call)
        {
            lock (_sync)
            {
                Calls.Add(call);
            }
        }

        public override async Task<CliResult> RunStreamingAsync(
            IReadOnlyList<string> args,
            Action<string, bool> onLine,
            CancellationToken ct,
            TimeSpan? timeout = null)
        {
            Record($"RunStreamingAsync:{string.Join(' ', args)}");
            if (_streamingGate is not null)
            {
                _streamingBlockedSignal!.TrySetResult(true);
                await _streamingGate.Task.ConfigureAwait(false);
            }

            return new CliResult(0, "", "");
        }

        public override async Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            Record($"RunAsync:{string.Join(' ', args)}");
            if (_runGate is not null)
            {
                _runBlockedSignal!.TrySetResult(true);
                await _runGate.Task.ConfigureAwait(false);
            }

            return new CliResult(0, "", "");
        }
    }
}
