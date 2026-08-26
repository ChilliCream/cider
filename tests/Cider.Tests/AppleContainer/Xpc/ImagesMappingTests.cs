using Cider.AppleContainer.ContentStore;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerRuntime"/>'s image grouping/mapping (task cider-ede.10, fix direction §1),
/// exercised as pure functions over hand-built fixtures — no live apiserver, no <c>contentGet</c>
/// involved. Mirrors a real image's on-disk shape: an index blob naming one manifest per platform
/// (plus, for a buildkit-built image, an <c>unknown/unknown</c> attestation manifest to filter out —
/// verified live against this machine's own content store, docs/spikes/xpc/02-apiserver-xpc-protocol.md
/// §6), each manifest naming a config blob.
/// </summary>
public class ImagesMappingTests
{
    private const string Digest = "sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc";
    private const string ManifestDigest = "sha256:ca84f5d75d13229d1e104c583f69bcc68d81fbf47707dfb07055c777e0f1ad11";
    private const string ConfigDigest = "sha256:83bdc1f5030f20f10d31e0f88afb4b0f048aaa077663ae6079e9c1b60fc433e4";
    private const string AttestationManifestDigest = "sha256:00000000000000000000000000000000000000000000000000000000000000";

    // ---- GroupByDigest -----------------------------------------------------------------------

    [Fact]
    public void GroupByDigest_merges_two_references_sharing_one_digest()
    {
        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/alpine:3.20", Digest),
            Description("ad-4rs/alias:1", Digest),
        };

