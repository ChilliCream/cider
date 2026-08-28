using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// cider-eqa.2: since the store-index repairs left vminit absent, every XPC <c>containerCreate</c>
/// degraded to the whole-create CLI path because <see cref="InitImageResolver"/> could only throw
/// <c>Unavailable</c> when the init image was missing locally. The historical blocker (cider-ede.10
/// predating pull support) is gone since 540c493, so the resolver now pulls the absent init image
/// itself over the SAME XPC <c>imagePull</c> route <c>XpcContainerRuntime.PullImageAsync</c> uses for
/// normal images — entering the runtime's <see cref="BlobSweepGate"/> as a write first (cider-ede.31)
/// — and only a failure of that pull still falls back to the CLI (via the same
/// <see cref="RuntimeErrorKind.Unavailable"/> classification <c>CreateContainerAsync</c>'s catch arm
/// turns into the throttled <c>WarnFallback</c> log + CLI delegate, the arm
/// <c>XpcContainerRuntimeGuardNoNetworkFallbackTests</c> exercises).
///
/// Drives the resolver directly through its test seams: the <c>referenceResolver</c> constructor
/// override (no <c>container</c> binary involved) and a recording <see cref="ImagesServiceClient"/>
/// subclass (the same injection shape <c>XpcContainerRuntimeRemoveImageTests</c> uses) — no live
/// apiserver anywhere.
/// </summary>
public sealed class InitImageResolverTests
{
    private const string VminitRef = "ghcr.io/apple/containerization/vminit:0.41.0";

    private static InitImageResolver NewResolver(RecordingImagesClient images, BlobSweepGate? gate = null) => new(
        new AppleContainerOptions(),
        images,
        gate ?? new BlobSweepGate(),
        NullLogger.Instance,
        referenceResolver: _ => Task.FromResult(VminitRef));

    private static ImageDescription VminitDescription() => new()
    {
        Reference = VminitRef,
        Descriptor = new Descriptor
        {
            MediaType = "application/vnd.oci.image.index.v1+json",
            Digest = "sha256:" + new string('2', 64),
            Size = 42,
        },
    };

    /// <summary>The ticket's primary unit proof: with vminit absent from <c>imageList</c>, resolving
    /// triggers exactly ONE XPC pull, unpacks the pulled description, and succeeds — no
    /// <see cref="RuntimeException"/> escapes, so <c>CreateContainerAsync</c> proceeds over XPC and
    /// its CLI-fallback warn is never reached. A second resolve is served from the cache with no
    /// further images-service calls.</summary>
    [Fact]
    public async Task ResolveAsync_VminitAbsent_PullsExactlyOnceOverXpc_ThenProceeds()
    {
        var images = new RecordingImagesClient
        {
            ListResult = [], // absent: the post-repair store state (coredns + postgres only)
            PullResult = VminitDescription(),
            SnapshotMissingUntilUnpacked = true,
        };
        var resolver = NewResolver(images);

        var reference = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal(VminitRef, reference);
        Assert.Equal(1, images.PullCalls);
        Assert.Equal(1, images.UnpackCalls);

        // Cached for the runtime's lifetime: no second pull, list, or unpack.
        await resolver.ResolveAsync(CancellationToken.None);
        Assert.Equal(1, images.PullCalls);
        Assert.Equal(1, images.ListCalls);
        Assert.Equal(1, images.UnpackCalls);
    }

    /// <summary>When vminit is already present locally, the pull route must not be touched at all —
    /// the pre-eqa.2 match + snapshot-ensure sequence is unchanged.</summary>
    [Fact]
    public async Task ResolveAsync_VminitPresent_NeverPulls()
    {
        var images = new RecordingImagesClient { ListResult = [VminitDescription()] };
        var resolver = NewResolver(images);

        var reference = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal(VminitRef, reference);
        Assert.Equal(0, images.PullCalls);
    }

