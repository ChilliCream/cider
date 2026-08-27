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
/// and corrupted the store twice in one day. cider-ede.41 then removed the store-wide sweep from
/// cider entirely (planner ruling, option A — a sweep in ONE process deletes ANOTHER process's
/// mid-write pull blobs; reproduced cross-process in ~2s with the in-process gate enabled in both
/// daemons, commit d63644b). The one sweep left on the XPC transport is the apiserver-unavailable
/// delete fallback, which shells to Apple's CLI delete — Apple's in-binary sweep, a documented
/// residual — and must hold <see cref="BlobSweepGate"/> exclusively against this runtime's own
/// in-flight pulls/builds. Drives <see cref="XpcContainerRuntime"/> through its test-only
/// constructor (the same <c>ImagesServiceClient</c> injection seam
/// <c>XpcContainerRuntimeListImagesToleranceTests</c> uses), with no live apiserver connection
/// involved.
/// </summary>
public sealed class XpcContainerRuntimeRemoveImageTests
{
    /// <summary>The primary (apiserver-reachable) delete path must be a pure reference drop: one
    /// <c>imageDelete</c> with <c>garbageCollect:false</c>, and no other images-service call — the
    /// no-sweep delete cider-ede.31 established and cider-ede.41 depends on.</summary>
    [Fact]
    public async Task RemoveImageAsync_IssuesExactlyOneNonGarbageCollectingDelete()
    {
        var fake = new RecordingImagesServiceClient();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("ImageDeleteAsync:docker.io/library/alpine:3.19:False", call);
    }

    /// <summary>
    /// The concurrency shape cider-ede.31's fix direction names: a pull held mid-write (blobs
    /// written, index entry not yet committed) must block the one remaining sweep on this transport —
    /// the apiserver-unavailable CLI delete fallback — from starting until the pull completes,
    /// proving <see cref="BlobSweepGate.EnterSweepAsync"/> genuinely waits for an in-flight
    /// <see cref="BlobSweepGate.EnterImageWriteAsync"/> rather than merely documenting that it should.
    /// </summary>
    [Fact]
    public async Task PullImageAsync_HeldMidWrite_BlocksAConcurrentFallbackDeleteSweepUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient { DeleteUnavailable = true };
        var cliFallback = new FakeContainerRuntime();
        SeedAlpine(cliFallback);
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        fake.ArmPullGate();

        var progress = new Progress<ProgressEvent>();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        // The pull is genuinely mid-write (inside ImagePullAsync, blocked on the armed gate) before
        // the fallback delete is even started.
        await fake.WaitUntilPullBlockedAsync();

