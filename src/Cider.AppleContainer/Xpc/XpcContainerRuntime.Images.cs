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
/// the CLI (this file's non-goals; see the // FALLBACK block in <c>XpcContainerRuntime.cs</c>).
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
    // ---- images: read paths (list/inspect) ------------------------------------------------------

    /// <summary><c>imageList</c>, grouped by index digest — one <see cref="RuntimeImage"/> per digest,
    /// references unioned (task fix direction §1: "same semantics as <c>RuntimeMapper.ToImages</c>",
    /// which does the identical merge for the CLI transport's one-row-per-reference output).</summary>
    public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "imageList",
            async () =>
            {
                var descriptions = await _imagesClient.ImageListAsync(ct).ConfigureAwait(false);
                var images = new List<RuntimeImage>();
                foreach (var group in GroupByDigest(descriptions))
                {
                    var digest = group[0].Descriptor.Digest;
                    var (variants, manifests, configs) = await LoadBlobsAsync(digest, ct).ConfigureAwait(false);
                    images.Add(ToRuntimeImage(References(group), digest, variants, manifests, configs));
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
        LoadBlobsAsync(string? digest, CancellationToken ct)
    {
        var index = await GetBlobAsync<OciIndex>(digest, ct).ConfigureAwait(false);
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

    /// <summary><c>contentGet(digest)</c> → local path → <see cref="LocalBlobReader.TryReadAsync{T}"/>
    /// (§6's two-step read). <c>null</c> on a missing digest, a <c>notFound</c> from <c>contentGet</c>
    /// itself, or an unparsable/missing blob file — every case collapses to "nothing recovered",
    /// exactly like the CLI transport's own best-effort blob reads.</summary>
    private async Task<T?> GetBlobAsync<T>(string? digest, CancellationToken ct) where T : class
    {
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }

        var path = await _imagesClient.ContentGetAsync(digest, ct).ConfigureAwait(false);
        return await LocalBlobReader.TryReadAsync<T>(path, _logger, ct).ConfigureAwait(false);
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

        return new RuntimeImage
        {
            Id = RuntimeMapper.ToImageId(digest),
            References = references,
            Size = size,
            Created = config?.Created,
            Platforms = platforms,
            Labels = config?.Config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
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
    /// <c>imageDelete{imageReference, garbageCollect:false}</c> then
    /// <c>imageCleanupOrphanedBlobs</c> (§6, mirroring <c>ImageDelete.swift</c> — fix direction §3).
    /// <paramref name="force"/> mirrors the CLI transport's own asymmetry with Docker's <c>-f</c>
    /// (<c>AppleContainerRuntime.Images.cs</c>'s <c>RemoveImageAsync</c> comment: Apple's <c>-f</c>
    /// means "ignore images that are not found", not "remove anyway") — a <c>notFound</c> is swallowed
    /// only when <paramref name="force"/> is set.
    /// </summary>
    public Task RemoveImageAsync(string reference, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        try
        {
            await _imagesClient.ImageDeleteAsync(reference, garbageCollect: false, ct).ConfigureAwait(false);
            await _imagesClient.ImageCleanupOrphanedBlobsAsync(ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("imageDelete", ex);
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
    /// loaded, unlike the CLI transport's <c>container image load</c>, which reports nothing
    /// machine-readable and so has to diff <c>ListImagesAsync</c> before/after instead). On an
    /// apiserver-unavailable fallback, the CLI runtime reads the already-staged temp file, not
    /// <paramref name="tarInput"/> itself — it was already fully consumed staging it, and a live
    /// request-body stream is not guaranteed seekable/re-readable.
    /// </summary>
    public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(tarInput);

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
