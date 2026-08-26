using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.ContentStore;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Every <see cref="IContainerRuntime"/> image member over the images service
/// (<c>com.apple.container.core.container-core-images</c>, task cider-ede.10). Routes:
/// docs/spikes/xpc/02-apiserver-xpc-protocol.md §6's image-routes table, reached through
/// <see cref="_imagesClient"/> (<see cref="ImagesServiceClient"/>, extended by this task with every
/// route it did not already carry from cider-ede.6). <c>BuildImageAsync</c>/<c>LoginAsync</c> stay on
/// the CLI (this file's non-goals; see the // FALLBACK block in <c>XpcContainerRuntime.cs</c>) — but
/// <c>BuildImageAsync</c> still enters <see cref="_blobSweepGate"/> before delegating (cider-ede.31
/// correction), so it is covered by the same gate as every write path below.
/// </summary>
/// <remarks>
/// <c>imageList</c> answers one <see cref="ImageDescription"/> per <i>reference</i>, each carrying
/// only the top-level index <see cref="Descriptor"/> — unlike the CLI's own <c>image ls --format
/// json</c>, nothing about size/platform/labels/created rides along. Getting any of that means
/// walking index → manifest → config through <c>contentGet</c> + a local file read, once per blob
/// (§6, "How ClientImage.get / config() read image configs"). <see cref="ListImagesAsync"/> and
/// <see cref="InspectImageAsync"/> both do this walk via <see cref="LoadBlobsAsync"/>, then hand the
/// already-decoded blobs to the pure mapping methods below (<see cref="ToRuntimeImage"/>,
/// <see cref="ToRuntimeImageDetail"/>) — split out precisely so
/// <c>tests/Cider.Tests/AppleContainer/Xpc/ImagesMappingTests.cs</c> can drive the mapping from
/// hand-built fixtures without a live apiserver, the same shape <c>XpcContainerRuntime.Mapping.cs</c>
/// already uses for containers/networks/volumes.
/// </remarks>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>Gates this runtime's own pulls/loads/builds against its own store-wide sweep
    /// (<see cref="PruneImagesAsync"/>, and the apiserver-unavailable delete fallback in
    /// <see cref="RemoveImageAsync"/>) — see <see cref="BlobSweepGate"/>'s own doc comment
    /// (cider-ede.31). <c>BuildImageAsync</c> (<c>XpcContainerRuntime.cs</c>'s // FALLBACK block) enters
    /// this same instance before delegating to the CLI (cider-ede.31 correction: it was the one write
    /// path on this transport left ungated, since it does not go through <c>_imagesClient</c> like the
    /// members below do).</summary>
    private readonly BlobSweepGate _blobSweepGate = new();

    // ---- images: read paths (list/inspect) ------------------------------------------------------

    /// <summary>
    /// <c>imageList</c>, grouped by raw index digest — one <see cref="RuntimeImage"/> per digest,
    /// references unioned (task fix direction §1: "same semantics as <c>RuntimeMapper.ToImages</c>",
    /// which does the identical merge for the CLI transport's one-row-per-reference output).
    /// <see cref="RuntimeImage.Id"/> is then derived from that group's own content, not the raw index
    /// digest used to group it (see <see cref="ToRuntimeImage"/>) — unlike the CLI transport, this does
    /// not additionally re-merge groups that turn out to share one content id after that derivation
    /// (e.g. two tags loaded separately from byte-identical content, each getting its own fresh Apple
    /// index digest): each stays its own list row here, with its own <see cref="RuntimeImage.Id"/> that
    /// happens to equal the other's. cider-ger.19's own regression (the same tag reloaded twice) never
    /// hits this, since reloading one reference replaces its single <c>imageList</c> row in place
    /// rather than adding a second one.
    /// </summary>
    public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "imageList",
            async () =>
            {
                var descriptions = await _imagesClient.ImageListAsync(ct).ConfigureAwait(false);
                var images = new List<RuntimeImage>();
                var unresolvedDigests = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in GroupByDigest(descriptions))
                {
                    var digest = group[0].Descriptor.Digest;
                    var (variants, manifests, configs) = await LoadBlobsAsync(digest, ct, unresolvedDigests).ConfigureAwait(false);
                    images.Add(ToRuntimeImage(References(group), digest, variants, manifests, configs));
                }

                // Parity with the CLI transport's own dangling-content Warning (cider-ede.24 fix
                // direction item 3): a single unresolvable blob must never fail the whole listing on
                // either transport, and an operator must see the same guidance either way — exactly one
                // Warning per listing call, not one per unresolved blob. The Warning names only
                // unresolvable top-level index blobs (the digest state.json names directly per image);
                // unresolvedDigests de-dupes one shared by two groups so it is only named once. A
                // per-platform manifest absent from a multi-platform index — the common case for an
                // ordinary pull that never fetched every platform's variant locally — is a normal,
                // silent store-miss, not a dangling reference, and never reaches this set.
                if (unresolvedDigests.Count > 0)
                {
                    _logger.LogWarning("{Message}", CliErrorMapper.DanglingContentRemedy(string.Join(", ", unresolvedDigests)));
                }

                return (IReadOnlyList<RuntimeImage>)images;
            },
            () => _cliFallback.ListImagesAsync(ct)));

    /// <summary>
    /// <c>imageList</c> + client-side match via <see cref="ImageSnapshotEnsurer.Match"/> — there is no
    /// per-reference lookup route, so this prefers the index descriptor's
    /// <c>containerizationImageName</c> annotation the same way Apple's <c>ClientImage.get(reference:)</c>
    /// does for a locally built image, falling back to exact <see cref="ImageDescription.Reference"/>
    /// equality.
    /// </summary>
    public Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        return await XpcReadAsync(
            "imageList",
            async () =>
            {
                var descriptions = await _imagesClient.ImageListAsync(ct).ConfigureAwait(false);
                var matched = ImageSnapshotEnsurer.Match(descriptions, reference);
                if (matched is null)
                {
                    return null;
                }

                var group = GroupByDigest(descriptions).Find(g => g.Contains(matched));
                if (group is null)
                {
                    return null;
                }

                var digest = group[0].Descriptor.Digest;
                var (variants, manifests, configs) = await LoadBlobsAsync(digest, ct).ConfigureAwait(false);
                return ToRuntimeImageDetail(References(group), digest, variants, manifests, configs);
            },
            () => _cliFallback.InspectImageAsync(reference, ct)).ConfigureAwait(false);
    });

    /// <summary>
    /// Walks one image's index → real manifests → configs (§6), <c>contentGet</c> + a local file read
    /// per blob, each blob read at most once per call (deduped through the two dictionaries this
    /// returns — the task fix direction's "cached per digest", scoped to one list/inspect call rather
    /// than the runtime's whole lifetime: an image's blobs never change once pulled, but nothing above
    /// this seam calls list/inspect often enough for a longer-lived cache to be worth the added
    /// invalidation surface).
    /// </summary>
    private async Task<(List<OciDescriptor> Variants, Dictionary<string, AppleOciManifest> Manifests, Dictionary<string, AppleOciImageDocument> Configs)>
        LoadBlobsAsync(string? digest, CancellationToken ct, ISet<string>? unresolvedDigests = null)
    {
        var index = await GetBlobAsync<OciIndex>(digest, ct, unresolvedDigests).ConfigureAwait(false);
        var variants = RealVariants(index);
        var manifests = new Dictionary<string, AppleOciManifest>(StringComparer.Ordinal);
        var configs = new Dictionary<string, AppleOciImageDocument>(StringComparer.Ordinal);

        foreach (var variant in variants)
        {
            if (variant.Digest is not { Length: > 0 } variantDigest || manifests.ContainsKey(variantDigest))
            {
                continue;
            }

            var manifest = await GetBlobAsync<AppleOciManifest>(variantDigest, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            manifests[variantDigest] = manifest;
            if (manifest.Config?.Digest is { Length: > 0 } configDigest && !configs.ContainsKey(configDigest))
            {
                var config = await GetBlobAsync<AppleOciImageDocument>(configDigest, ct).ConfigureAwait(false);
                if (config is not null)
                {
                    configs[configDigest] = config;
                }
            }
        }

        return (variants, manifests, configs);
    }

    /// <summary>
    /// <c>contentGet(digest)</c> → local path → <see cref="LocalBlobReader.TryReadAsync{T}"/> (§6's
    /// two-step read). <c>null</c> on a missing digest, a <c>notFound</c> from <c>contentGet</c> itself,
    /// or an unparsable/missing blob file — every case collapses to "nothing recovered", exactly like
    /// the CLI transport's own best-effort blob reads. <paramref name="digest"/> is recorded into
    /// <paramref name="unresolvedDigests"/> for the caller's single per-listing Warning (item 3) only
    /// when the caller passes a non-null set — <see cref="LoadBlobsAsync"/> does that for the image's
    /// own top-level index blob only, since that is the one digest <c>state.json</c> names directly and
    /// the one whose loss actually empties the listed row; a per-platform manifest or its config that
    /// was legitimately never fetched locally (a normal, silent store-miss on a multi-platform index)
    /// stays untracked here — not just the exception path below, since
    /// <see cref="ImagesServiceClient.ContentGetAsync"/> already swallows a <c>notFound</c> itself and
    /// answers <c>null</c>, and a <paramref name="digest"/> whose blob file has since been deleted from
    /// disk resolves to a path that simply no longer exists — both far more common live shapes than an
    /// actual thrown exception.
    ///
    /// cider-ede.24 fix direction item 2: a single dangling/unresolvable content reference must never
    /// fail the whole listing (parity with the CLI transport's own tolerance in
    /// <c>AppleContainerRuntime.ListImagesAsync</c>) — so any <see cref="XpcException"/>
    /// <see cref="ContentGetAsync"/> raises beyond the <c>notFound</c> it already swallows itself is
    /// caught here too and collapses to "nothing recovered" the same way. The one exception is
    /// <see cref="RuntimeErrorKind.Unavailable"/>, which must keep propagating unchanged — that is what
    /// lets <see cref="XpcReadAsync{T}"/>'s own catch fall the *entire* call back to the CLI transport
    /// rather than this method silently absorbing an apiserver that is not there at all.
    /// </summary>
    private async Task<T?> GetBlobAsync<T>(string? digest, CancellationToken ct, ISet<string>? unresolvedDigests = null) where T : class
    {
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }

        string? path;
        try
        {
            path = await _imagesClient.ContentGetAsync(digest, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (!IsUnavailable(ex))
        {
            _logger.LogDebug(ex, "could not resolve content digest {Digest} over xpc contentGet", digest);
            unresolvedDigests?.Add(digest);
            return null;
        }

        var blob = await LocalBlobReader.TryReadAsync<T>(path, _logger, ct).ConfigureAwait(false);
        if (blob is null)
        {
            unresolvedDigests?.Add(digest);
        }

        return blob;
    }

    /// <summary>
    /// Apple's <c>imageList</c> answers one row per reference, sharing one index
    /// <see cref="Descriptor.Digest"/> when two tags name the same image — the same shape
    /// <c>RuntimeMapper.ToImages</c>'s own doc comment describes for the CLI's <c>image ls</c>. Groups
    /// preserve first-seen order, both across groups and of references within a group, and a
    /// description with no digest becomes its own singleton group rather than merging blindly (same
    /// defensive choice <c>RuntimeMapper.ToImages</c> makes).
    /// </summary>
    internal static List<List<ImageDescription>> GroupByDigest(List<ImageDescription> descriptions)
    {
        var groups = new List<List<ImageDescription>>();
        var byDigest = new Dictionary<string, List<ImageDescription>>(StringComparer.Ordinal);

        foreach (var description in descriptions)
        {
            var digest = description.Descriptor.Digest;
            if (string.IsNullOrEmpty(digest))
            {
                groups.Add([description]);
                continue;
            }

            if (!byDigest.TryGetValue(digest, out var group))
            {
                group = [];
                byDigest[digest] = group;
                groups.Add(group);
            }

            group.Add(description);
        }

        return groups;
    }

    /// <summary>The group's references, first-seen order, deduplicated.</summary>
    internal static IReadOnlyList<string> References(List<ImageDescription> group)
    {
        var references = new List<string>(group.Count);
        foreach (var description in group)
        {
            if (!string.IsNullOrEmpty(description.Reference) && !references.Contains(description.Reference, StringComparer.Ordinal))
            {
                references.Add(description.Reference);
            }
        }

        return references;
    }

    /// <summary>Every non-attestation manifest entry of an index (buildkit pairs one
    /// <c>unknown/unknown</c> manifest with each real platform) — mirrors
    /// <c>RuntimeMapper.RealVariants</c>' own filter, applied to <see cref="OciIndex.Manifests"/>
    /// instead of a CLI JSON row's <c>variants[]</c>.</summary>
    internal static List<OciDescriptor> RealVariants(OciIndex? index)
    {
        var result = new List<OciDescriptor>();
        if (index?.Manifests is not { Count: > 0 } manifests)
        {
            return result;
        }

        foreach (var manifest in manifests)
        {
            if (!IsAttestation(manifest.Platform))
            {
                result.Add(manifest);
            }
        }

        return result;
    }

    private static bool IsAttestation(OciPlatform? platform) =>
        string.Equals(platform?.Architecture, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform?.Os, "unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the requested platform's variant, else the host's, else the first real one —
    /// field-for-field the same preference order as <c>RuntimeMapper.PickVariant</c>, reusing its
    /// <see cref="RuntimeMapper.ParsePlatform"/>/<see cref="RuntimeMapper.HostArchitecture"/> helpers
    /// directly rather than re-deriving them.</summary>
    internal static OciDescriptor? PickVariant(List<OciDescriptor> variants, string? platform)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        var (os, architecture, variantName) = RuntimeMapper.ParsePlatform(platform);
        os ??= "linux";
        architecture ??= RuntimeMapper.HostArchitecture;

        foreach (var candidate in variants)
        {
            if (Matches(candidate, os, architecture, variantName))
            {
                return candidate;
            }
        }

        foreach (var candidate in variants)
        {
            if (string.Equals(candidate.Platform?.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return variants[0];
    }

    private static bool Matches(OciDescriptor variant, string os, string architecture, string? variantName)
    {
        if (!string.Equals(variant.Platform?.Os, os, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(variant.Platform?.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return variantName is null || string.Equals(variant.Platform?.Variant, variantName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ToPlatformString(OciPlatform? platform)
    {
        if (string.IsNullOrEmpty(platform?.Os) || string.IsNullOrEmpty(platform.Architecture))
        {
            return null;
        }

        return string.IsNullOrEmpty(platform.Variant)
            ? $"{platform.Os}/{platform.Architecture}"
            : $"{platform.Os}/{platform.Architecture}/{platform.Variant}";
    }

    /// <summary>A real variant's own on-disk size: cider's own equivalent of the CLI's
    /// <c>getFullImageSize</c> (Apple's <c>ClientImage</c>/<c>ImageResource.swift</c>), which is the
    /// manifest descriptor's own <c>size</c> (<paramref name="variant"/>.Size) plus the manifest's
    /// <c>config.size</c> plus the sum of its <c>layers[].size</c> — not layers alone. Mirrors Apple's
    /// <c>continue</c> on a manifest fetch failure by returning 0 when the digest can't be resolved to
    /// a loaded manifest.</summary>
    internal static long VariantSize(OciDescriptor variant, IReadOnlyDictionary<string, AppleOciManifest> manifests)
    {
        if (variant.Digest is not { } digest || !manifests.TryGetValue(digest, out var manifest))
        {
            return 0;
        }

        long size = variant.Size ?? 0;
        size += manifest.Config?.Size ?? 0;
        if (manifest.Layers is { Count: > 0 } layers)
        {
            foreach (var layer in layers)
            {
                size += layer.Size ?? 0;
            }
        }

        return size;
    }

    /// <summary>
    /// <see cref="RuntimeImage"/> for one digest group. <see cref="RuntimeImage.Size"/> sums every
    /// real platform variant's own <see cref="VariantSize"/> — matching
    /// <c>RuntimeMapper.ToImage</c>'s own "sum across every real variant" behavior for the CLI
    /// transport (task fix direction §1's parity requirement), not just the host's platform.
    /// Created/Labels come from the preferred (host, else first) variant's config — the same "one
    /// representative variant" choice <c>RuntimeMapper.ToImage</c> makes via its own
    /// <c>PickVariant(json, null)</c>.
    ///
    /// <see cref="RuntimeImage.Id"/> is the preferred variant's manifest <c>config.digest</c>, not
    /// <paramref name="digest"/> (the group's raw index digest) — the same content-addressed
    /// derivation <c>AppleContainerRuntime.RecoverContentAddressedIdsAsync</c> applies on the CLI
    /// transport (cider-ger.19 orchestrator follow-up: this path was still reporting Apple's unstable
    /// index digest, so it alone kept the default XPC install failing compat scenario 6 even after the
    /// CLI-only fix). No extra blob read is needed here: <paramref name="manifests"/> was already
    /// loaded by <see cref="LoadBlobsAsync"/> to build <paramref name="configs"/>, so this just reads
    /// the digest already in hand. Falls back to the index digest when the preferred variant's
    /// manifest was not resolved (store miss), same best-effort contract as the CLI transport.
    /// <paramref name="digest"/> itself is preserved on <see cref="RuntimeImage.IndexDigests"/> so a
    /// container still bound to it (<c>configuration.image.descriptor.digest</c> at creation time)
    /// stays recognized as using this image even after <see cref="RuntimeImage.Id"/> stops matching it.
    /// </summary>
    internal static RuntimeImage ToRuntimeImage(
        IReadOnlyList<string> references,
        string? digest,
        List<OciDescriptor> variants,
        IReadOnlyDictionary<string, AppleOciManifest> manifests,
        IReadOnlyDictionary<string, AppleOciImageDocument> configs)
    {
        long size = 0;
        var platforms = new List<string>();
        foreach (var variant in variants)
        {
            size += VariantSize(variant, manifests);
            var platformString = ToPlatformString(variant.Platform);
            if (platformString is not null && !platforms.Contains(platformString, StringComparer.Ordinal))
            {
                platforms.Add(platformString);
            }
        }

        var preferred = PickVariant(variants, null);
        var config = PreferredConfig(preferred, manifests, configs);
        var indexDigest = RuntimeMapper.ToImageId(digest);
        var contentId = preferred?.Digest is { Length: > 0 } preferredDigest && manifests.TryGetValue(preferredDigest, out var preferredManifest)
            ? preferredManifest.Config?.Digest
            : null;

        return new RuntimeImage
        {
            Id = string.IsNullOrEmpty(contentId) ? indexDigest : contentId,
            References = references,
            Size = size,
            Created = config?.Created,
            Platforms = platforms,
            Labels = config?.Config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            IndexDigests = indexDigest.Length > 0 ? [indexDigest] : [],
        };
    }

    /// <summary>
    /// <see cref="RuntimeImageDetail"/> for the preferred (host, else first) platform variant only —
    /// the same single-variant shape <c>RuntimeMapper.ToImageDetail</c> returns for the CLI transport.
    /// <see cref="RuntimeImageDetail.Config"/> is the honest, untruncated OCI config
    /// (<see cref="RuntimeMapper.ToImageConfig"/>, shared with the CLI mapper since both now read the
    /// same <see cref="AppleOciConfig"/> type) — <c>ExposedPorts</c>/<c>Volumes</c> need no recovery
    /// hack here the way <c>AppleContainerRuntime.RecoverExposedPortsAsync</c> needs one for the CLI's
    /// truncated <c>image inspect</c> echo (task fix direction §1), because this reads the real config
    /// blob directly.
    /// </summary>
    internal static RuntimeImageDetail? ToRuntimeImageDetail(
        IReadOnlyList<string> references,
        string? digest,
        List<OciDescriptor> variants,
        IReadOnlyDictionary<string, AppleOciManifest> manifests,
        IReadOnlyDictionary<string, AppleOciImageDocument> configs)
    {
        var variant = PickVariant(variants, null);
        if (variant is null)
        {
            return null;
        }

        var summary = ToRuntimeImage(references, digest, variants, manifests, configs);
        var manifest = variant.Digest is { } variantDigest && manifests.TryGetValue(variantDigest, out var m) ? m : null;
        var config = PreferredConfig(variant, manifests, configs);

        return new RuntimeImageDetail
        {
            Id = summary.Id,
            References = summary.References,
            IndexDigests = summary.IndexDigests,
            Size = VariantSize(variant, manifests),
            Created = config?.Created ?? summary.Created,
            Platforms = summary.Platforms,
            Labels = summary.Labels,
            Architecture = config?.Architecture ?? variant.Platform?.Architecture ?? "",
            Os = config?.Os ?? variant.Platform?.Os ?? "",
            Variant = config?.Variant ?? variant.Platform?.Variant,
            Layers = config?.Rootfs?.DiffIds is { Count: > 0 } diffIds ? [.. diffIds] : [],
            LayerSizes = manifest?.Layers is { Count: > 0 } layers ? [.. layers.Select(l => l.Size ?? 0)] : [],
            Author = config?.Author,
            RepoDigests = RepoDigests(references, digest),
            History = config?.History is { Count: > 0 } history ? [.. history.Select(ToHistory)] : [],
            Config = RuntimeMapper.ToImageConfig(config?.Config),
        };
    }

    private static AppleOciImageDocument? PreferredConfig(
        OciDescriptor? variant,
        IReadOnlyDictionary<string, AppleOciManifest> manifests,
        IReadOnlyDictionary<string, AppleOciImageDocument> configs)
    {
        if (variant?.Digest is not { } variantDigest || !manifests.TryGetValue(variantDigest, out var manifest))
        {
            return null;
        }

        return manifest.Config?.Digest is { } configDigest && configs.TryGetValue(configDigest, out var config) ? config : null;
    }

    private static RuntimeImageHistory ToHistory(AppleOciHistory history) => new()
    {
        Created = history.Created,
        CreatedBy = history.CreatedBy ?? "",
        Comment = history.Comment ?? "",
        EmptyLayer = history.EmptyLayer,
    };

    /// <summary>Same construction as <c>RuntimeMapper.RepoDigests</c>: <c>{repository}@{digest}</c>
    /// from the group's first reference, stripped of its tag.</summary>
    internal static IReadOnlyList<string> RepoDigests(IReadOnlyList<string> references, string? digest)
    {
        var name = references.Count > 0 ? references[0] : null;
        if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(name))
        {
            return [];
        }

        var colon = name.LastIndexOf(':');
        var slash = name.LastIndexOf('/');
        var repository = colon > slash ? name[..colon] : name;
        return [$"{repository}@{digest}"];
    }

    // ---- images: write paths -----------------------------------------------------------------

    /// <summary>
    /// <c>imagePull{imageReference, ociPlatform?, insecureFlag:false, progressUpdateEndpoint}</c> then
    /// <c>imageUnpack</c> on the same progress endpoint — the real CLI does both too
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §4's call-sequence table: "(I) imagePull
    /// [+progressUpdateEndpoint] ; (I) imageUnpack"). <see cref="ProgressUpdateListener"/> only ever
    /// forwards a <see cref="ProgressEvent"/> once the apiserver itself sends one — nothing is
    /// reported before that happens, which is what lets
    /// <c>ImageManager.PullAsync</c>'s own header-holdback rule (its doc comment: "nothing may reach
    /// the caller until the pull is provably past the manifest lookup") work unmodified over this
    /// transport too: a <c>notFound</c> that lands before the apiserver ever reports progress crosses
    /// this method as a plain <see cref="RuntimeException"/> of kind
    /// <see cref="RuntimeErrorKind.NotFound"/>, exactly what that rule watches for (task fix direction
    /// §2).
    /// </summary>
    public Task PullImageAsync(
        string reference,
        string? platform,
        RegistryAuth? auth,
        IProgress<ProgressEvent> progress,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentNullException.ThrowIfNull(progress);

        if (auth is not null && !string.IsNullOrEmpty(auth.Username))
        {
            // LoginAsync stays on the CLI (fix direction §2: "registry login stores credentials the
            // images service reads" — there is no XPC route for it at all).
            await _cliFallback.LoginAsync(auth, ct).ConfigureAwait(false);
        }

        var ociPlatform = ParseOciPlatform(platform);

        // cider-ede.31: a pull writes blobs before its index entry is committed — it must never run
        // while a store-wide sweep (PruneImagesAsync, or the delete fallback below) is in flight.
        await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);

        try
        {
            using var listener = new ProgressUpdateListener(_logger, progress.Report);
            var description = await _imagesClient.ImagePullAsync(reference, ociPlatform, listener.Endpoint, ct).ConfigureAwait(false);
            await _imagesClient.ImageUnpackAsync(description, ociPlatform ?? Platform.Current, ct, listener.Endpoint).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("imagePull", ex);
            await _cliFallback.PullImageAsync(reference, platform, auth, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ToPullRuntimeException(ex, $"pull {reference}");
        }
    });

    /// <summary>
    /// A registry-manifest-not-found failure during <c>imagePull</c>/<c>imagePush</c> does not arrive
    /// as the generic apiserver <c>notFound</c> code (§1.3) — verified live against a real apiserver
    /// (1.3.0): pulling a nonexistent tag surfaces <see cref="XpcErrorClass.ApiServer"/> code
    /// <c>"unknown"</c> wrapping <c>"HTTP request to https://registry-1.docker.io/... failed with
    /// response: 404 Not Found. Reason: Unknown"</c> as plain message text — the underlying registry
    /// client's own HTTP error, not a <c>ContainerizationError.notFound</c>. The generic
    /// <see cref="XpcErrorMapper"/> code table can't tell this apart from a genuine internal failure
    /// by code alone, so — the same reason <c>XpcContainerRuntime.Resources.cs</c>'s
    /// <c>ToVolumeRuntimeException</c> and <c>XpcContainerRuntime.Archive.cs</c>'s
    /// <c>ToCopyRuntimeException</c> already sniff message text elsewhere in this transport — this
    /// reads it once, here, below the <c>IContainerRuntime</c> seam, so
    /// <c>ImageManager.PullAsync</c>'s "a <see cref="RuntimeErrorKind.NotFound"/> before the pull is
    /// under way answers a plain HTTP 404" contract (task fix direction §2) still holds over this
    /// transport.
    /// </summary>
    internal static RuntimeException ToPullRuntimeException(XpcException ex, string context) =>
        ex.ErrorClass == XpcErrorClass.ApiServer && ex.Message.Contains("response: 404", StringComparison.OrdinalIgnoreCase)
            ? new RuntimeException(RuntimeErrorKind.NotFound, $"{context}: {ex.Message}", ex)
            : ex.ToRuntimeException(context);

    /// <summary><c>imagePush{imageReference, progressUpdateEndpoint}</c> (§6; fix direction §2 "likewise").
    /// The Docker-shaped "The push refers to repository […]" header is reported unconditionally up
    /// front, same as the CLI transport's own <c>PushImageAsync</c> — pushing only ever targets an
    /// image cider already resolved a local id for above this seam, so there is no equivalent
    /// "manifest not found yet" race to hold it back for.</summary>
    public Task PushImageAsync(
        string reference,
        RegistryAuth? auth,
        IProgress<ProgressEvent> progress,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentNullException.ThrowIfNull(progress);

        if (auth is not null && !string.IsNullOrEmpty(auth.Username))
        {
            await _cliFallback.LoginAsync(auth, ct).ConfigureAwait(false);
        }

        progress.Report(new ProgressEvent { Status = $"The push refers to repository [{reference}]" });

        try
        {
            using var listener = new ProgressUpdateListener(_logger, progress.Report);
            await _imagesClient.ImagePushAsync(reference, null, listener.Endpoint, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("imagePush", ex);
            await _cliFallback.PushImageAsync(reference, auth, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            var exception = ToPullRuntimeException(ex, $"push {reference}");
            progress.Report(new ProgressEvent { Error = exception.Message });
            throw exception;
        }
    });

    /// <summary>Best-effort <c>string</c> → <see cref="Platform"/>; <c>null</c> when
    /// <paramref name="platform"/> is empty or carries no architecture — matching the optional
    /// <c>ociPlatform</c> field (§6), letting the apiserver fall back to its own default (the host's)
    /// instead of cider guessing at one.</summary>
    private static Platform? ParseOciPlatform(string? platform)
    {
        if (string.IsNullOrEmpty(platform))
        {
            return null;
        }

        var (os, architecture, variant) = RuntimeMapper.ParsePlatform(platform);
        return string.IsNullOrEmpty(architecture)
            ? null
            : new Platform { Os = os ?? "linux", Architecture = architecture, Variant = variant };
    }

    /// <summary><c>imageTag{imageReference, imageNewReference}</c> (§6).</summary>
    public Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceReference);
        ArgumentException.ThrowIfNullOrEmpty(targetReference);

        try
        {
            await _imagesClient.ImageTagAsync(sourceReference, targetReference, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("imageTag", ex);
            await _cliFallback.TagImageAsync(sourceReference, targetReference, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"image tag {sourceReference}");
        }
    });

    /// <summary>
    /// <c>imageDelete{imageReference, garbageCollect:false}</c> — <c>garbageCollect</c> hardcoded
    /// <c>false</c> (cider-ede.31 fix direction §1, "drop the sweep from the delete path entirely"):
    /// this used to also call <c>imageCleanupOrphanedBlobs</c> right after, mirroring
    /// <c>ImageDelete.swift</c>'s own one-shot CLI sequence, but that sweep is store-wide and not
    /// scoped to the image just deleted — run on every single <c>rmi</c> from a daemon serving
    /// concurrent clients, it kept a race window open permanently against any pull/load that had
    /// written blobs but not yet committed its index entry (cider-ede.31: corrupted the store twice in
    /// one day this way). The sweep now runs only from <see cref="PruneImagesAsync"/>, where the user
    /// explicitly asked to reclaim space. Leaving this image's now-unreferenced blobs in place until
    /// then costs disk, not correctness. <paramref name="force"/> mirrors the CLI transport's own
    /// asymmetry with Docker's <c>-f</c> (<c>AppleContainerRuntime.Images.cs</c>'s
    /// <c>RemoveImageAsync</c> comment: Apple's <c>-f</c> means "ignore images that are not found", not
    /// "remove anyway") — a <c>notFound</c> is swallowed only when <paramref name="force"/> is set.
    /// </summary>
    public Task RemoveImageAsync(string reference, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        try
        {
            await _imagesClient.ImageDeleteAsync(reference, garbageCollect: false, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("imageDelete", ex);

            // Apple's own `container image delete` CLI always sweeps internally, as one step of its
            // single process invocation (ImageDelete.swift, confirmed via `container image delete
            // --help` — no flag skips it) — unlike the primary path just above, this genuinely is a
            // sweep from this daemon's point of view, so it takes the gate exclusively, the same as
            // PruneImagesAsync, rather than running unguarded against this runtime's own concurrent
            // pulls/loads.
            await using var sweep = await _blobSweepGate.EnterSweepAsync(ct).ConfigureAwait(false);
            await _cliFallback.RemoveImageAsync(reference, force, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex) when (force && XpcErrorMapper.ToRuntimeErrorKind(ex) == RuntimeErrorKind.NotFound)
        {
            // Apple's own -f: ignore an image that is not found, rather than fail the call.
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"image delete {reference}");
        }
    });

    /// <summary>
    /// The store-wide sweep, moved here off the per-<c>rmi</c> delete path (cider-ede.31 fix direction
    /// §2): called only from <c>ImageManager.PruneAsync</c> (<c>docker image/system prune</c>), where a
    /// sweep is what the user explicitly asked for, and takes <see cref="_blobSweepGate"/> exclusively
    /// so it cannot overlap this runtime's own in-flight pulls/loads (fix direction §3). Logs at
    /// Information what it reclaimed (fix direction §4: "so the next occurrence is attributable rather
    /// than mysterious") — <c>Debug</c> when it reclaimed nothing, so a routine prune of an
    /// already-clean store does not spam the log.
    ///
    /// cider-ehn: when the sweep fails on a pre-existing dangling content reference elsewhere in the
    /// store (the cider-bci catch below), it is tried first because it is cheaper and
    /// Apple-authoritative, but it is all-or-nothing — that one unrelated corruption means *nothing*
    /// gets reclaimed, including blobs <paramref name="deletedImageDigests"/> itself just orphaned a
    /// moment ago. <see cref="TryScopedReclaimAsync"/> is the fallback for exactly that case: a scoped,
    /// client-side <c>contentDelete</c> of only those blobs, run under this same
    /// <see cref="_blobSweepGate"/> scope (fix direction §4) and never inferring "orphaned" from an
    /// absence (see that method's own doc comment for the binding safety rule).
    /// </summary>
    public Task PruneImagesAsync(IReadOnlyList<string> deletedImageDigests, CancellationToken ct) => GuardAsync(async () =>
    {
        await using var sweep = await _blobSweepGate.EnterSweepAsync(ct).ConfigureAwait(false);

        try
        {
            var (digests, imageSize) = await _imagesClient.ImageCleanupOrphanedBlobsAsync(ct).ConfigureAwait(false);
            if (digests.Count > 0)
            {
                _logger.LogInformation(
                    "image prune: reclaimed {Count} orphaned blob(s), {Size} byte(s) via the whole-store sweep", digests.Count, imageSize);
            }
            else
            {
                _logger.LogDebug("image prune: no orphaned blobs to reclaim");
            }
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            // No CLI-transport equivalent to fall back to here: the CLI's own `container image
            // delete` already swept once per target inside RemoveImageAsync's own fallback branch
            // above, whenever the apiserver was unavailable for *that* call — there is nothing left
            // for this call to additionally reclaim over the CLI.
            WarnFallback("imageCleanupOrphanedBlobs", ex);
        }
        catch (XpcException ex) when (CliErrorMapper.IsDanglingContent(ex.Message))
        {
            // cider-bci: unlike a per-image `imageDelete`, this sweep walks every blob in the whole
            // store — including ones from images this daemon never touched — so one pre-existing
            // dangling/unresolvable content reference elsewhere in the store (cider-ede.24's own class
            // of corruption, which cider must tolerate and never repair) fails the sweep every single
            // time, even on a store that otherwise has nothing wrong with it. Before this, that turned
            // *every* `docker image prune` into a total failure (500), discarding the per-image
            // deletions `ImageManager.PruneAsync` had already made above — the same "never turn a
            // success into a failure" rule ListImagesAsync's own dangling-content tolerance follows
            // (this file's own doc comment credits it to cider-ede.24). Reclaiming orphaned blobs is a
            // nicety on top of those deletions, not their contract, so this degrades the same way
            // ListImagesAsync does: log once, at the same Warning level and with the same
            // operator-facing remedy text, and let the prune otherwise report success.
            var digest = CliErrorMapper.ExtractDanglingDigest(ex.Message) ?? ex.Message;
            _logger.LogWarning("{Message}", CliErrorMapper.DanglingContentRemedy(digest));

            // cider-ehn fallback: try to reclaim exactly the blobs this call's own deletions just
            // orphaned, even though the whole-store sweep above could not run at all.
            await TryScopedReclaimAsync(deletedImageDigests, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException("image prune");
        }
    });

    /// <summary>
    /// cider-ehn's scoped fallback, entered only when <see cref="PruneImagesAsync"/>'s whole-store
    /// sweep failed on a pre-existing dangling content reference elsewhere in the store.
    /// <paramref name="deletedImageDigests"/> are the raw index digests (<c>RuntimeImage.IndexDigests</c>)
    /// of the images <c>ImageManager.PruneAsync</c> just finished deleting via <c>imageDelete</c> in
    /// this same call — <c>garbageCollect: false</c> (<see cref="RemoveImageAsync"/>'s own doc comment)
    /// leaves every blob those images' manifests named still resolvable via <c>contentGet</c>, only the
    /// index *reference* itself is gone, so <see cref="CollectManifestDigestsAsync"/> can still walk
    /// them here exactly as <see cref="LoadBlobsAsync"/> would for a live listing (same
    /// <see cref="GetBlobAsync{T}"/> primitive — no second content-read path).
    ///
    /// <para>
    /// <b>THE SAFETY RULE — binding, planner-ruled (task cider-ehn's own description).</b> Deleting a
    /// blob by digest is irreversible, and the only valid proof that one is safe to delete is a
    /// positive one: every image the store still lists was enumerated, its manifest was actually read,
    /// and none of them names that digest. An absence of evidence — a remaining image this method
    /// could not enumerate, or one whose index or manifest failed to resolve — is never allowed to
    /// stand in for that proof. So: if listing the store's current images fails, or if any *remaining*
    /// image's index or manifest cannot be resolved, this method deletes nothing at all — not even the
    /// candidate digests it could otherwise fully account for — and makes zero
    /// <see cref="ImagesServiceClient.ContentDeleteAsync"/> calls. This is strictly more conservative
    /// on a corrupted store, not a limitation to work around: the corrupted-store case this whole task
    /// exists to survive is exactly the case where a remaining image's manifest is unreadable, and that
    /// unreadable entry is precisely the one that might reference the blobs a reclaim would otherwise
    /// target. Do not weaken this to "prove non-reference only against readable entries" — that
    /// narrower rule was explicitly considered and rejected: it would delete blobs belonging to the
    /// very image whose entry is dangling.
    /// </para>
    /// <para>
    /// Any other unexpected failure while gathering that proof (the images service going away
    /// mid-walk, an XPC error unrelated to a single missing blob, a malformed reply that throws
    /// during JSON deserialization rather than as an <see cref="XpcException"/>) is treated the same
    /// way — caught, logged, and answered with zero deletions — rather than turning this best-effort
    /// fallback into a second way for <c>docker image prune</c> to 500.
    /// </para>
    /// </summary>
    private async Task TryScopedReclaimAsync(IReadOnlyList<string> deletedImageDigests, CancellationToken ct)
    {
        if (deletedImageDigests.Count == 0)
        {
            return;
        }

        try
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var digest in deletedImageDigests)
            {
                // Best-effort: a deleted image's own index/manifest failing to resolve just means it
                // contributes no candidates (a smaller, still-safe set) — this is not the
                // safety-critical half of this method; the walk over *remaining* images below is.
                await CollectManifestDigestsAsync(digest, candidates, ct).ConfigureAwait(false);
            }

            if (candidates.Count == 0)
            {
                _logger.LogDebug("image prune (scoped fallback): no blob digests recovered from the deleted image(s); nothing to reclaim");
                return;
            }

            var remaining = await _imagesClient.ImageListAsync(ct).ConfigureAwait(false);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in GroupByDigest(remaining))
            {
                var digest = group[0].Descriptor.Digest;
                if (!await CollectManifestDigestsAsync(digest, keep, ct).ConfigureAwait(false))
                {
                    // Safety rule: one remaining image whose index/manifest cannot be read means
                    // non-reference cannot be proven for *any* candidate, not just the ones that
                    // particular manifest might have named. Delete nothing.
                    _logger.LogWarning(
                        "image prune (scoped fallback): a remaining image's manifest could not be read, so " +
                        "non-reference cannot be proven for any candidate blob; deleting nothing (this is the " +
                        "intended, more conservative behaviour on a store with an unresolvable reference, not a bug)");
                    return;
                }
            }

            candidates.ExceptWith(keep);
            if (candidates.Count == 0)
            {
                _logger.LogDebug("image prune (scoped fallback): every candidate blob is still referenced by a remaining image; nothing to reclaim");
                return;
            }

            var (reclaimedDigests, imageSize) = await _imagesClient.ContentDeleteAsync([.. candidates], ct).ConfigureAwait(false);
            _logger.LogInformation(
                "image prune (scoped fallback): reclaimed {Count} orphaned blob(s), {Size} byte(s) after the whole-store sweep failed",
                reclaimedDigests.Count, imageSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "image prune (scoped fallback): could not complete the scoped reclaim; deleting nothing");
        }
    }

    /// <summary>
    /// Walks one image's index → real-platform manifests (§6, the same <c>contentGet</c> + local file
    /// read <see cref="LoadBlobsAsync"/> uses), harvesting every manifest's <c>config.digest</c> and
    /// <c>layers[].digest</c> into <paramref name="into"/> — no need to also fetch the config blob
    /// itself, since cider-ehn's fix direction only reclaims "the config and layer digests reachable
    /// from the manifests", both of which are named directly in the manifest already in hand.
    /// Returns <c>false</c> the instant the index, or any real-platform manifest, fails to resolve —
    /// <see cref="TryScopedReclaimAsync"/> treats that as fatal to the *whole* reclaim when walking a
    /// remaining image (the safety rule its own doc comment states), and as "this one image
    /// contributes nothing" when walking a deleted image.
    /// <para>
    /// A blob that parses but yields no config/layer digest is not proof; return false. Concretely:
    /// if the digest does not resolve to an index shape at all (<see cref="OciIndex.Manifests"/> is
    /// null — either the blob failed to resolve, or it parsed as something else entirely), the same
    /// digest is re-read as a bare <see cref="AppleOciManifest"/> — a legitimately single-manifest
    /// image with no index wrapper still counts as proof as long as it names a config or layer
    /// digest; anything else does not, and this returns false. And when the index does resolve but
    /// every one of its entries is attestation-only (<see cref="RealVariants"/> comes back empty),
    /// that is also not proof — nothing about the image was positively accounted for — so this
    /// returns false rather than silently treating an attestation-only index as a fully-walked,
    /// content-free image.
    /// </para>
    /// </summary>
    private async Task<bool> CollectManifestDigestsAsync(string? digest, HashSet<string> into, CancellationToken ct)
    {
        var index = await GetBlobAsync<OciIndex>(digest, ct).ConfigureAwait(false);
        if (index?.Manifests is null)
        {
            return await CollectBareManifestDigestsAsync(digest, into, ct).ConfigureAwait(false);
        }

        var variants = RealVariants(index);
        if (variants.Count == 0)
        {
            return false;
        }

        var complete = true;
        foreach (var variant in variants)
        {
            if (variant.Digest is not { Length: > 0 } variantDigest)
            {
                complete = false;
                continue;
            }

            var manifest = await GetBlobAsync<AppleOciManifest>(variantDigest, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                complete = false;
                continue;
            }

            if (manifest.Config?.Digest is { Length: > 0 } configDigest)
            {
                into.Add(configDigest);
            }

            if (manifest.Layers is { Count: > 0 } layers)
            {
                foreach (var layer in layers)
                {
                    if (layer.Digest is { Length: > 0 } layerDigest)
                    {
                        into.Add(layerDigest);
                    }
                }
            }
        }

        return complete;
    }

    /// <summary>
    /// The fallback half of <see cref="CollectManifestDigestsAsync"/>: <paramref name="digest"/> did
    /// not resolve to an index shape, so re-read the same digest as a bare
    /// <see cref="AppleOciManifest"/> directly — a legitimately single-manifest image (no index
    /// wrapper at all) still walks and still counts as proof. Only actually harvesting a config or
    /// layer digest counts as proof; a manifest that resolves but names neither (or a digest that
    /// does not resolve as a manifest either) returns false the same as an unreadable index would.
    /// </summary>
    private async Task<bool> CollectBareManifestDigestsAsync(string? digest, HashSet<string> into, CancellationToken ct)
    {
        var manifest = await GetBlobAsync<AppleOciManifest>(digest, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            return false;
        }

        var harvested = false;
        if (manifest.Config?.Digest is { Length: > 0 } configDigest)
        {
            into.Add(configDigest);
            harvested = true;
        }

        if (manifest.Layers is { Count: > 0 } layers)
        {
            foreach (var layer in layers)
            {
                if (layer.Digest is { Length: > 0 } layerDigest)
                {
                    into.Add(layerDigest);
                    harvested = true;
                }
            }
        }

        return harvested;
    }

    /// <summary>
    /// <c>imageList</c> to resolve each reference to its <see cref="ImageDescription"/>, then
    /// <c>imageSave{imageDescriptions, filePath}</c> to a temp file, streamed out and deleted — the
    /// same temp-file shape <see cref="ExportContainerAsync"/> and the CLI transport's own
    /// <c>SaveImagesAsync</c> both already use (fix direction §3).
    /// </summary>
    public Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(tarOutput);

        if (references.Count == 0)
        {
            throw RuntimeException.InvalidArgument("no image references given to save");
        }

        Directory.CreateDirectory(_options.TmpDir);
        var tmp = Path.Combine(_options.TmpDir, $"cider-save-{Guid.NewGuid():N}.tar");
        try
        {
            try
            {
                var all = await _imagesClient.ImageListAsync(ct).ConfigureAwait(false);
                var descriptions = new List<ImageDescription>(references.Count);
                foreach (var reference in references)
                {
                    var match = ImageSnapshotEnsurer.Match(all, reference)
                        ?? throw RuntimeException.NotFound($"image save: no such image: {reference}");
                    descriptions.Add(match);
                }

                await _imagesClient.ImageSaveAsync(descriptions, tmp, ct).ConfigureAwait(false);
            }
            catch (XpcException ex) when (IsUnavailable(ex))
            {
                WarnFallback("imageSave", ex);
                await _cliFallback.SaveImagesAsync(references, tarOutput, ct).ConfigureAwait(false);
                return;
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException("image save");
            }

            await using var file = File.OpenRead(tmp);
            await file.CopyToAsync(tarOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    });

    /// <summary>
    /// Stages <paramref name="tarInput"/> to a temp file, then <c>imageLoad{filePath,
    /// forceLoad:false}</c> → <c>imageUnpack</c> per loaded description (fix direction §3: "no
    /// before/after diff needed anymore" — <c>imageLoad</c>'s own reply already names exactly what it
    /// loaded, unlike the CLI transport's <c>container image load</c>, which only writes a bare
    /// reference to stdout (<c>Loaded image: &lt;ref&gt;</c>, per 918daf4) and so reads that plus a
    /// before/after <c>ListImagesAsync</c> diff as a secondary source instead). On an
    /// apiserver-unavailable fallback, the CLI runtime reads the already-staged temp file, not
    /// <paramref name="tarInput"/> itself — it was already fully consumed staging it, and a live
    /// request-body stream is not guaranteed seekable/re-readable.
    /// </summary>
    public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(tarInput);

        // cider-ede.31: same reasoning as PullImageAsync's own gate entry — a load writes blobs
        // before its index entry is committed, so it must never run alongside a store-wide sweep.
        await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);

        Directory.CreateDirectory(_options.TmpDir);
        var tmp = Path.Combine(_options.TmpDir, $"cider-load-{Guid.NewGuid():N}.tar");
        try
        {
            await using (var file = File.Create(tmp))
            {
                await tarInput.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            List<ImageDescription> loadedDescriptions;
            try
            {
                (loadedDescriptions, _) = await _imagesClient.ImageLoadAsync(tmp, forceLoad: false, ct).ConfigureAwait(false);
            }
            catch (XpcException ex) when (IsUnavailable(ex))
            {
                WarnFallback("imageLoad", ex);
                await using var staged = File.OpenRead(tmp);
                return await _cliFallback.LoadImagesAsync(staged, ct).ConfigureAwait(false);
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException("image load");
            }

            var loaded = new List<string>();
            foreach (var description in loadedDescriptions)
            {
                if (!string.IsNullOrEmpty(description.Reference) && !loaded.Contains(description.Reference, StringComparer.Ordinal))
                {
                    loaded.Add(description.Reference);
                }
            }

            // imageLoad only loads the content; the CLI transport's own `container image load` also
            // unpacks it eagerly, so this does the same — otherwise the very next container create off
            // a freshly loaded image would pay a lazy-unpack cost no other path through this transport
            // pays. Best-effort: a failure here does not fail the load itself, matching how the rest of
            // this transport treats unpack as a precondition it can always retry later
            // (ImageSnapshotEnsurer.EnsureAsync's own snapshotGet/imageUnpack retry).
            foreach (var description in loadedDescriptions)
            {
                try
                {
                    await _imagesClient.ImageUnpackAsync(description, Platform.Current, ct).ConfigureAwait(false);
                }
                catch (XpcException ex)
                {
                    _logger.LogDebug(ex, "could not eagerly unpack loaded image {Reference}; it will unpack lazily on first use", description.Reference);
                }
            }

            return (IReadOnlyList<string>)loaded;
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    });
}
