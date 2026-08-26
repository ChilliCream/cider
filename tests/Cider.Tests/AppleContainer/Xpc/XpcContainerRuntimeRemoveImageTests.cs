using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// cider-ede.31: <c>docker rmi</c> used to also sweep the *whole* content store
/// (<c>imageCleanupOrphanedBlobs</c>) on every single call — not scoped to the image just deleted —
/// which raced any concurrent pull/load that had written blobs but not yet committed its index entry
/// and corrupted the store twice in one day. The fix moves that sweep to <c>PruneImagesAsync</c> (the
/// one place a user explicitly asked to reclaim space) and gates it exclusively, via
/// <see cref="BlobSweepGate"/>, against this runtime's own in-flight pulls/loads. Drives
/// <see cref="XpcContainerRuntime"/> through its test-only constructor (the same
/// <c>ImagesServiceClient</c> injection seam <c>XpcContainerRuntimeListImagesToleranceTests</c> uses),
/// with no live apiserver connection involved.
/// </summary>
public sealed class XpcContainerRuntimeRemoveImageTests
{
    [Fact]
    public async Task RemoveImageAsync_IssuesNoCleanupOrphanedBlobsCall()
    {
        var fake = new RecordingImagesServiceClient();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);

        var delete = Assert.Single(fake.Calls, c => c.StartsWith("ImageDeleteAsync:", StringComparison.Ordinal));
        Assert.Equal("ImageDeleteAsync:docker.io/library/alpine:3.19:False", delete);
        Assert.DoesNotContain(fake.Calls, c => c == "ImageCleanupOrphanedBlobsAsync");
    }

    [Fact]
    public async Task PruneImagesAsync_CallsCleanupOrphanedBlobsExactlyOnce()
    {
        var fake = new RecordingImagesServiceClient();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([], CancellationToken.None);

        Assert.Single(fake.Calls, c => c == "ImageCleanupOrphanedBlobsAsync");
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("ImageDeleteAsync:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The concurrency shape the task's own verification names: "a pull ... held mid-write while a
    /// prune runs must leave the pulled image intact". <see cref="RecordingImagesServiceClient"/>'s
    /// <c>ImageCleanupOrphanedBlobsAsync</c> blocks until released; a concurrently-started pull must
    /// not observe the sweep complete before the pull itself does — proving
    /// <see cref="BlobSweepGate.EnterSweepAsync"/> genuinely waits for an in-flight
    /// <see cref="BlobSweepGate.EnterImageWriteAsync"/> rather than merely documenting that it should.
    /// </summary>
    [Fact]
    public async Task PullImageAsync_HeldMidWrite_BlocksAConcurrentSweepUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        fake.ArmPullGate();

        var progress = new Progress<ProgressEvent>();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        // The pull is genuinely mid-write (inside ImagePullAsync, blocked on the armed gate) before the
        // sweep is even started, the same shape a real pull that has written blobs but not yet
        // committed its index entry would present to a sweep that started a moment later.
        await fake.WaitUntilPullBlockedAsync();

        var sweepTask = runtime.PruneImagesAsync([], CancellationToken.None);

        // The sweep must not be able to complete while the pull is still held — give it a beat to
        // (wrongly) race ahead before releasing the pull, the way the pre-fix code would have let it.
        var racedAhead = await Task.WhenAny(sweepTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(sweepTask, racedAhead);
        Assert.False(sweepTask.IsCompleted, "PruneImagesAsync must wait for the in-flight pull to finish");

        fake.ReleasePull();
        await pullTask;
        await sweepTask;

        // Order proves the wait was real, not coincidental: the pull's own ImagePullAsync call is
        // recorded before the sweep's ImageCleanupOrphanedBlobsAsync call.
        var pullIndex = fake.Calls.IndexOf(fake.Calls.First(c => c.StartsWith("ImagePullAsync:", StringComparison.Ordinal)));
        var sweepIndex = fake.Calls.IndexOf("ImageCleanupOrphanedBlobsAsync");
        Assert.True(pullIndex < sweepIndex, $"expected the pull to be recorded before the sweep; calls were [{string.Join(", ", fake.Calls)}]");
    }

    /// <summary>The reverse ordering: a sweep already in flight blocks a pull that starts after it,
    /// until the sweep finishes — the other half of the same gate.</summary>
    [Fact]
    public async Task PruneImagesAsync_InFlight_BlocksAConcurrentPullUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        fake.ArmSweepGate();

        var sweepTask = runtime.PruneImagesAsync([], CancellationToken.None);
        await fake.WaitUntilSweepBlockedAsync();

        var progress = new Progress<ProgressEvent>();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(pullTask, racedAhead);
        Assert.False(pullTask.IsCompleted, "PullImageAsync must wait for the in-flight sweep to finish");

        fake.ReleaseSweep();
        await sweepTask;
        await pullTask;

        var sweepIndex = fake.Calls.IndexOf("ImageCleanupOrphanedBlobsAsync");
        var pullIndex = fake.Calls.IndexOf(fake.Calls.First(c => c.StartsWith("ImagePullAsync:", StringComparison.Ordinal)));
        Assert.True(sweepIndex < pullIndex, $"expected the sweep to be recorded before the pull; calls were [{string.Join(", ", fake.Calls)}]");
    }

    /// <summary>
    /// cider-ede.31 correction: <c>BuildImageAsync</c> delegates straight to the CLI fallback
    /// (<c>XpcContainerRuntime.cs</c>'s // FALLBACK block) rather than through <c>_imagesClient</c>
    /// like <see cref="PullImageAsync"/> above — it was the one XPC-transport write path left ungated
    /// against <see cref="PruneImagesAsync"/>. This proves it now enters the same
    /// <see cref="BlobSweepGate"/> instance before delegating: a build held mid-write by
    /// <see cref="FakeContainerRuntime.ArmBuildGate"/> must block a concurrently-started
    /// <see cref="PruneImagesAsync"/> until it completes, the same shape
    /// <see cref="PullImageAsync_HeldMidWrite_BlocksAConcurrentSweepUntilItCompletes"/> proves for pull.
    /// </summary>
    [Fact]
    public async Task BuildImageAsync_HeldMidWrite_BlocksAConcurrentSweepUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient();
        var cliFallback = new FakeContainerRuntime();
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        cliFallback.ArmBuildGate();

        var progress = new Progress<ProgressEvent>();
        var buildTask = runtime.BuildImageAsync(new BuildSpec { ContextDir = "/tmp/ctx" }, progress, CancellationToken.None);

        // The build is genuinely mid-write (inside BuildImageAsync, blocked on the armed gate) before
        // the sweep is even started, the same shape a real build that has written blobs but not yet
        // committed its index entry would present to a sweep that started a moment later.
        await cliFallback.WaitUntilBuildBlockedAsync();

        var sweepTask = runtime.PruneImagesAsync([], CancellationToken.None);

        // The sweep must not be able to complete while the build is still held — give it a beat to
        // (wrongly) race ahead before releasing the build, the way the pre-fix code would have let it
        // (BuildImageAsync used to delegate with no XPC-side gate entry at all).
        var racedAhead = await Task.WhenAny(sweepTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(sweepTask, racedAhead);
        Assert.False(sweepTask.IsCompleted, "PruneImagesAsync must wait for the in-flight build to finish");

        cliFallback.ReleaseBuild();
        await buildTask;
        await sweepTask;

        // Order proves the wait was real, not coincidental: the build's own call is recorded before the
        // sweep's ImageCleanupOrphanedBlobsAsync call.
        var buildIndex = cliFallback.Calls.IndexOf(cliFallback.Calls.First(c => c.StartsWith("BuildImageAsync:", StringComparison.Ordinal)));
        var sweepIndex = fake.Calls.IndexOf("ImageCleanupOrphanedBlobsAsync");
        Assert.True(buildIndex >= 0 && sweepIndex >= 0, $"expected both calls to be recorded; build calls were [{string.Join(", ", cliFallback.Calls)}], sweep calls were [{string.Join(", ", fake.Calls)}]");
    }

    /// <summary>The reverse ordering: a sweep already in flight blocks a build that starts after it,
    /// until the sweep finishes — the other half of the same gate.</summary>
    [Fact]
    public async Task PruneImagesAsync_InFlight_BlocksAConcurrentBuildUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient();
        var cliFallback = new FakeContainerRuntime();
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        fake.ArmSweepGate();

        var sweepTask = runtime.PruneImagesAsync([], CancellationToken.None);
        await fake.WaitUntilSweepBlockedAsync();

        var progress = new Progress<ProgressEvent>();
        var buildTask = runtime.BuildImageAsync(new BuildSpec { ContextDir = "/tmp/ctx" }, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(buildTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(buildTask, racedAhead);
        Assert.False(buildTask.IsCompleted, "BuildImageAsync must wait for the in-flight sweep to finish");

        fake.ReleaseSweep();
        await sweepTask;
        await buildTask;

        var sweepIndex = fake.Calls.IndexOf("ImageCleanupOrphanedBlobsAsync");
        Assert.True(sweepIndex >= 0 && cliFallback.Calls.Any(c => c.StartsWith("BuildImageAsync:", StringComparison.Ordinal)), $"expected both calls to be recorded; build calls were [{string.Join(", ", cliFallback.Calls)}], sweep calls were [{string.Join(", ", fake.Calls)}]");
    }

    /// <summary>
    /// cider-bci regression: <c>imageCleanupOrphanedBlobs</c> walks the *whole* store, including blobs
    /// from images this daemon never touched, so a pre-existing dangling/unresolvable content reference
    /// elsewhere in the store (the same cider-ede.24 class <see cref="XpcContainerRuntimeListImagesToleranceTests"/>
    /// covers for <c>ListImagesAsync</c>) must not turn the sweep's failure into a total
    /// <c>PruneImagesAsync</c> failure — before the fix, this exception propagated straight out and
    /// discarded every per-image deletion <c>ImageManager.PruneAsync</c> had already made, so
    /// `docker image prune -f` came back as a 500 (or, before cider-ede.31 made the sweep unconditional,
    /// silently skipped every dangling image whose deletion happened to run after this exception's
    /// spiritual predecessor). The fix degrades the same way <c>ListImagesAsync</c> does: log exactly
    /// one Warning naming the offending digest and let the call return normally.
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_ToleratesADanglingContentReferenceInTheSweep_LogsOneWarningAndDoesNotThrow()
    {
        var digest = "sha256:" + new string('9', 64);
        var fake = new RecordingImagesServiceClient();
        fake.CleanupFailure = XpcException.ApiServer("internalError", $"content with digest {digest}");
        var logger = new RecordingLogger<XpcContainerRuntime>();
        var runtime = NewRuntime(fake, new FakeContainerRuntime(), logger);
        using var _ = runtime;

        // Must not throw: the store-wide sweep failing over unrelated store corruption is not the
        // caller's failure to report.
        await runtime.PruneImagesAsync([], CancellationToken.None);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(digest, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>A genuine, non-dangling-content failure from the sweep must still surface — this
    /// tolerance is narrowly scoped to the one known corruption shape, not "swallow every sweep
    /// error".</summary>
    [Fact]
    public async Task PruneImagesAsync_StillThrows_ForANonDanglingContentFailureInTheSweep()
    {
        var fake = new RecordingImagesServiceClient();
        fake.CleanupFailure = XpcException.ApiServer("internalError", "something else entirely went wrong");
        var runtime = NewRuntime(fake, new FakeContainerRuntime(), NullLogger<XpcContainerRuntime>.Instance);
        using var _ = runtime;

        await Assert.ThrowsAsync<RuntimeException>(() => runtime.PruneImagesAsync([], CancellationToken.None));
    }

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient) =>
        NewRuntime(imagesClient, new FakeContainerRuntime(), NullLogger<XpcContainerRuntime>.Instance);

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient, IContainerRuntime cliFallback) =>
        NewRuntime(imagesClient, cliFallback, NullLogger<XpcContainerRuntime>.Instance);

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient, IContainerRuntime cliFallback, ILogger<XpcContainerRuntime> logger)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            cliFallback, apiserver, images, capabilities, options, logger, imagesClient);
    }

    /// <summary>Captures every log entry made against it — the same shape
    /// <c>XpcContainerRuntimeListImagesToleranceTests</c> uses for the analogous <c>ListImagesAsync</c>
    /// tolerance.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Records every call it makes, and lets a test hold <c>ImagePullAsync</c> or
    /// <c>ImageCleanupOrphanedBlobsAsync</c> open on demand (<see cref="ArmPullGate"/>/
    /// <see cref="ArmSweepGate"/>) to drive the two ordering tests above. Every other override is a
    /// bare no-op/empty result — this fake never talks to a real apiserver.</summary>
    private sealed class RecordingImagesServiceClient()
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        private readonly object _sync = new();
        public List<string> Calls { get; } = [];

        private TaskCompletionSource<bool>? _pullGate;
        private TaskCompletionSource<bool>? _pullBlockedSignal;

        private TaskCompletionSource<bool>? _sweepGate;
        private TaskCompletionSource<bool>? _sweepBlockedSignal;

        /// <summary>Test hook: makes the next <see cref="ImageCleanupOrphanedBlobsAsync"/> call throw
        /// this instead of its normal empty result — simulates the apiserver rejecting the sweep
        /// (a dangling content reference, or any other failure a test wants to arm).</summary>
        public XpcException? CleanupFailure { get; set; }

        public void ArmPullGate()
        {
            _pullGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pullBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilPullBlockedAsync() => _pullBlockedSignal!.Task;

        public void ReleasePull() => _pullGate?.TrySetResult(true);

        public void ArmSweepGate()
        {
            _sweepGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sweepBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilSweepBlockedAsync() => _sweepBlockedSignal!.Task;

        public void ReleaseSweep() => _sweepGate?.TrySetResult(true);

        private void Record(string call)
        {
            lock (_sync)
            {
                Calls.Add(call);
            }
        }

        public override async Task<ImageDescription> ImagePullAsync(string reference, Platform? platform, XpcObject? progressEndpoint, CancellationToken ct)
        {
            Record($"ImagePullAsync:{reference}");
            if (_pullGate is not null)
            {
                _pullBlockedSignal!.TrySetResult(true);
                await _pullGate.Task.ConfigureAwait(false);
            }

            return new ImageDescription
            {
                Reference = reference,
                Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:" + new string('a', 64), Size = 1 },
            };
        }

        public override Task ImageUnpackAsync(ImageDescription image, Platform platform, CancellationToken ct, XpcObject? progressEndpoint = null)
        {
            Record("ImageUnpackAsync");
            return Task.CompletedTask;
        }

        public override Task ImageDeleteAsync(string reference, bool garbageCollect, CancellationToken ct)
        {
            Record($"ImageDeleteAsync:{reference}:{garbageCollect}");
            return Task.CompletedTask;
        }

        public override async Task<(IReadOnlyList<string> Digests, ulong ImageSize)> ImageCleanupOrphanedBlobsAsync(CancellationToken ct)
        {
            if (CleanupFailure is { } failure)
            {
                CleanupFailure = null;
                throw failure;
            }

            if (_sweepGate is not null)
            {
                _sweepBlockedSignal!.TrySetResult(true);
                await _sweepGate.Task.ConfigureAwait(false);
            }

            Record("ImageCleanupOrphanedBlobsAsync");
            return ((IReadOnlyList<string>)[], 0UL);
        }
    }
}
