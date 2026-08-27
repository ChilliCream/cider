using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// cider-i1v: <c>GuardNoNetworkFallback</c> (<c>XpcContainerRuntime.Create.cs</c>) is the only thing
/// stopping a <c>network_mode: none</c> container from silently getting the default network when
/// <c>CreateContainerAsync</c> falls back from the apiserver to the CLI runtime — Apple's
/// <c>container create</c> has no flag for zero attachments, and omitting <c>--network</c> entirely
/// attaches the default network instead. It is called at all three of that method's fallback sites,
/// and until this file none of the three had any coverage at all (the one test that ever exercised
/// this invariant, <c>AppleContainerRuntimeNoNetworkGuardTests.cs</c>, tested a *different* guard — the
/// CLI-transport copy removed in 39f6873 — and was deleted with it).
///
/// Each fallback site is exercised by a distinct trigger:
///  1. No merged <see cref="Cider.Core.Runtime.ContainerSpec.Entrypoint"/> — checked before any
///     client-side work, so no XPC call is ever made.
///  2. A transport-level <see cref="XpcException"/> classified
///     <see cref="RuntimeErrorKind.Unavailable"/> — modelled by making the fake
///     <see cref="ImagesServiceClient"/>'s <c>imageList</c> throw one directly, the same shape a real
///     apiserver disconnect mid-create would produce (verified live against a genuinely nonexistent
///     mach service before writing this: <c>XpcClient.SendAsync</c> to a service name nothing
///     registers throws <c>XpcException{ErrorClass=Transport, Code=connectionInvalid}</c> in single-digit
///     milliseconds, which <see cref="XpcErrorMapper.ToRuntimeErrorKind"/> classifies
///     <see cref="RuntimeErrorKind.Unavailable"/> — not a 60s timeout hang).
///  3. A client-side <see cref="RuntimeException"/> already classified
///     <see cref="RuntimeErrorKind.Unavailable"/> — <see cref="ImageSnapshotEnsurer.EnsureAsync"/>'s own
///     "reference not found in imageList" case, reached with no XPC failure needed at all.
///
/// Every trigger gets a paired negative: the same failure with <see cref="Cider.Core.Runtime.ContainerSpec.Networks"/>
/// non-empty must NOT throw and must reach the CLI fallback — proving the guard fires on "no networks
/// requested", not on "a fallback happened", which is the overreach this file must also rule out.
/// Drives <see cref="XpcContainerRuntime"/> through the same test-only <c>ImagesServiceClient</c>
/// injection seam <see cref="XpcContainerRuntimeListImagesToleranceTests"/> uses; the <c>apiserver</c>
/// <see cref="XpcClient"/> is a real, unconnected client (never dialed in triggers 1 and 3, and only
/// ever dialed — and only ever failing fast — in trigger 2's own fake).
/// </summary>
public sealed class XpcContainerRuntimeGuardNoNetworkFallbackTests
{
    private const string Image = "docker.io/library/alpine:3.19";
    private static readonly ImageDescription MatchingImage = new()
    {
        Reference = Image,
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:" + new string('1', 64), Size = 32 },
    };

    // ---- site 1: no merged Entrypoint ------------------------------------------------------