    /// <summary>Negative control ("break the ensure, watch the fallback warn return"): when the XPC
    /// pull itself fails, the resolver throws <see cref="RuntimeErrorKind.Unavailable"/> carrying the
    /// pull failure as the reason — exactly the exception shape
    /// <c>XpcContainerRuntime.CreateContainerAsync</c>'s <c>RuntimeException</c>-Unavailable catch arm
    /// converts into the <c>WarnFallback("containerCreate", …)</c> log plus the last-resort CLI
    /// delegate (arm behavior proven by <c>XpcContainerRuntimeGuardNoNetworkFallbackTests</c>'s
    /// site-3 pair). A later retry must attempt the pull again rather than serve a poisoned cache.</summary>
    [Fact]
    public async Task ResolveAsync_PullFails_ThrowsUnavailableWithReason_AndRetriesNextCall()
    {
        var images = new RecordingImagesClient
        {
            ListResult = [],
            PullFailure = XpcException.Interrupted("simulated apiserver disconnect mid-pull"),
        };
        var resolver = NewResolver(images);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => resolver.ResolveAsync(CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.Unavailable, ex.Kind);
        Assert.Contains("xpc pull failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("simulated apiserver disconnect mid-pull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("falling back to the CLI", ex.Message, StringComparison.Ordinal);

        // Only a fully successful run is cached as ensured: heal the pull and the next resolve
        // completes over XPC.
        images.PullFailure = null;
        images.PullResult = VminitDescription();
        var reference = await resolver.ResolveAsync(CancellationToken.None);
        Assert.Equal(VminitRef, reference);
        Assert.Equal(2, images.PullCalls);
    }

    /// <summary>cider-ede.31: the init-image pull writes blobs exactly like a normal pull, so it must
    /// enter the runtime's <see cref="BlobSweepGate"/> as a write — a sweep already holding the gate
    /// (the apiserver-unavailable CLI delete fallback, the one sweep left on this transport) must
    /// block the pull from starting until it completes.</summary>
    [Fact]
    public async Task ResolveAsync_PullWaitsForAnInFlightSweep()
    {
        var images = new RecordingImagesClient
        {
            ListResult = [],
            PullResult = VminitDescription(),
        };
        var gate = new BlobSweepGate();
        var resolver = NewResolver(images, gate);

        var sweep = await gate.EnterSweepAsync(CancellationToken.None);
        var resolveTask = resolver.ResolveAsync(CancellationToken.None);

        var racedAhead = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(resolveTask, racedAhead);
        Assert.Equal(0, images.PullCalls);

        await sweep.DisposeAsync();
        Assert.Equal(VminitRef, await resolveTask);
        Assert.Equal(1, images.PullCalls);
    }

    /// <summary>Counts calls; serves canned results; can fail the pull
    /// (<see cref="PullFailure"/>) or report the snapshot missing until <c>imageUnpack</c> has run
    /// (<see cref="SnapshotMissingUntilUnpacked"/>). Never talks to a real apiserver.</summary>
    private sealed class RecordingImagesClient()
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        public List<ImageDescription> ListResult { get; set; } = [];
        public ImageDescription? PullResult { get; set; }
        public XpcException? PullFailure { get; set; }
        public bool SnapshotMissingUntilUnpacked { get; set; }

        public int ListCalls { get; private set; }
        public int PullCalls { get; private set; }
        public int UnpackCalls { get; private set; }

        public override Task<List<ImageDescription>> ImageListAsync(CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult(ListResult);
        }

        public override Task<ImageDescription> ImagePullAsync(string reference, Platform? platform, XpcObject? progressEndpoint, CancellationToken ct)
        {
            PullCalls++;
            if (PullFailure is { } failure)
            {
                return Task.FromException<ImageDescription>(failure);
            }

            return Task.FromResult(PullResult ?? throw new InvalidOperationException("PullResult not seeded"));
        }

        public override Task ImageUnpackAsync(ImageDescription image, Platform platform, CancellationToken ct, XpcObject? progressEndpoint = null)
        {
            UnpackCalls++;
            return Task.CompletedTask;
        }

        public override Task<Filesystem> SnapshotGetAsync(ImageDescription image, Platform platform, CancellationToken ct)
        {
            if (SnapshotMissingUntilUnpacked && UnpackCalls == 0)
            {
                throw XpcException.ApiServer("notFound", $"no snapshot for {image.Reference}");
            }

            return Task.FromResult(new Filesystem
            {
                Type = new FsType { Virtiofs = new EmptyPayload() },
                Source = "/tmp/fake-snapshot",
                Destination = "/",
                Options = [],
            });
        }
    }
}