        var removeTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);

        // The fallback's CLI delete (the sweep) must not be able to start while the pull is still
        // held — give it a beat to (wrongly) race ahead before releasing the pull.
        var racedAhead = await Task.WhenAny(removeTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(removeTask, racedAhead);
        Assert.False(removeTask.IsCompleted, "the fallback delete must wait for the in-flight pull to finish");
        Assert.DoesNotContain(cliFallback.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));

        fake.ReleasePull();
        await pullTask;
        await removeTask;

        Assert.Contains(cliFallback.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));
    }

    /// <summary>The reverse ordering: a fallback-delete sweep already in flight blocks a pull that
    /// starts after it, until the sweep finishes — the other half of the same gate.</summary>
    [Fact]
    public async Task FallbackDeleteSweep_InFlight_BlocksAConcurrentPullUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient { DeleteUnavailable = true };
        var cliFallback = new FakeContainerRuntime();
        SeedAlpine(cliFallback);
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        cliFallback.ArmRemoveGate();

        var removeTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);
        await cliFallback.WaitUntilRemoveBlockedAsync();

        var progress = new Progress<ProgressEvent>();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(pullTask, racedAhead);
        Assert.False(pullTask.IsCompleted, "PullImageAsync must wait for the in-flight fallback delete sweep to finish");
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("ImagePullAsync:", StringComparison.Ordinal));

        cliFallback.ReleaseRemove();
        await removeTask;
        await pullTask;

        Assert.Contains(fake.Calls, c => c.StartsWith("ImagePullAsync:", StringComparison.Ordinal));
    }

    /// <summary>
    /// cider-ede.31 correction: <c>BuildImageAsync</c> delegates straight to the CLI fallback
    /// (<c>XpcContainerRuntime.cs</c>'s // FALLBACK block) rather than through <c>_imagesClient</c>
    /// like <see cref="PullImageAsync"/> above — it was the one XPC-transport write path left ungated
    /// against the transport's sweep. This proves it still enters the same
    /// <see cref="BlobSweepGate"/> instance before delegating: a build held mid-write must block a
    /// concurrently-started fallback-delete sweep until it completes.
    /// </summary>
    [Fact]
    public async Task BuildImageAsync_HeldMidWrite_BlocksAConcurrentFallbackDeleteSweepUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient { DeleteUnavailable = true };
        var cliFallback = new FakeContainerRuntime();
        SeedAlpine(cliFallback);
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        cliFallback.ArmBuildGate();

        var progress = new Progress<ProgressEvent>();
        var buildTask = runtime.BuildImageAsync(new BuildSpec { ContextDir = "/tmp/ctx" }, progress, CancellationToken.None);
        await cliFallback.WaitUntilBuildBlockedAsync();

        var removeTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);

        var racedAhead = await Task.WhenAny(removeTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(removeTask, racedAhead);
        Assert.False(removeTask.IsCompleted, "the fallback delete must wait for the in-flight build to finish");
        Assert.DoesNotContain(cliFallback.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));

        cliFallback.ReleaseBuild();
        await buildTask;
        await removeTask;

        Assert.Contains(cliFallback.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));
    }

    /// <summary>The reverse ordering: a fallback-delete sweep already in flight blocks a build that
    /// starts after it, until the sweep finishes — the other half of the same gate.</summary>
    [Fact]
    public async Task FallbackDeleteSweep_InFlight_BlocksAConcurrentBuildUntilItCompletes()
    {
        var fake = new RecordingImagesServiceClient { DeleteUnavailable = true };
        var cliFallback = new FakeContainerRuntime();
        SeedAlpine(cliFallback);
        var runtime = NewRuntime(fake, cliFallback);
        using var _ = runtime;

        cliFallback.ArmRemoveGate();

        var removeTask = runtime.RemoveImageAsync("docker.io/library/alpine:3.19", force: false, CancellationToken.None);
        await cliFallback.WaitUntilRemoveBlockedAsync();

        var progress = new Progress<ProgressEvent>();
        var buildTask = runtime.BuildImageAsync(new BuildSpec { ContextDir = "/tmp/ctx" }, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(buildTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(buildTask, racedAhead);
        Assert.False(buildTask.IsCompleted, "BuildImageAsync must wait for the in-flight fallback delete sweep to finish");
        Assert.DoesNotContain(cliFallback.Calls, c => c.StartsWith("BuildImageAsync:", StringComparison.Ordinal));

        cliFallback.ReleaseRemove();
        await removeTask;
        await buildTask;

        Assert.Contains(cliFallback.Calls, c => c.StartsWith("BuildImageAsync:", StringComparison.Ordinal));
    }

    private static void SeedAlpine(FakeContainerRuntime cliFallback) =>
        cliFallback.SeedImage(new RuntimeImageDetail
        {
            Id = "sha256:" + new string('a', 64),
            References = ["docker.io/library/alpine:3.19"],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient) =>
        NewRuntime(imagesClient, new FakeContainerRuntime());

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient, IContainerRuntime cliFallback)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            cliFallback, apiserver, images, capabilities, options, NullLogger<XpcContainerRuntime>.Instance, imagesClient);
    }

    /// <summary>Records every call it makes; lets a test hold <c>ImagePullAsync</c> open on demand
    /// (<see cref="ArmPullGate"/>) and make <c>ImageDeleteAsync</c> report the apiserver unavailable
    /// (<see cref="DeleteUnavailable"/>), which routes <c>RemoveImageAsync</c> into its CLI fallback —
    /// the one store-wide sweep left on this transport since cider-ede.41. Every other override is a
    /// bare no-op/empty result — this fake never talks to a real apiserver.</summary>
    private sealed class RecordingImagesServiceClient()
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        private readonly object _sync = new();
        public List<string> Calls { get; } = [];

        private TaskCompletionSource<bool>? _pullGate;
        private TaskCompletionSource<bool>? _pullBlockedSignal;

        /// <summary>Test hook: makes every <c>ImageDeleteAsync</c> call throw a transport-level
        /// "interrupted" <see cref="XpcException"/> (mapped to <c>RuntimeErrorKind.Unavailable</c>),
        /// so <c>RemoveImageAsync</c> takes its apiserver-unavailable CLI fallback — Apple's
        /// in-binary sweep, the residual cider-ede.41 documents.</summary>
        public bool DeleteUnavailable { get; set; }

        public void ArmPullGate()
        {
            _pullGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pullBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilPullBlockedAsync() => _pullBlockedSignal!.Task;

        public void ReleasePull() => _pullGate?.TrySetResult(true);

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
            if (DeleteUnavailable)
            {
                throw XpcException.Interrupted("simulated apiserver disconnect");
            }

            Record($"ImageDeleteAsync:{reference}:{garbageCollect}");
            return Task.CompletedTask;
        }
    }
}