    [Fact]
    public async Task CreateContainerAsync_NoEntrypoint_NoNetworks_ThrowsNotSupported_WithoutCallingCli()
    {
        var cli = new FakeContainerRuntime();
        var runtime = NewRuntime(cli, new StubImagesServiceClient());
        using var _ = runtime;

        var spec = NewSpec(entrypoint: null, networks: []);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => runtime.CreateContainerAsync(spec, CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.NotSupported, ex.Kind);
        Assert.DoesNotContain(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    /// <summary>Negative control: the same no-Entrypoint fallback with a real network list must reach
    /// the CLI exactly as it did before this guard existed — the guard must not block an ordinary
    /// fallback that never asked for <c>network_mode: none</c>.</summary>
    [Fact]
    public async Task CreateContainerAsync_NoEntrypoint_WithNetworks_FallsBackToCli_GuardDoesNotFire()
    {
        var cli = new FakeContainerRuntime();
        var runtime = NewRuntime(cli, new StubImagesServiceClient());
        using var _ = runtime;

        var spec = NewSpec(entrypoint: null, networks: ["default"]);

        await runtime.CreateContainerAsync(spec, CancellationToken.None);

        Assert.Contains(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    // ---- site 2: apiserver-unavailable XpcException mid-create -----------------------------

    [Fact]
    public async Task CreateContainerAsync_XpcUnavailableMidCreate_NoNetworks_ThrowsNotSupported_WithoutCallingCli()
    {
        var cli = new FakeContainerRuntime();
        var images = new StubImagesServiceClient { ImageListFailure = XpcException.Interrupted("simulated apiserver disconnect") };
        var runtime = NewRuntime(cli, images);
        using var _ = runtime;

        // A merged Entrypoint so the create takes the try path (site 2's catch arm), not site 1's.
        var spec = NewSpec(entrypoint: "/bin/sh", networks: []);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => runtime.CreateContainerAsync(spec, CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.NotSupported, ex.Kind);
        Assert.DoesNotContain(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    /// <summary>Negative control for site 2: the identical apiserver disconnect, with a real network
    /// list, must still fall back to the CLI instead of throwing.</summary>
    [Fact]
    public async Task CreateContainerAsync_XpcUnavailableMidCreate_WithNetworks_FallsBackToCli_GuardDoesNotFire()
    {
        var cli = new FakeContainerRuntime();
        var images = new StubImagesServiceClient { ImageListFailure = XpcException.Interrupted("simulated apiserver disconnect") };
        var runtime = NewRuntime(cli, images);
        using var _ = runtime;

        var spec = NewSpec(entrypoint: "/bin/sh", networks: ["default"]);

        await runtime.CreateContainerAsync(spec, CancellationToken.None);

        Assert.Contains(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    // ---- site 3: client-side RuntimeException(Unavailable) precondition failure ------------

    [Fact]
    public async Task CreateContainerAsync_ClientSideUnavailablePrecondition_NoNetworks_ThrowsNotSupported_WithoutCallingCli()
    {
        var cli = new FakeContainerRuntime();
        // No matching image in imageList -> ImageSnapshotEnsurer.EnsureAsync throws
        // RuntimeException(Unavailable) itself, with no XPC failure involved at all.
        var images = new StubImagesServiceClient { Descriptions = [] };
        var runtime = NewRuntime(cli, images);
        using var _ = runtime;

        var spec = NewSpec(entrypoint: "/bin/sh", networks: []);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => runtime.CreateContainerAsync(spec, CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.NotSupported, ex.Kind);
        Assert.DoesNotContain(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    /// <summary>Negative control for site 3: the identical unresolved-reference precondition failure,
    /// with a real network list, must still fall back to the CLI instead of throwing.</summary>
    [Fact]
    public async Task CreateContainerAsync_ClientSideUnavailablePrecondition_WithNetworks_FallsBackToCli_GuardDoesNotFire()
    {
        var cli = new FakeContainerRuntime();
        var images = new StubImagesServiceClient { Descriptions = [] };
        var runtime = NewRuntime(cli, images);
        using var _ = runtime;

        var spec = NewSpec(entrypoint: "/bin/sh", networks: ["default"]);

        await runtime.CreateContainerAsync(spec, CancellationToken.None);

        Assert.Contains(cli.Calls, c => c.StartsWith("CreateContainerAsync:", StringComparison.Ordinal));
    }

    private static ContainerSpec NewSpec(string? entrypoint, IReadOnlyList<string> networks) => new()
    {
        RuntimeId = "cider-i1v-" + Guid.NewGuid().ToString("N")[..12],
        Image = Image,
        Entrypoint = entrypoint,
        Networks = networks,
    };

    private static XpcContainerRuntime NewRuntime(FakeContainerRuntime cli, ImagesServiceClient imagesClient)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.i1v.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.i1v.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            cli, apiserver, images, capabilities, options, NullLogger<XpcContainerRuntime>.Instance, imagesClient);
    }

    /// <summary>Answers <c>imageList</c> with <see cref="Descriptions"/> (default: one entry matching
    /// <see cref="Image"/>, so <see cref="ImageSnapshotEnsurer"/> succeeds and site 2/3's tests can
    /// choose exactly one failure mode each); <see cref="ImageListFailure"/>, when set, makes
    /// <c>imageList</c> throw that exception instead of answering at all — site 2's trigger. Every
    /// other route this fake does not override falls through to <see cref="ImagesServiceClient"/>'s
    /// real implementation, which would need a live connection — none of these tests reach one,
    /// since a matching image's own <c>snapshotGet</c> is never exercised by <c>CreateContainerAsync</c>
    /// before the point each test's failure fires.</summary>
    private sealed class StubImagesServiceClient()
        : ImagesServiceClient(new XpcClient("com.apple.container.test.i1v.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        public List<ImageDescription> Descriptions { get; init; } = [MatchingImage];

        public XpcException? ImageListFailure { get; init; }

        public override Task<List<ImageDescription>> ImageListAsync(CancellationToken ct) =>
            ImageListFailure is { } failure ? Task.FromException<List<ImageDescription>>(failure) : Task.FromResult(Descriptions);
    }
}
