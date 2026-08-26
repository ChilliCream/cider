using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Cider.Tests.Fakes;
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

        await runtime.PruneImagesAsync(CancellationToken.None);

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

        var sweepTask = runtime.PruneImagesAsync(CancellationToken.None);

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

        var sweepTask = runtime.PruneImagesAsync(CancellationToken.None);
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

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            new FakeContainerRuntime(), apiserver, images, capabilities, options, NullLogger<XpcContainerRuntime>.Instance, imagesClient);
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