        var groups = XpcContainerRuntime.GroupByDigest(descriptions);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        var references = XpcContainerRuntime.References(group);
        Assert.Equal(["docker.io/library/alpine:3.20", "ad-4rs/alias:1"], references);
    }

    [Fact]
    public void GroupByDigest_keeps_different_digests_apart()
    {
        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/alpine:3.20", Digest),
            Description("docker.io/library/busybox:1", "sha256:" + new string('1', 64)),
        };

        var groups = XpcContainerRuntime.GroupByDigest(descriptions);

        Assert.Equal(2, groups.Count);
    }

    // ---- ImageSnapshotEnsurer.Match: annotation-preferring reference resolution -------------------

    [Fact]
    public void Match_prefers_the_containerizationImageName_annotation_over_the_stored_Reference()
    {
        // A locally built image whose stored Reference (imageList's own field) differs from the
        // human-facing tag it was built/re-tagged with — that tag only survives as the index
        // descriptor's containerizationImageName annotation. InspectImageAsync and SaveImagesAsync
        // both now resolve a caller-given reference through this same Match, so they find it too,
        // instead of only matching an exact (and here, wrong) Reference string.
        var descriptions = new List<ImageDescription>
        {
            new()
            {
                Reference = "sha256:" + new string('4', 64),
                Descriptor = new Descriptor
                {
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Digest = Digest,
                    Size = 374,
                    Annotations = new Dictionary<string, string> { ["containerizationImageName"] = "myapp:local" },
                },
            },
            Description("docker.io/library/alpine:3.20", "sha256:" + new string('5', 64)),
        };

        var matched = ImageSnapshotEnsurer.Match(descriptions, "myapp:local");

        Assert.NotNull(matched);
        Assert.Same(descriptions[0], matched);

        // InspectImageAsync's own next step after Match: resolve the matched description back to its
        // digest group.
        var group = XpcContainerRuntime.GroupByDigest(descriptions).Find(g => g.Contains(matched));
        Assert.NotNull(group);
        Assert.Equal(Digest, group![0].Descriptor.Digest);
    }

    [Fact]
    public void Match_falls_back_to_exact_Reference_equality_when_no_annotation_matches()
    {
        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/alpine:3.20", Digest),
        };

        var matched = ImageSnapshotEnsurer.Match(descriptions, "docker.io/library/alpine:3.20");

        Assert.NotNull(matched);
        Assert.Same(descriptions[0], matched);
    }

    // ---- RealVariants: attestation filtering ------------------------------------------------------

    [Fact]
    public void RealVariants_filters_out_the_unknown_unknown_attestation_manifest()
    {
        var index = new OciIndex
        {
            Manifests =
            [
                new OciDescriptor { Digest = ManifestDigest, Platform = new OciPlatform { Os = "linux", Architecture = "arm64" } },
                new OciDescriptor { Digest = AttestationManifestDigest, Platform = new OciPlatform { Os = "unknown", Architecture = "unknown" } },
            ],
        };

        var variants = XpcContainerRuntime.RealVariants(index);

        var variant = Assert.Single(variants);
        Assert.Equal(ManifestDigest, variant.Digest);
    }

    [Fact]
    public void RealVariants_returns_empty_for_a_null_or_empty_index()
    {
        Assert.Empty(XpcContainerRuntime.RealVariants(null));
        Assert.Empty(XpcContainerRuntime.RealVariants(new OciIndex()));
    }

    // ---- ToRuntimeImage / ToRuntimeImageDetail -----------------------------------------------------

    [Fact]
    public void ToRuntimeImage_sums_the_full_getFullImageSize_formula_across_every_real_variant()
    {
        // getFullImageSize (Apple's ClientImage/ImageResource.swift) is descriptor.size +
        // config.size + sum(layers[].size) per variant, summed across every real variant — not
        // layers alone. The third variant's digest deliberately has no entry in `manifests` (a
        // manifest that failed to resolve, mirroring Apple's own `continue` on that failure) and
        // must still contribute exactly 0, not throw and not silently drop the other variants.
        var variants = new List<OciDescriptor>
        {
            new() { Digest = ManifestDigest, Size = 374, Platform = new OciPlatform { Os = "linux", Architecture = "arm64" } },
            new() { Digest = "sha256:" + new string('2', 64), Size = 375, Platform = new OciPlatform { Os = "linux", Architecture = "amd64" } },
            new() { Digest = "sha256:" + new string('3', 64), Size = 376, Platform = new OciPlatform { Os = "linux", Architecture = "386" } },
        };

        var manifests = new Dictionary<string, AppleOciManifest>(StringComparer.Ordinal)
        {
            [ManifestDigest] = new AppleOciManifest
            {
                Config = new OciDescriptor { Digest = ConfigDigest, Size = 1512 },
                Layers = [new OciDescriptor { Size = 4120486 }, new OciDescriptor { Size = 104 }],
            },
            ["sha256:" + new string('2', 64)] = new AppleOciManifest
            {
                Layers = [new OciDescriptor { Size = 1000 }],
            },
        };

        var configs = new Dictionary<string, AppleOciImageDocument>(StringComparer.Ordinal)
        {
            [ConfigDigest] = new AppleOciImageDocument
            {
                Created = new DateTimeOffset(2026, 8, 25, 19, 18, 4, TimeSpan.Zero),
                Config = new AppleOciConfig { Labels = new Dictionary<string, string> { ["a"] = "b" } },
            },
        };

        var image = XpcContainerRuntime.ToRuntimeImage(["alpine:3.22"], Digest, variants, manifests, configs);

        // cider-ger.19 orchestrator follow-up: Id is the preferred variant's manifest config digest
        // (the value that is stable across separate loads of identical content), not the group's raw
        // index digest — the same content-addressed derivation the CLI transport's own
        // AppleContainerRuntime.RecoverContentAddressedIdsAsync applies. The raw index digest survives
        // on IndexDigests, for the rmi/prune in-use join (ImageManager.IsBoundTo).
        Assert.Equal(ConfigDigest, image.Id);
        Assert.Equal([Digest], image.IndexDigests);
        Assert.Equal((374 + 1512 + 4120486 + 104) + (375 + 1000) + 0, image.Size);
        Assert.Equal(["linux/arm64", "linux/amd64", "linux/386"], image.Platforms);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 19, 18, 4, TimeSpan.Zero), image.Created);
        Assert.Equal("b", image.Labels["a"]);
    }

    [Fact]
    public void ToRuntimeImage_FallsBackToTheIndexDigest_WhenThePreferredVariantsManifestDidNotResolve()
    {
        // Store-miss recovery, same best-effort contract as the CLI transport's
        // RecoverContentAddressedIdsAsync: no entry for the variant's digest in `manifests` at all.
        var variants = new List<OciDescriptor>
        {
            new() { Digest = ManifestDigest, Size = 374, Platform = new OciPlatform { Os = "linux", Architecture = "arm64" } },
        };

        var image = XpcContainerRuntime.ToRuntimeImage(
            ["alpine:3.22"],
            Digest,
            variants,
            manifests: new Dictionary<string, AppleOciManifest>(StringComparer.Ordinal),
            configs: new Dictionary<string, AppleOciImageDocument>(StringComparer.Ordinal));

        Assert.Equal(Digest, image.Id);
        Assert.Equal([Digest], image.IndexDigests);
    }

    [Fact]
    public void ToRuntimeImageDetail_reports_the_honest_config_including_exposed_ports_and_volumes()
    {
        var variants = new List<OciDescriptor>
        {
            new() { Digest = ManifestDigest, Size = 374, Platform = new OciPlatform { Os = "linux", Architecture = "arm64" } },
        };

        var manifests = new Dictionary<string, AppleOciManifest>(StringComparer.Ordinal)
        {
            [ManifestDigest] = new AppleOciManifest
            {
                Config = new OciDescriptor { Digest = ConfigDigest, Size = 1512 },
                Layers = [new OciDescriptor { Size = 4120486 }, new OciDescriptor { Size = 104 }],
            },
        };

        var configs = new Dictionary<string, AppleOciImageDocument>(StringComparer.Ordinal)
        {
            [ConfigDigest] = new AppleOciImageDocument
            {
                Architecture = "arm64",
                Os = "linux",
                Created = new DateTimeOffset(2026, 8, 25, 19, 18, 4, TimeSpan.Zero),
                Author = "someone",
                Rootfs = new AppleOciRootFs { DiffIds = ["sha256:aaa", "sha256:bbb"] },
                History = [new AppleOciHistory { CreatedBy = "RUN echo hi", Comment = "buildkit.dockerfile.v0" }],
                Config = new AppleOciConfig
                {
                    // Verified live: 1.2.2's `container image inspect` drops these even when the real
                    // config carries them (docs/apple-container-notes.md's ExposedPorts probe) — the
                    // whole point of reading the config blob directly over XPC is that no such
                    // recovery hack is needed here.
                    ExposedPorts = new Dictionary<string, System.Text.Json.JsonElement> { ["5432/tcp"] = default },
                    Volumes = new Dictionary<string, System.Text.Json.JsonElement> { ["/data"] = default },
                },
            },
        };

        var detail = XpcContainerRuntime.ToRuntimeImageDetail(["postgres:18.3"], Digest, variants, manifests, configs);

        Assert.NotNull(detail);
        Assert.Equal(ConfigDigest, detail!.Id);
        Assert.Equal([Digest], detail.IndexDigests);
        Assert.Equal("arm64", detail.Architecture);
        Assert.Equal("linux", detail.Os);
        // Size uses the single preferred variant's full getFullImageSize formula (descriptor.size +
        // config.size + sum(layers[].size)) — the same variant.Size semantics RuntimeMapper.ToImageDetail
        // reads straight off the CLI transport's JSON row for the CLI transport's own Size.
        Assert.Equal(374 + 1512 + 4120486 + 104, detail.Size);
        Assert.Equal(["sha256:aaa", "sha256:bbb"], detail.Layers);
        // LayerSizes is the per-layer breakdown field, deliberately layers-only — not the total.
        Assert.Equal([4120486L, 104L], detail.LayerSizes);
        Assert.Equal("someone", detail.Author);
        // RepoDigests strips the tag, same as RuntimeMapper.RepoDigests for the CLI transport.
        Assert.Equal(["postgres@" + Digest], detail.RepoDigests);
        Assert.Single(detail.History);
        Assert.Equal("RUN echo hi", detail.History[0].CreatedBy);
        Assert.Contains("5432/tcp", detail.Config.ExposedPorts);
        Assert.Contains("/data", detail.Config.Volumes);
    }

    [Fact]
    public void ToRuntimeImageDetail_returns_null_when_there_is_no_real_variant()
    {
        var detail = XpcContainerRuntime.ToRuntimeImageDetail(
            ["alpine:3.22"],
            Digest,
            variants: [],
            manifests: new Dictionary<string, AppleOciManifest>(),
            configs: new Dictionary<string, AppleOciImageDocument>());

        Assert.Null(detail);
    }

    // ---- PickVariant ---------------------------------------------------------------------------

    [Fact]
    public void PickVariant_prefers_an_explicitly_requested_platform()
    {
        var variants = new List<OciDescriptor>
        {
            new() { Digest = "arm", Platform = new OciPlatform { Os = "linux", Architecture = "arm64" } },
            new() { Digest = "amd", Platform = new OciPlatform { Os = "linux", Architecture = "amd64" } },
        };

        var picked = XpcContainerRuntime.PickVariant(variants, "linux/amd64");

        Assert.Equal("amd", picked!.Digest);
    }

    [Fact]
    public void PickVariant_falls_back_to_the_first_real_variant_when_nothing_matches()
    {
        var variants = new List<OciDescriptor>
        {
            new() { Digest = "only", Platform = new OciPlatform { Os = "windows", Architecture = "arm" } },
        };

        var picked = XpcContainerRuntime.PickVariant(variants, null);

        Assert.Equal("only", picked!.Digest);
    }

    // ---- ToPullRuntimeException ------------------------------------------------------------------

    [Fact]
    public void ToPullRuntimeException_maps_a_registry_manifest_404_to_NotFound()
    {
        // Verified live against a real apiserver (1.3.0): pulling a nonexistent tag surfaces
        // ApiServer code "unknown" wrapping this exact HTTP-client message shape, not
        // ContainerizationError.notFound — the generic XpcErrorMapper table alone maps "unknown" to
        // RuntimeErrorKind.Internal (a 500), breaking ImageManager.PullAsync's "notFound before the
        // pull is under way answers a plain 404" contract unless this is sniffed here.
        var ex = XpcException.ApiServer(
            "unknown",
            "HTTP request to https://registry-1.docker.io/v2/library/alpine/manifests/9.99.99-does-not-exist " +
            "failed with response: 404 Not Found. Reason: Unknown");

        var mapped = XpcContainerRuntime.ToPullRuntimeException(ex, "pull docker.io/library/alpine:9.99.99-does-not-exist");

        Assert.Equal(RuntimeErrorKind.NotFound, mapped.Kind);
    }

    [Fact]
    public void ToPullRuntimeException_leaves_an_unrelated_unknown_error_as_Internal()
    {
        var ex = XpcException.ApiServer("unknown", "some other apiserver failure with no HTTP status in it");

        var mapped = XpcContainerRuntime.ToPullRuntimeException(ex, "pull docker.io/library/alpine:latest");

        Assert.Equal(RuntimeErrorKind.Internal, mapped.Kind);
    }

    [Fact]
    public void ToPullRuntimeException_passes_through_the_generic_mapping_for_other_codes()
    {
        var ex = XpcException.Interrupted("connection dropped");

        var mapped = XpcContainerRuntime.ToPullRuntimeException(ex, "pull docker.io/library/alpine:latest");

        Assert.Equal(RuntimeErrorKind.Unavailable, mapped.Kind);
    }

    private static ImageDescription Description(string reference, string digest) => new()
    {
        Reference = reference,
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = digest, Size = 374 },
    };
}
