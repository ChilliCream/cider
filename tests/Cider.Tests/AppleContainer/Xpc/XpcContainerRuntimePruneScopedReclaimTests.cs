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
/// cider-ehn: <c>PruneImagesAsync</c>'s scoped fallback — entered only when the whole-store sweep
/// (<c>imageCleanupOrphanedBlobs</c>) fails on a pre-existing dangling content reference elsewhere in
/// the store (the same cider-bci/cider-ede.24 corruption class <see cref="XpcContainerRuntimeRemoveImageTests"/>
/// and <see cref="XpcContainerRuntimeListImagesToleranceTests"/> already cover for the sweep itself and
/// for <c>ListImagesAsync</c>). Drives <see cref="XpcContainerRuntime"/> through its test-only
/// constructor against a fake <see cref="ImagesServiceClient"/> with real, on-disk OCI JSON blobs, no
/// live apiserver connection involved.
///
/// The safety-rule test (<see cref="PruneImagesAsync_ScopedFallback_AbortsWithZeroContentDeleteCalls_WhenARemainingImagesManifestCannotBeRead"/>)
/// is the one the task's own description calls out as never to be weakened — a remaining image whose
/// manifest cannot be read must abort the whole reclaim, not just narrow it.
/// </summary>
public sealed class XpcContainerRuntimePruneScopedReclaimTests
{
    private static readonly string DanglingDigest = RepeatDigest('9');

    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_DeletesExactlyTheDeletedImagesConfigAndLayerDigests()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');
        var layer1Digest = RepeatDigest('4');
        var layer2Digest = RepeatDigest('5');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest, layer1Digest, layer2Digest),
        };

        // No remaining images at all: every candidate digest survives.
        var fake = new FakeImagesServiceClient(
            cleanupFailure: DanglingContentFailure(),
            remaining: [],
            resolvable: resolvable);
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        var deleteCall = Assert.Single(fake.ContentDeleteCalls);
        Assert.Equal(
            new[] { configDigest, layer1Digest, layer2Digest }.OrderBy(d => d, StringComparer.Ordinal),
            deleteCall.OrderBy(d => d, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_ExcludesADigestStillReferencedByARemainingImage()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');
        var uniqueLayerDigest = RepeatDigest('4');
        var sharedLayerDigest = RepeatDigest('5');

        var remainingIndexDigest = RepeatDigest('6');
        var remainingManifestDigest = RepeatDigest('7');
        var remainingConfigDigest = RepeatDigest('8');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest, uniqueLayerDigest, sharedLayerDigest),
            [remainingIndexDigest] = tempDir.WriteIndex("remaining-index", remainingManifestDigest),
            // The remaining image's own manifest also names sharedLayerDigest — a base layer both
            // images happen to share — so it must survive even though the deleted image named it too.
            [remainingManifestDigest] = tempDir.WriteManifest("remaining-manifest", remainingConfigDigest, sharedLayerDigest),
        };

        var remaining = new List<ImageDescription> { Description("docker.io/library/kept:latest", remainingIndexDigest) };
        var fake = new FakeImagesServiceClient(DanglingContentFailure(), remaining, resolvable);
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        var deleteCall = Assert.Single(fake.ContentDeleteCalls);
        Assert.Equal(
            new[] { configDigest, uniqueLayerDigest }.OrderBy(d => d, StringComparer.Ordinal),
            deleteCall.OrderBy(d => d, StringComparer.Ordinal));
        Assert.DoesNotContain(sharedLayerDigest, deleteCall);
        Assert.DoesNotContain(remainingConfigDigest, deleteCall);
    }

    /// <summary>
    /// THE SAFETY-RULE TEST — task cider-ehn's own binding ruling: an unreadable remaining manifest
    /// must abort the whole reclaim, with zero <c>contentDelete</c> calls, even though every candidate
    /// digest was otherwise fully accounted for. Never weaken this to "prove non-reference only
    /// against readable entries".
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_AbortsWithZeroContentDeleteCalls_WhenARemainingImagesManifestCannotBeRead()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');
        var layerDigest = RepeatDigest('4');

        var unreadableRemainingIndexDigest = RepeatDigest('6');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest, layerDigest),

            // Deliberately absent: the remaining image's own index digest resolves to nothing (the
            // exact cider-ede.24 corruption shape — the dangling entry state.json still names).
        };

        var remaining = new List<ImageDescription>
        {
            Description("docker.io/library/dangling:latest", unreadableRemainingIndexDigest),
        };
        var fake = new FakeImagesServiceClient(DanglingContentFailure(), remaining, resolvable);
        var logger = new RecordingLogger<XpcContainerRuntime>();
        var runtime = NewRuntime(fake, logger);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        Assert.Empty(fake.ContentDeleteCalls);
        Assert.DoesNotContain(fake.Calls, c => c == "ContentDeleteAsync");

        // Both the original sweep failure and the scoped-fallback abort get their own Warning.
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("deleting nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// REGRESSION TEST for the "parsed but empty" hole in <see cref="XpcContainerRuntime.CollectManifestDigestsAsync"/>:
    /// a remaining image whose index descriptor resolves straight to a <b>bare manifest JSON</b>
    /// (<c>{schemaVersion, config, layers}</c>, no <c>manifests[]</c> wrapper at all) deserializes as an
    /// <see cref="OciIndex"/> with a null <c>Manifests</c> — before the fix that was accepted as "index
    /// resolved, zero real variants to walk, so <c>complete</c> stays <c>true</c>", handing back an empty
    /// digest set as if the remaining image had been fully, positively accounted for. That let a layer
    /// this remaining image actually shares with the deleted image survive candidacy only by chance (it
    /// was never subtracted from <c>keep</c> at all) — this test pins that layer to actually being kept,
    /// which fails on today's code once the sharing is real. Must fail before the fix, pass after.
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_ExcludesASharedLayer_WhenARemainingImagesDescriptorIsABareManifest()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');
        var uniqueLayerDigest = RepeatDigest('4');
        var sharedLayerDigest = RepeatDigest('5');

        // The remaining image's own top-level descriptor digest resolves directly to a bare manifest
        // (config+layers, no index wrapper) — a legitimately single-manifest image, the shape
        // CollectBareManifestDigestsAsync exists to still walk and still count as proof.
        var remainingBareManifestDigest = RepeatDigest('6');
        var remainingConfigDigest = RepeatDigest('8');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest, uniqueLayerDigest, sharedLayerDigest),
            [remainingBareManifestDigest] = tempDir.WriteManifest("remaining-bare-manifest", remainingConfigDigest, sharedLayerDigest),
        };

        var remaining = new List<ImageDescription> { Description("docker.io/library/kept:latest", remainingBareManifestDigest) };
        var fake = new FakeImagesServiceClient(DanglingContentFailure(), remaining, resolvable);
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        var deleteCall = Assert.Single(fake.ContentDeleteCalls);
        Assert.Equal(
            new[] { configDigest, uniqueLayerDigest }.OrderBy(d => d, StringComparer.Ordinal),
            deleteCall.OrderBy(d => d, StringComparer.Ordinal));
        Assert.DoesNotContain(sharedLayerDigest, deleteCall);
        Assert.DoesNotContain(remainingConfigDigest, deleteCall);
    }

    /// <summary>
    /// A remaining image whose index itself resolves fine, but whose per-platform manifest blob is
    /// absent from the store (a narrower corruption shape than the whole index being unreadable) must
    /// still abort the whole reclaim per the safety rule — <see cref="CollectManifestDigestsAsync"/>'s
    /// <c>complete</c> tracking already covers this branch; pinned here as explicit coverage per the
    /// amended-report finding that only the "index is null" abort branch had a test.
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_AbortsWithZeroContentDeleteCalls_WhenARemainingImagesManifestBlobIsAbsent()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');
        var layerDigest = RepeatDigest('4');

        var remainingIndexDigest = RepeatDigest('6');
        var remainingManifestDigest = RepeatDigest('7');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest, layerDigest),
            [remainingIndexDigest] = tempDir.WriteIndex("remaining-index", remainingManifestDigest),

            // Deliberately absent: remainingManifestDigest never resolves, even though the index
            // naming it did.
        };

        var remaining = new List<ImageDescription>
        {
            Description("docker.io/library/kept:latest", remainingIndexDigest),
        };
        var fake = new FakeImagesServiceClient(DanglingContentFailure(), remaining, resolvable);
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        Assert.Empty(fake.ContentDeleteCalls);
    }

    /// <summary>
    /// <c>imageList</c> failing outright must abort the scoped reclaim the same as any other
    /// enumeration failure — zero <c>contentDelete</c> calls, and (this is the regression half, finding
    /// 2) <see cref="XpcContainerRuntime.PruneImagesAsync"/> itself must not throw even when the failure
    /// is not an <see cref="XpcException"/>, since <c>TryScopedReclaimAsync</c>'s own doc comment already
    /// promised to swallow "any other unexpected failure while gathering that proof".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PruneImagesAsync_ScopedFallback_AbortsWithZeroContentDeleteCalls_WhenImageListFails(bool xpcException)
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest),
        };

        Exception listFailure = xpcException
            ? XpcException.ApiServer("internalError", "images service unavailable")
            : new InvalidOperationException("malformed imageList reply");
        var fake = new FakeImagesServiceClient(DanglingContentFailure(), [], resolvable, listFailure);
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        await runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);

        Assert.Empty(fake.ContentDeleteCalls);
    }

    /// <summary>
    /// Fix direction §4: the scoped reclaim runs under the same <see cref="BlobSweepGate"/> exclusion
    /// as the whole-store sweep it falls back from — a concurrent pull must not be able to start (and
    /// so must not be able to write blobs) while the scoped <c>contentDelete</c> is still in flight.
    /// </summary>
    [Fact]
    public async Task PruneImagesAsync_ScopedFallback_HoldsTheBlobSweepGateAcrossTheContentDeleteCall()
    {
        using var tempDir = new TempDir();

        var deletedIndexDigest = RepeatDigest('1');
        var deletedManifestDigest = RepeatDigest('2');
        var configDigest = RepeatDigest('3');

        var resolvable = new Dictionary<string, string?>
        {
            [deletedIndexDigest] = tempDir.WriteIndex("deleted-index", deletedManifestDigest),
            [deletedManifestDigest] = tempDir.WriteManifest("deleted-manifest", configDigest),
        };

        var fake = new FakeImagesServiceClient(DanglingContentFailure(), [], resolvable);
        fake.ArmContentDeleteGate();
        var runtime = NewRuntime(fake);
        using var _ = runtime;

        var pruneTask = runtime.PruneImagesAsync([deletedIndexDigest], CancellationToken.None);
        await fake.WaitUntilContentDeleteBlockedAsync();

        var progress = new Progress<ProgressEvent>();
        var pullTask = runtime.PullImageAsync("docker.io/library/redis:8.6", null, null, progress, CancellationToken.None);

        var racedAhead = await Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(pullTask, racedAhead);
        Assert.False(pullTask.IsCompleted, "a concurrent pull must wait for the scoped reclaim's contentDelete to finish");

        fake.ReleaseContentDelete();
        await pruneTask;
        await pullTask;
    }

    private static XpcException DanglingContentFailure() =>
        XpcException.ApiServer("internalError", $"content with digest {DanglingDigest}");

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient) =>
        NewRuntime(imagesClient, NullLogger<XpcContainerRuntime>.Instance);

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient, ILogger<XpcContainerRuntime> logger)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            new FakeContainerRuntime(), apiserver, images, capabilities, options, logger, imagesClient);
    }

    private static string RepeatDigest(char c) => "sha256:" + new string(c, 64);

    private static ImageDescription Description(string reference, string digest) => new()
    {
        Reference = reference,
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = digest, Size = 374 },
    };

    /// <summary>A per-test temp directory holding real, resolvable OCI index/manifest blobs — the
    /// same shape <c>XpcContainerRuntimeListImagesToleranceTests.TempDir</c> uses.</summary>
    private sealed class TempDir : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("cider-ehn-tests-").FullName;

        public string WriteIndex(string name, string manifestDigest)
        {
            var path = Path.Combine(_dir, $"{name}.json");
            var json = "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":[" +
                "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\"" + manifestDigest +
                "\",\"size\":528,\"platform\":{\"architecture\":\"amd64\",\"os\":\"linux\"}}]}";
            File.WriteAllText(path, json);
            return path;
        }

        public string WriteManifest(string name, string configDigest, params string[] layerDigests)
        {
            var layers = string.Join(",", layerDigests.Select(d =>
                "{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"" + d + "\",\"size\":100}"));
            var path = Path.Combine(_dir, $"{name}.json");
            var json = "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"," +
                "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\"" + configDigest + "\",\"size\":200}," +
                "\"layers\":[" + layers + "]}";
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Overrides every route <see cref="XpcContainerRuntime.PruneImagesAsync"/>'s scoped
    /// fallback needs: <c>imageCleanupOrphanedBlobs</c> always fails with <paramref name="cleanupFailure"/>
    /// (the whole-store sweep never succeeds in this file — it exists to drive the fallback);
    /// <c>imageList</c> answers <paramref name="remaining"/>; <c>contentGet</c> resolves through
    /// <paramref name="resolvable"/>, falling back to <c>null</c> ("nothing recovered") for any digest
    /// not listed there, the same <c>notFound</c>/missing-on-disk shape
    /// <see cref="XpcContainerRuntimeListImagesToleranceTests.FakeImagesServiceClient"/> already
    /// exercises; <c>contentDelete</c> records the digests it was asked to delete.</summary>
    private sealed class FakeImagesServiceClient(
        XpcException cleanupFailure, List<ImageDescription> remaining, Dictionary<string, string?> resolvable, Exception? listFailure = null)
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        private readonly object _sync = new();
        public List<string> Calls { get; } = [];
        public List<List<string>> ContentDeleteCalls { get; } = [];

        private TaskCompletionSource<bool>? _contentDeleteGate;
        private TaskCompletionSource<bool>? _contentDeleteBlockedSignal;

        public void ArmContentDeleteGate()
        {
            _contentDeleteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _contentDeleteBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilContentDeleteBlockedAsync() => _contentDeleteBlockedSignal!.Task;

        public void ReleaseContentDelete() => _contentDeleteGate?.TrySetResult(true);

        private void Record(string call)
        {
            lock (_sync)
            {
                Calls.Add(call);
            }
        }

        public override Task<ImageDescription> ImagePullAsync(string reference, Platform? platform, XpcObject? progressEndpoint, CancellationToken ct)
        {
            Record($"ImagePullAsync:{reference}");
            return Task.FromResult(new ImageDescription
            {
                Reference = reference,
                Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:" + new string('a', 64), Size = 1 },
            });
        }

        public override Task ImageUnpackAsync(ImageDescription image, Platform platform, CancellationToken ct, XpcObject? progressEndpoint = null)
        {
            Record("ImageUnpackAsync");
            return Task.CompletedTask;
        }

        public override Task<(IReadOnlyList<string> Digests, ulong ImageSize)> ImageCleanupOrphanedBlobsAsync(CancellationToken ct)
        {
            Record("ImageCleanupOrphanedBlobsAsync");
            throw cleanupFailure;
        }

        public override Task<List<ImageDescription>> ImageListAsync(CancellationToken ct)
        {
            Record("ImageListAsync");
            if (listFailure is not null)
            {
                throw listFailure;
            }

            return Task.FromResult(remaining);
        }

        public override Task<string?> ContentGetAsync(string digest, CancellationToken ct)
        {
            Record($"ContentGetAsync:{digest}");
            return Task.FromResult(resolvable.GetValueOrDefault(digest));
        }

        public override async Task<(IReadOnlyList<string> Digests, ulong ImageSize)> ContentDeleteAsync(IReadOnlyList<string> digests, CancellationToken ct)
        {
            var snapshot = digests.ToList();
            lock (_sync)
            {
                ContentDeleteCalls.Add(snapshot);
            }

            Record("ContentDeleteAsync");

            if (_contentDeleteGate is not null)
            {
                _contentDeleteBlockedSignal!.TrySetResult(true);
                await _contentDeleteGate.Task.ConfigureAwait(false);
            }

            return (snapshot, (ulong)snapshot.Count * 100);
        }
    }

    /// <summary>Captures every log entry made against it — the same shape the other
    /// <c>XpcContainerRuntime*Tests</c> files use.</summary>
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
}
