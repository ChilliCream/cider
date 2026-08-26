using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Images;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>Docker image operations: list/inspect/pull/push/tag/remove/history/save/load/prune/build/commit/import/login.</summary>
public sealed class ImageManager
{
    private readonly IContainerRuntime _runtime;
    private readonly EventBus _events;
    private readonly CiderOptions _options;
    private readonly ILogger<ImageManager> _logger;

    // ---- image detail cache --------------------------------------------
    //
    // `POST /containers/create` used to cost ~4 image runtime calls (inspect + a list fallback,
    // twice — once to resolve the image, once again for the config merge) before the actual
    // create. A create no longer re-derives the image config, and this cache means a *second*
    // create of the same reference costs zero: FindImageDetailAsync serves it straight from here.
    //
    // Keyed by the image id (`sha256:…`) and by every normalized reference (`name:tag`,
    // `name@digest`) known for it, all stamped with the cache's current version. A version bump
    // (on every mutation that goes through this manager — pull, load, build, tag, rmi, prune,
    // commit, import) invalidates the whole cache at once rather than trying to reason about which
    // entries a given mutation could have touched (a `tag` can retarget a reference that used to
    // point elsewhere, a `prune` can drop several images at once, etc.).
    //
    // Even with the version-based invalidation above, a change made outside this process (a second
    // `cider`/dockerd-compatible process, or `container image pull/rm/tag` run directly) bumps
    // nothing this manager knows about, so a bare version check would serve a stale entry forever.
    // A short TTL bounds that: once an entry is older than <see cref="CacheTtl"/> it reads as a miss
    // regardless of its version, so drift is caught within 30s without giving up the zero-runtime-call
    // fast path for the common case (repeated resolution of the same reference within one request).
    private long _imageCacheVersion;
    private readonly ConcurrentDictionary<string, CachedImageDetail> _imageCache = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly record struct CachedImageDetail(RuntimeImageDetail Detail, long Version, DateTimeOffset StoredAt);

    public ImageManager(IContainerRuntime runtime, EventBus events, CiderOptions options, ILogger<ImageManager> logger)
        : this(runtime, events, options, logger, TimeProvider.System)
    {
    }

    /// <param name="timeProvider">Drives the image detail cache's TTL; defaults to <see cref="TimeProvider.System"/> — overridable so tests can advance the clock without a real 30s wait.</param>
    public ImageManager(IContainerRuntime runtime, EventBus events, CiderOptions options, ILogger<ImageManager> logger, TimeProvider timeProvider)
    {
        _runtime = runtime;
        _events = events;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <param name="nameFilter">
    /// The pre-1.25 singular <c>?filter=</c> query parameter, which docker-py and older clients still
    /// send: a repository-name match (dockerd folds it into the <c>reference</c> filter), applied on
    /// top of <paramref name="filters"/>. Optional and last so the existing call sites keep working.
    /// </param>
    public async Task<IReadOnlyList<ImageSummary>> ListAsync(
        bool all,
        Filters filters,
        bool digests,
        CancellationToken ct,
        string? nameFilter = null)
    {
        var images = await _runtime.ListImagesAsync(ct).ConfigureAwait(false);
        var result = new List<ImageSummary>();
        foreach (var image in images)
        {
            if (!MatchesFilters(image, filters))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(nameFilter) && !MatchesNameFilter(image, nameFilter))
            {
                continue;
            }

            result.Add(ToSummary(image));
        }

        return result;
    }

    public async Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken ct)
    {
        var detail = await FindImageDetailAsync(reference, ct).ConfigureAwait(false)
            ?? throw DockerErrors.NoSuchImage(reference);
        return ToInspectResponse(detail);
    }

    /// <param name="onRuntimeCall">
    /// Invoked once per actual runtime round trip this resolution makes (a cache hit makes none) —
    /// <c>ContainerManager.CreateAsync</c> uses it to log the per-create runtime call count.
    /// </param>
    public async Task<RuntimeImageDetail> EnsureImageAsync(
        string reference,
        string? platform,
        RegistryAuth? auth,
        bool pullIfMissing,
        IProgress<ProgressEvent>? progress,
        CancellationToken ct,
        Action? onRuntimeCall = null)
    {
        var existing = await FindImageDetailAsync(reference, ct, onRuntimeCall).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        if (!pullIfMissing)
        {
            throw DockerErrors.NoSuchImage(reference);
        }

        var normalized = ImageReference.Parse(reference).Normalize().ToString();
        onRuntimeCall?.Invoke();
        try
        {
            await _runtime.PullImageAsync(normalized, platform, auth, progress ?? NullProgress, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        InvalidateImageCache();
        return await FindImageDetailAsync(reference, ct, onRuntimeCall).ConfigureAwait(false)
            ?? throw DockerErrors.NoSuchImage(reference);
    }

    public async Task PullAsync(
        string reference,
        string? tag,
        string? platform,
        RegistryAuth? auth,
        IProgress<JsonMessage> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var parsed = ImageReference.Parse(reference);
        if (!string.IsNullOrEmpty(tag) && parsed.Tag is null && parsed.Digest is null)
        {
            parsed = parsed with { Tag = tag };
        }

        var normalized = parsed.Normalize();
        var familiar = normalized.Familiar();
        var pullId = normalized.Tag ?? normalized.Digest ?? "latest";

        var existedBefore = await FindImageDetailAsync(familiar, ct).ConfigureAwait(false) is not null;

        // Real dockerd only degrades a missing manifest to an in-stream error once progress has
        // already reached the client; before that it answers with a plain 404. So the synthetic
        // "Pulling from ..." header is held back until the runtime itself reports something
        // (proving the pull is under way) or finishes — if it fails before either, nothing has been
        // written yet and the failure can still become a normal HTTP error (ImageRoutes/DockerResults
        // start the NDJSON response lazily for exactly this).
        var headerReported = false;
        void ReportHeaderOnce()
        {
            if (headerReported)
            {
                return;
            }

            headerReported = true;
            progress.Report(new JsonMessage { Status = $"Pulling from {normalized.Path}", Id = pullId });
        }

        // A terminal error-only event is not progress: releasing the header for it — or writing it —
        // would start the response and cost a pull that never got under way its 404. Runtimes report
        // one immediately before throwing, and that throw is handled below; should one ever report it
        // without failing, it is flushed with the rest so nothing is lost.
        JsonMessage? pendingError = null;
        var relay = new SynchronousProgress<ProgressEvent>(e =>
        {
            var message = ToJsonMessage(e);
            if (e.Error is not null && e.Status is null && e.Id is null && e.Current is null && e.Total is null)
            {
                pendingError ??= message;
                return;
            }

            ReportHeaderOnce();
            progress.Report(message);
        });
        try
        {
            await _runtime.PullImageAsync(normalized.ToString(), platform, auth, relay, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            if (!headerReported)
            {
                throw ex.Kind == RuntimeErrorKind.NotFound
                    ? DockerErrors.ManifestUnknown(familiar)
                    : ex.ToDockerError();
            }

            progress.Report(new JsonMessage { Error = ex.Message, ErrorDetail = new JsonError { Message = ex.Message } });
            throw ex.ToDockerError();
        }

        InvalidateImageCache();
        ReportHeaderOnce();
        if (pendingError is not null)
        {
            progress.Report(pendingError);
        }

        var afterDetail = await FindImageDetailAsync(familiar, ct).ConfigureAwait(false);
        var digest = afterDetail?.RepoDigests.Select(ExtractDigest).FirstOrDefault(d => d is not null)
            ?? (afterDetail is not null ? afterDetail.Id : null);
        if (digest is not null)
        {
            progress.Report(new JsonMessage { Status = $"Digest: {digest}" });
        }

        progress.Report(new JsonMessage
        {
            Status = existedBefore
                ? $"Status: Image is up to date for {familiar}"
                : $"Status: Downloaded newer image for {familiar}",
        });

        _events.Publish(DockerEvents.Image("pull", afterDetail?.Id ?? familiar, familiar));
    }

    public async Task PushAsync(string reference, string? tag, RegistryAuth? auth, IProgress<JsonMessage> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var parsed = ImageReference.Parse(reference);
        if (!string.IsNullOrEmpty(tag))
        {
            parsed = parsed with { Tag = tag };
        }

        var normalized = parsed.Normalize();

        // Docker 404s before any progress line when the image being pushed doesn't exist locally at
        // all (no registry round trip needed to know that); once the runtime push itself starts, a
        // failure degrades to an in-stream error like every other kind.
        if (await FindImageDetailAsync(normalized.Familiar(), ct).ConfigureAwait(false) is null)
        {
            throw DockerErrors.NoSuchImage(reference);
        }

        progress.Report(new JsonMessage { Status = $"The push refers to repository [{normalized.Name}]" });

        var relay = new SynchronousProgress<ProgressEvent>(e => progress.Report(ToJsonMessage(e)));
        try
        {
            await _runtime.PushImageAsync(normalized.ToString(), auth, relay, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            progress.Report(new JsonMessage { Error = ex.Message, ErrorDetail = new JsonError { Message = ex.Message } });
            throw ex.ToDockerError();
        }

        progress.Report(new JsonMessage { Status = $"{normalized.Tag ?? "latest"}: digest: {normalized.Digest ?? "unknown"} size: 0" });
        _events.Publish(DockerEvents.Image("push", normalized.ToString(), normalized.Familiar()));
    }

    public async Task TagAsync(string reference, string repo, string? tag, CancellationToken ct)
    {
        var source = await FindImageDetailAsync(reference, ct).ConfigureAwait(false)
            ?? throw DockerErrors.NoSuchImage(reference);
        var target = (repo + (string.IsNullOrEmpty(tag) ? "" : $":{tag}"));
        var targetNormalized = ImageReference.Parse(target).Normalize().ToString();

        try
        {
            await _runtime.TagImageAsync(RuntimeReferenceFor(source, reference), targetNormalized, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        InvalidateImageCache();
        _events.Publish(DockerEvents.Image("tag", source.Id, ImageReference.Parse(targetNormalized).Familiar()));
    }

    public async Task<IReadOnlyList<ImageDeleteResponseItem>> RemoveAsync(string reference, bool force, bool noPrune, CancellationToken ct)
    {
        var image = await FindImageDetailAsync(reference, ct).ConfigureAwait(false)
            ?? throw DockerErrors.NoSuchImage(reference);

        var items = new List<ImageDeleteResponseItem>();
        var tagBeingRemoved = TryNormalizedTag(reference);

        // The runtime reports every reference of the digest (Apple lists one row per reference; the
        // adapter merges them), so the same tag in two spellings must not count as two references.
        var allReferences = image.References
            .Select(NormalizedOf)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var otherReferences = tagBeingRemoved is null
            ? allReferences
            : allReferences.Where(r => !string.Equals(r, tagBeingRemoved, StringComparison.Ordinal)).ToList();

        // Two separate Docker rules, deliberately not collapsed into one flag.
        //
        // 1. The image is deleted only when the reference is an id or the last reference left; any
        //    other reference still pointing at the digest turns the removal into a plain untag.
        // 2. The running-container conflict is gated by the *repository name* count instead — the
        //    classic store's `isSingleReference` counts distinct repository names, not tags, so
        //    two tags of the SAME repository (repo:a, repo:b) still answer 409 while a container
        //    runs off the image, whereas a digest shared across two DIFFERENT repositories untags
        //    without complaint. `force` overrides that conflict, never the delete decision —
        //    `docker rmi -f <tag>` on a multi-reference image still only untags.
        var repositoryNameCount = allReferences.Select(RepositoryNameOf).Distinct(StringComparer.Ordinal).Count();
        var wouldDelete = tagBeingRemoved is null || repositoryNameCount <= 1;
        var deleteAll = tagBeingRemoved is null || otherReferences.Count == 0;

        // A running container only blocks the removal Docker treats as a delete. It untags one of
        // several repositories happily while a container runs off the image, so the conflict check
        // belongs behind `wouldDelete`, not in front of it.
        if (wouldDelete && !force)
        {
            await ThrowIfUsedByRunningContainerAsync(image, reference, ct).ConfigureAwait(false);
        }

        if (tagBeingRemoved is not null)
        {
            items.Add(new ImageDeleteResponseItem { Untagged = ImageReference.Parse(tagBeingRemoved).Familiar() });
        }

        try
        {
            if (deleteAll)
            {
                foreach (var extra in VisibleReferences(otherReferences))
                {
                    items.Add(new ImageDeleteResponseItem { Untagged = ImageReference.Parse(extra).Familiar() });
                }

                // Apple's `image delete <ref>` drops that one reference and only reclaims the blobs
                // with the last of them, so every reference has to go for the image to be deleted.
                var targets = new List<string>();
                if (tagBeingRemoved is not null)
                {
                    targets.Add(tagBeingRemoved);
                }

                targets.AddRange(otherReferences);
                if (targets.Count == 0)
                {
                    targets.Add(RuntimeReferenceFor(image, reference));
                }

                RuntimeException? missing = null;
                var removed = false;
                foreach (var target in targets)
                {
                    try
                    {
                        await _runtime.RemoveImageAsync(target, force, ct).ConfigureAwait(false);
                        removed = true;
                    }
                    catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
                    {
                        // A reference the runtime no longer knows is not a failure as long as one
                        // of the others did remove the image.
                        missing ??= ex;
                    }
                }

                if (!removed && missing is not null)
                {
                    throw missing;
                }

                items.Add(new ImageDeleteResponseItem { Deleted = image.Id });
                _events.Publish(DockerEvents.Image("delete", image.Id, tagBeingRemoved is null ? image.Id : ImageReference.Parse(tagBeingRemoved).Familiar()));
            }
            else
            {
                await _runtime.RemoveImageAsync(tagBeingRemoved!, force, ct).ConfigureAwait(false);
                _events.Publish(DockerEvents.Image("untag", image.Id, ImageReference.Parse(tagBeingRemoved!).Familiar()));
            }
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        InvalidateImageCache();
        return items;
    }

    /// <summary>
    /// Docker's <c>rmi</c> conflict: a running (or stopping) container pins the image, so deleting it
    /// needs <c>--force</c>. Only a removal that would really delete the image can hit this — an
    /// untag of one of several tags never does.
    /// </summary>
    private async Task ThrowIfUsedByRunningContainerAsync(RuntimeImageDetail image, string reference, CancellationToken ct)
    {
        var containers = await _runtime.ListContainersAsync(ct).ConfigureAwait(false);
        var blocker = containers.FirstOrDefault(c =>
            c.State is RuntimeContainerState.Running or RuntimeContainerState.Stopping &&
            IsBoundTo(c, image));

        if (blocker is null)
        {
            return;
        }

        var containerDisplayId = ContainerIdentity.ReadDockerId(blocker.Labels) is { } dockerId
            ? DockerId.Short(dockerId)
            : blocker.RuntimeId;
        var imageDisplayId = ShortDigest(image.Id);
        throw DockerErrors.Conflict(
            $"conflict: unable to remove repository reference \"{reference}\" (must force) - container {containerDisplayId} is using its referenced image {imageDisplayId}");
    }

    public async Task<IReadOnlyList<ImageHistoryItem>> HistoryAsync(string reference, CancellationToken ct)
    {
        var detail = await FindImageDetailAsync(reference, ct).ConfigureAwait(false)
            ?? throw DockerErrors.NoSuchImage(reference);

        var created = detail.Created.HasValue ? Time.DockerTime.UnixSeconds(detail.Created.Value) : 0;
        var tags = VisibleReferences(detail.References).Select(r => ImageReference.Parse(r).Familiar()).ToList();

        // Docker builds this from the image config's `history` array, newest entry first, one row per
        // build instruction — including the ones that produced no layer. Apple carries that array
        // through verbatim (`container image inspect` -> variants[i].config.history), so the
        // instruction text, its comment and its own timestamp are all real values here.
        var history = detail.History;
        if (history.Count == 0)
        {
            // No history in the config (a `container image load` of a minimal image, say): fall back
            // to one row per layer, which is at least the right shape.
            var layers = detail.Layers.Count > 0 ? detail.Layers.Count : 1;
            return [.. Enumerable.Range(0, layers).Select(i => new ImageHistoryItem
            {
                Id = i == 0 ? detail.Id : "<missing>",
                Created = created,
                CreatedBy = "",
                Tags = i == 0 ? tags : null,
                Size = 0,
                Comment = "",
            })];
        }

        // Per-row `Size`: dockerd's own algorithm (there is no per-row size in the config; it is
        // derived). Walk `history[]` newest-first — which is exactly this loop's direction, since
        // `detail.History` is stored oldest-first — and for every entry that is not `EmptyLayer`,
        // consume the next manifest layer descriptor from the end (`detail.LayerSizes`, also
        // oldest-first, so "from the end" is newest-first too); an `EmptyLayer` entry reports 0. The
        // running total this produces equals the sum of `LayerSizes` exactly when every non-empty
        // entry got a real layer. `LayerSizes` is empty when the engine could not report per-layer
        // sizes at all, in which case every row is an honest 0 rather than a fabricated number — see
        // the README limitation.
        var layerSizes = detail.LayerSizes;
        var nextLayerIndex = layerSizes.Count - 1;

        var items = new List<ImageHistoryItem>(history.Count);
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            var newest = i == history.Count - 1;

            long size = 0;
            if (!entry.EmptyLayer && nextLayerIndex >= 0)
            {
                size = layerSizes[nextLayerIndex];
                nextLayerIndex--;
            }

            items.Add(new ImageHistoryItem
            {
                // Only the topmost row carries the image id; Docker shows `<missing>` for the rest,
                // because the intermediate image ids are not recoverable from the config alone.
                Id = newest ? detail.Id : "<missing>",
                Created = entry.Created.HasValue ? Time.DockerTime.UnixSeconds(entry.Created.Value) : created,
                CreatedBy = entry.CreatedBy,
                Tags = newest ? tags : null,
                Size = size,
                Comment = entry.Comment,
            });
        }

        return items;
    }

    public async Task SaveAsync(IReadOnlyList<string> references, Stream tarOut, CancellationToken ct)
    {
        var resolved = new List<string>();
        foreach (var reference in references)
        {
            var detail = await FindImageDetailAsync(reference, ct).ConfigureAwait(false)
                ?? throw DockerErrors.NoSuchImage(reference);
            resolved.Add(RuntimeReferenceFor(detail, reference));
        }

        try
        {
            await _runtime.SaveImagesAsync(resolved, tarOut, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }
    }

    public async Task LoadAsync(Stream tarIn, IProgress<JsonMessage> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await LoadImagesAsync(tarIn, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Same runtime load as <see cref="LoadAsync"/> (<c>POST /images/load</c> delegates here, and its
    /// progress output is unchanged), but hands back the normalized references that the load actually
    /// affected — for the BuildKit control proxy (T7), which loads the tar buildkitd's <c>docker</c>
    /// exporter produced and needs the resulting references to inspect/tag the built image, rather
    /// than parse them back out of progress lines.
    /// </summary>
    /// <returns>
    /// The normalized references that appeared or were retargeted by the load: a before/after diff of
    /// <see cref="IContainerRuntime.ListImagesAsync"/>. When the diff is empty — reloading a tar whose
    /// image and tags are already present, byte for byte — the runtime's own <c>Loaded image:</c>
    /// names are returned instead, so a re-load never comes back empty.
    /// </returns>
    public async Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarIn, IProgress<JsonMessage>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.TmpDir);
        var tmpFile = Path.Combine(_options.TmpDir, $"load-{Guid.NewGuid():N}.tar");
        try
        {
            await using (var fileStream = File.Create(tmpFile))
            {
                await tarIn.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            var before = await SnapshotImageIdsByReferenceAsync(ct).ConfigureAwait(false);

            IReadOnlyList<string> loaded;
            await using (var readStream = File.OpenRead(tmpFile))
            {
                try
                {
                    loaded = await _runtime.LoadImagesAsync(readStream, ct).ConfigureAwait(false);
                }
                catch (RuntimeException ex)
                {
                    progress?.Report(new JsonMessage { Error = ex.Message, ErrorDetail = new JsonError { Message = ex.Message } });
                    throw ex.ToDockerError();
                }
            }

            InvalidateImageCache();
            foreach (var reference in loaded)
            {
                var familiar = ImageReference.TryParse(reference, out var parsed) ? parsed.Familiar() : reference;
                progress?.Report(new JsonMessage { Stream = $"Loaded image: {familiar}\n" });
                _events.Publish(DockerEvents.Image("load", reference, familiar));
            }

            IReadOnlyList<string> references = loaded;
            if (before is not null)
            {
                var after = await SnapshotImageIdsByReferenceAsync(ct).ConfigureAwait(false);
                if (after is not null)
                {
                    var changed = after
                        .Where(entry => !before.TryGetValue(entry.Key, out var beforeId) || !string.Equals(beforeId, entry.Value, StringComparison.Ordinal))
                        .Select(entry => entry.Key)
                        .ToList();

                    references = changed.Count > 0 ? changed : loaded;
                }
            }

            return references
                .Select(reference => ImageReference.TryParse(reference, out var parsed) ? parsed.Normalize().ToString() : reference)
                .ToList();
        }
        finally
        {
            TryDeleteFile(tmpFile);
        }
    }

    /// <summary>
    /// Every reference known to the runtime right now, mapped to the image id it points at. Used only
    /// as a before/after diff around an already-successful <see cref="LoadImagesAsync"/> load. A caught
    /// listing failure here (e.g. a poisoned Apple image store — cider-ede.24 comment 66) means "no
    /// snapshot available" — it is NOT the same as a genuinely empty listing, and must not be conflated
    /// with one (comment 66's ban on synthesizing an empty success out of a failure). Returns null in
    /// that case, so <c>LoadImagesAsync</c> can tell "unknown" from "nothing" and skip the diff
    /// entirely, falling back to the runtime's own <c>Loaded image:</c> names.
    /// </summary>
    private async Task<Dictionary<string, string>?> SnapshotImageIdsByReferenceAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyList<RuntimeImage> images;
        try
        {
            images = await _runtime.ListImagesAsync(ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "could not list images while snapshotting references for an image load diff");
            return null;
        }

        foreach (var image in images)
        {
            foreach (var reference in image.References)
            {
                map[reference] = image.Id;
            }
        }

        return map;
    }

    /// <summary>
    /// <c>POST /commit</c>. Apple <c>container</c> has no commit primitive, so the container's whole
    /// root filesystem is exported (<see cref="IContainerRuntime.ExportContainerAsync"/>), wrapped in
    /// a one-layer OCI image whose config is the container's effective configuration overlaid with
    /// <paramref name="changes"/>, and handed back to the runtime through <c>image load</c>. The
    /// result is a genuinely runnable image, but a <em>flattened</em> one: unlike Docker's commit it
    /// shares no layer with the parent image (documented in README's limitations).
    /// </summary>
    /// <returns>The new image's id (<c>sha256:…</c>).</returns>
    public async Task<string> CommitAsync(
        ContainerRecord container,
        string? repo,
        string? tag,
        string? comment,
        string? author,
        IReadOnlyList<string> changes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(changes);

        var sourceReference = container.ImageId.Length > 0 ? container.ImageId : container.ImageRef;
        var source = await FindImageDetailAsync(sourceReference, ct).ConfigureAwait(false);

        // Parsed first, so an unsupported directive is a 400 before anything is exported.
        var config = ImageChanges.Apply(ConfigOf(container, source), changes);
        var reference = TargetReference(repo, tag);

        var spec = new OciImageSpec
        {
            Reference = reference,
            Config = config,
            Architecture = string.IsNullOrEmpty(source?.Architecture) ? "arm64" : source.Architecture,
            Os = string.IsNullOrEmpty(source?.Os) ? "linux" : source.Os,
            Variant = source?.Variant,
            Author = author,
            Comment = comment,
            CreatedBy = string.Join(' ', container.Entrypoint.Concat(container.Cmd)),
            Created = DateTimeOffset.UtcNow,
        };

        var id = await BuildAndLoadAsync(
            spec,
            async (rootFs, token) =>
            {
                try
                {
                    await _runtime.ExportContainerAsync(container.RuntimeId, rootFs, token).ConfigureAwait(false);
                }
                catch (RuntimeException ex)
                {
                    throw ex.ToDockerError();
                }
            },
            ct).ConfigureAwait(false);

        _events.Publish(DockerEvents.Image("commit", id, EventNameFor(reference, id)));
        return id;
    }

    /// <summary>
    /// <c>POST /images/create?fromSrc=-</c> (<c>docker import</c>): the request body is a raw root
    /// filesystem tar (optionally gzipped) which becomes the single layer of a brand new image whose
    /// configuration starts empty and is built entirely from <paramref name="changes"/>.
    /// </summary>
    /// <returns>The new image's id (<c>sha256:…</c>).</returns>
    public async Task<string> ImportAsync(
        Stream rootFsTar,
        string? repo,
        string? tag,
        string? message,
        IReadOnlyList<string> changes,
        IProgress<JsonMessage> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootFsTar);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);

        var config = ImageChanges.Apply(new ImageConfig(), changes);
        var reference = TargetReference(repo, tag);

        var spec = new OciImageSpec
        {
            Reference = reference,
            Config = config,
            Comment = message,
            CreatedBy = "Imported from -",
            Created = DateTimeOffset.UtcNow,
        };

        // The layer's diff id is the digest of the *uncompressed* tar, so a gzipped body has to be
        // decompressed on the way in (docker import accepts both).
        var body = await MaybeDecompressAsync(rootFsTar, ct).ConfigureAwait(false);
        string id;
        try
        {
            id = await BuildAndLoadAsync(spec, (destination, token) => body.CopyToAsync(destination, token), ct).ConfigureAwait(false);
        }
        finally
        {
            await body.DisposeAsync().ConfigureAwait(false);
        }

        progress.Report(new JsonMessage { Status = id });
        _events.Publish(DockerEvents.Image("import", id, EventNameFor(reference, id)));
        return id;
    }

    public async Task<ImagePruneResponse> PruneAsync(Filters filters, CancellationToken ct)
    {
        // dockerd's imagesAcceptedFilters (moby/daemon/images/image_prune.go).
        filters = (filters ?? Filters.Empty).Validate("dangling", "label", "label!", "until");

        // dockerd's `danglingOnly, err := pruneFilters.GetBoolOrDefault("dangling", true)`: absent or
        // `dangling=true` restricts the candidate set to dangling images (imageStore.Heads());
        // `dangling=false` — what `docker image prune -a` sends — widens it to every image
        // (imageStore.Map()). Before this fix the manager hard-coded the dangling-only restriction
        // and never read the filter's value at all, so `dangling=false` had no effect.
        var danglingOnly = ResolveDanglingOnly(filters);

        // Resolved once, up front, the way dockerd calls getUntilFromPruneFilters before its image
        // loop — `until` used to not be read at all here, so any value (garbage included) was
        // silently ignored.
        var until = filters.ResolveUntil(detail => $"invalid value for 'until' filter: {detail}");

        var images = await _runtime.ListImagesAsync(ct).ConfigureAwait(false);
        var containers = await _runtime.ListContainersAsync(ct).ConfigureAwait(false);

        var deleted = new List<ImageDeleteResponseItem>();
        long space = 0;
        foreach (var image in images)
        {
            if (danglingOnly && !IsDangling(image))
            {
                continue;
            }

            // IsBoundTo, not a raw c.ImageDigest/image.Id set lookup (cider-ger.19 orchestrator
            // follow-up): image.Id is now the content-addressed config digest, but a container's own
            // ImageDigest is still whatever raw digest the engine handed it at creation time (the index
            // digest on the Apple transports) — a plain set-membership check against image.Id alone
            // went stale for every image once that changed, and let `docker image prune -a` delete an
            // image still backing a running container. See IsBoundTo's own doc comment.
            if (containers.Any(c => IsBoundTo(c, image)))
            {
                continue;
            }

            if (until is not null && (image.Created is null || image.Created > until))
            {
                continue;
            }

            if (!filters.MatchesLabels(image.Labels))
            {
                continue;
            }

            try
            {
                // Apple's `image delete <ref>` only resolves a *reference*, not a bare digest (see
                // RuntimeReferenceFor), and it drops just that one reference — it only reclaims the
                // blobs once the last reference is gone (see RemoveAsync's `deleteAll` loop, which
                // this mirrors). A dangling image can carry several duplicate `cider-build-*` tags
                // that all resolved to the same content digest (the same untagged Dockerfile built
                // more than once); removing only the first of them left the digest alive under the
                // rest, so it kept reappearing as dangling until as many more `prune -f` calls as it
                // had leftover tags. Every reference has to go for the image to actually disappear.
                // cider-bci: an unparseable reference can't be resolved by `image delete`, and an
                // empty one makes the runtime adapter throw ArgumentException, which is not a
                // RuntimeException and so bypasses both catches below — that 500s the whole prune
                // instead of just skipping the one bad reference. Filter to references that actually
                // parse (TryParse only — synthetic build tags parse fine and must be kept) before
                // building the target list, and fall back to the id when none of them do.
                var parseable = image.References.Where(r => ImageReference.TryParse(r, out _)).ToList();
                var targets = parseable.Count > 0
                    ? parseable.Select(NormalizedOf).Distinct(StringComparer.Ordinal).ToList()
                    : [RuntimeReferenceFor(image, image.Id)];

                RuntimeException? missing = null;
                var removedAny = false;
                foreach (var target in targets)
                {
                    try
                    {
                        await _runtime.RemoveImageAsync(target, false, ct).ConfigureAwait(false);
                        removedAny = true;
                    }
                    catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
                    {
                        // A reference the runtime no longer knows is not a failure as long as one of
                        // the others did remove the image.
                        missing ??= ex;
                    }
                }

                if (!removedAny && missing is not null)
                {
                    throw missing;
                }
            }
            catch (RuntimeException)
            {
                continue;
            }

            deleted.Add(new ImageDeleteResponseItem { Deleted = image.Id });
            space += image.Size;
            _events.Publish(DockerEvents.Image("delete", image.Id, image.Id));
        }

        if (deleted.Count > 0)
        {
            InvalidateImageCache();
        }

        // cider-ede.31 fix direction §2: the store-wide sweep runs only here — the one place the user
        // explicitly asked to reclaim space — never from RemoveAsync's own per-image delete, and exactly
        // once per prune request regardless of how many images (if any) it just removed above, not once
        // per image. Unconditional on `deleted.Count` (cider-ede.31 correction): a prune that found
        // nothing dangling to delete had still already left orphaned blobs from an earlier plain `rmi`
        // permanently unreclaimable, since PruneImagesAsync is the only reclaim path in the codebase —
        // a no-op prune must still sweep.
        await _runtime.PruneImagesAsync(ct).ConfigureAwait(false);

        return new ImagePruneResponse { ImagesDeleted = deleted, SpaceReclaimed = space };
    }

    public async Task BuildAsync(BuildRequest request, Stream tarContext, IProgress<JsonMessage> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tarContext);
        ArgumentNullException.ThrowIfNull(progress);

        if (!string.IsNullOrEmpty(request.Remote))
        {
            throw DockerErrors.NotImplemented("cider: remote build contexts are not supported");
        }

        Directory.CreateDirectory(_options.TmpDir);
        var contextDir = Path.Combine(_options.TmpDir, $"build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        try
        {
            await ExtractContextAsync(tarContext, contextDir, ct).ConfigureAwait(false);

            var dockerfilePath = ResolveDockerfilePath(contextDir, request.Dockerfile);
            if (!File.Exists(dockerfilePath))
            {
                throw DockerErrors.BadParameter($"Cannot locate specified Dockerfile: {request.Dockerfile}");
            }

            // Every other path through the engine (create, pull, tag, rmi) hands Apple the fully
            // normalized reference, so the build must too: `-t e2e/app:1` stores the image under the
            // bare name `e2e/app`, which `container create docker.io/e2e/app:1` then treats as a
            // remote reference and tries to pull ("no credentials found for host registry-1.docker.io").
            var runtimeTags = request.Tags
                .Select(tag => ImageReference.TryParse(tag, out var parsed) ? parsed.Normalize().ToString() : tag)
                .ToList();

            var spec = new Runtime.BuildSpec
            {
                ContextDir = contextDir,
                Dockerfile = request.Dockerfile,
                Tags = runtimeTags,
                BuildArgs = request.BuildArgs,
                Labels = request.Labels,
                Target = request.Target,
                Platforms = string.IsNullOrEmpty(request.Platform) ? [] : [request.Platform],
                NoCache = request.NoCache,
                Pull = request.Pull,
                Quiet = request.Quiet,
            };

            var relay = new SynchronousProgress<ProgressEvent>(e =>
            {
                if (request.Quiet)
                {
                    return;
                }

                if (e.Stream is not null)
                {
                    // The Docker-shaped closing lines belong to this manager alone (ARCHITECTURE §9):
                    // it emits them below, from the tags the *client* asked for. A runtime that also
                    // produced them — the adapter did until that was fixed — printed each one twice
                    // and leaked the synthetic tag of an untagged build, so they are dropped here.
                    if (IsBuildTerminalLine(e.Stream))
                    {
                        return;
                    }

                    progress.Report(new JsonMessage { Stream = e.Stream });
                }
                else if (e.Error is not null)
                {
                    progress.Report(new JsonMessage { Error = e.Error, ErrorDetail = new JsonError { Message = e.Error } });
                }
                else if (e.Status is not null)
                {
                    progress.Report(new JsonMessage { Status = e.Status, Id = e.Id });
                }
            });

            string imageId;
            try
            {
                imageId = await _runtime.BuildImageAsync(spec, relay, ct).ConfigureAwait(false);
            }
            catch (RuntimeException ex)
            {
                progress.Report(new JsonMessage { Error = ex.Message, ErrorDetail = new JsonError { Message = ex.Message } });
                progress.Report(new JsonMessage { ErrorDetail = new JsonError { Message = ex.Message } });
                throw ex.ToDockerError();
            }

            InvalidateImageCache();
            if (request.Quiet)
            {
                progress.Report(new JsonMessage { Stream = $"{imageId}\n" });
            }
            else
            {
                progress.Report(new JsonMessage { Aux = new BuildResultAux { ID = imageId } });
                var shortId = ShortDigest(imageId);
                progress.Report(new JsonMessage { Stream = $"Successfully built {shortId}\n" });
                foreach (var tag in request.Tags)
                {
                    progress.Report(new JsonMessage { Stream = $"Successfully tagged {tag}\n" });
                }
            }

            _events.Publish(DockerEvents.Image("build", imageId, request.Tags.FirstOrDefault()));
        }
        catch (DockerApiException)
        {
            throw;
        }
        catch (IOException ex)
        {
            var message = $"error building image: {ex.Message}";
            progress.Report(new JsonMessage { Error = message, ErrorDetail = new JsonError { Message = message } });
            throw DockerErrors.BadParameter(message);
        }
        finally
        {
            TryDeleteDirectory(contextDir);
        }
    }

    public async Task<AuthResponse> LoginAsync(AuthConfig auth, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var registryAuth = new RegistryAuth
        {
            Username = auth.Username,
            Password = auth.Password,
            ServerAddress = auth.ServerAddress,
            IdentityToken = auth.IdentityToken,
        };

        try
        {
            await _runtime.LoginAsync(registryAuth, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        return new AuthResponse { Status = "Login Succeeded", IdentityToken = "" };
    }

    public async Task<int> CountAsync(CancellationToken ct) =>
        (await _runtime.ListImagesAsync(ct).ConfigureAwait(false)).Count;

    // ---- helpers ------------------------------------------------------

    private static readonly IProgress<ProgressEvent> NullProgress = new Progress<ProgressEvent>();

    /// <param name="onRuntimeCall">See <see cref="EnsureImageAsync"/>.</param>
    private async Task<RuntimeImageDetail?> FindImageDetailAsync(string reference, CancellationToken ct, Action? onRuntimeCall = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // Captured once, before the cache lookup and before the first runtime call below, so a
        // concurrent mutation (pull/rm/tag/...) that bumps the version while a runtime call here is
        // still in flight is never lost: CacheDetail compares the *current* version against this
        // captured one and skips its write if they no longer match, rather than re-reading the
        // version fresh right before the write (which would let it stamp a result fetched before the
        // bump as belonging to the version after it).
        var version = Interlocked.Read(ref _imageCacheVersion);

        var cacheKey = CacheKeyFor(reference);
        if (cacheKey is not null && TryGetCachedDetail(cacheKey, out var cached))
        {
            return cached;
        }

        onRuntimeCall?.Invoke();
        var direct = await _runtime.InspectImageAsync(reference, ct).ConfigureAwait(false);
        if (direct is not null)
        {
            CacheDetail(direct, version);
            return direct;
        }

        onRuntimeCall?.Invoke();
        var images = await _runtime.ListImagesAsync(ct).ConfigureAwait(false);
        var candidate = MatchImage(images, reference);
        if (candidate is null)
        {
            return null;
        }

        if (candidate is RuntimeImageDetail detail)
        {
            CacheDetail(detail, version);
            return detail;
        }

        foreach (var candidateRef in candidate.References.Append(candidate.Id))
        {
            onRuntimeCall?.Invoke();
            var byRef = await _runtime.InspectImageAsync(candidateRef, ct).ConfigureAwait(false);
            if (byRef is not null)
            {
                CacheDetail(byRef, version);
                return byRef;
            }
        }

        return null;
    }

    /// <summary>
    /// The stable cache key for an exact reference — the image id (<c>sha256:…</c>) or the fully
    /// normalized <c>name:tag</c>/<c>name@digest</c> form. <c>null</c> for a reference that cannot
    /// be resolved without scanning the whole image list (a short hex prefix, which is ambiguous
    /// until every image is known; or one that fails to parse at all).
    /// </summary>
    private static string? CacheKeyFor(string reference)
    {
        var stripped = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference["sha256:".Length..] : reference;
        if (DockerId.IsFullId(stripped))
        {
            return "sha256:" + stripped;
        }

        if (DockerId.IsHexPrefix(stripped))
        {
            return null;
        }

        return ImageReference.TryParse(reference, out var parsed) ? parsed.Normalize().ToString() : null;
    }

    private bool TryGetCachedDetail(string cacheKey, out RuntimeImageDetail detail)
    {
        if (_imageCache.TryGetValue(cacheKey, out var cached)
            && cached.Version == Interlocked.Read(ref _imageCacheVersion)
            && _timeProvider.GetUtcNow() - cached.StoredAt < CacheTtl)
        {
            detail = cached.Detail;
            return true;
        }

        detail = null!;
        return false;
    }

    /// <summary>
    /// Indexes a freshly resolved detail under its id and every reference it carries, stamped with
    /// <paramref name="version"/> — the cache version <see cref="FindImageDetailAsync"/> captured
    /// before making the runtime call that produced <paramref name="detail"/>, not a fresh read.
    /// If the cache has since been invalidated (<paramref name="version"/> no longer matches), the
    /// write is skipped outright: the detail behind it is already stale, and stamping it with
    /// today's version would wrongly resurrect it as current.
    /// </summary>
    private void CacheDetail(RuntimeImageDetail detail, long version)
    {
        if (version != Interlocked.Read(ref _imageCacheVersion))
        {
            return;
        }

        var entry = new CachedImageDetail(detail, version, _timeProvider.GetUtcNow());
        _imageCache["sha256:" + IdWithoutPrefix(detail.Id)] = entry;
        foreach (var reference in detail.References)
        {
            if (ImageReference.TryParse(reference, out var parsed))
            {
                _imageCache[parsed.Normalize().ToString()] = entry;
            }
        }
    }

    /// <summary>
    /// Bumps the cache's version so every entry currently in <see cref="_imageCache"/> reads as
    /// stale, and drops them outright — called after any runtime call that can change what an image
    /// reference resolves to. Clearing here (rather than leaving superseded entries to be
    /// overwritten piecemeal as each reference happens to be re-resolved) keeps the dictionary from
    /// accumulating entries no live version will ever match again. Bumping the version first and
    /// clearing after would still be race-free, but ordering it this way — clear, then bump — means a
    /// write from a read already in flight can land in between only under its own pre-bump version,
    /// which <see cref="TryGetCachedDetail"/> and <see cref="CacheDetail"/> already treat as current
    /// until the increment below lands: the reader stamps the version it captured before its own
    /// runtime call, and <see cref="CacheDetail"/> re-checks that captured version against the
    /// current one right before writing, so a bump that lands while the read is still in flight makes
    /// it skip the write instead of resurrecting stale data as current.
    /// </summary>
    private void InvalidateImageCache()
    {
        _imageCache.Clear();
        Interlocked.Increment(ref _imageCacheVersion);
    }

    private static RuntimeImage? MatchImage(IReadOnlyList<RuntimeImage> images, string reference)
    {
        var stripped = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference["sha256:".Length..] : reference;
        if (DockerId.IsFullId(stripped))
        {
            var byId = images.FirstOrDefault(i => IdWithoutPrefix(i.Id) == stripped);
            if (byId is not null)
            {
                return byId;
            }
        }
        else if (DockerId.IsHexPrefix(stripped) && stripped.Length >= 4)
        {
            var matches = images.Where(i => IdWithoutPrefix(i.Id).StartsWith(stripped, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        if (!ImageReference.TryParse(reference, out var parsed))
        {
            return null;
        }

        var familiar = parsed.Normalize().Familiar();
        var normalizedForm = parsed.Normalize().ToString();
        foreach (var image in images)
        {
            foreach (var r in image.References)
            {
                if (!ImageReference.TryParse(r, out var rp))
                {
                    continue;
                }

                if (string.Equals(rp.Normalize().Familiar(), familiar, StringComparison.Ordinal) ||
                    string.Equals(rp.Normalize().ToString(), normalizedForm, StringComparison.Ordinal))
                {
                    return image;
                }
            }
        }

        return null;
    }

    private static bool MatchesFilters(RuntimeImage image, Filters filters)
    {
        if (filters.IsEmpty)
        {
            return true;
        }

        if (!filters.MatchesLabels(image.Labels))
        {
            return false;
        }

        if (filters.Contains("dangling"))
        {
            var wantsDangling = filters.Get("dangling").Contains("true");
            if (wantsDangling != IsDangling(image))
            {
                return false;
            }
        }

        if (filters.Contains("reference"))
        {
            // Only references a client can actually see may match: the synthetic tag of an untagged
            // build is hidden everywhere else, so `--filter reference=cider-build-*` must not
            // surface it either.
            var familiars = VisibleReferences(image.References)
                .Select(r => ImageReference.Parse(r).Normalize().Familiar());
            if (!filters.MatchAny("reference", pattern => familiars.Any(f => f.Contains(pattern, StringComparison.Ordinal))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The legacy singular <c>?filter=</c> parameter of <c>GET /images/json</c>. dockerd folds it
    /// into the <c>reference</c> filter, whose matcher is Go's <c>path.Match</c> glob over the
    /// *familiar* reference — so it is a name match (optionally with <c>*</c>/<c>?</c> wildcards),
    /// not the substring search our <c>reference</c> filter does: <c>filter=busybox</c> matches
    /// <c>busybox:latest</c>, <c>filter=box</c> matches nothing.
    /// <para>
    /// dockerd gates the parameter to API &lt; 1.41, but we honour it at every version: the clients
    /// that still send it (docker-py) negotiate a modern version on the same connection, and no
    /// modern client sends <c>filter=</c> at all, so accepting it late can only help.
    /// </para>
    /// </summary>
    private static bool MatchesNameFilter(RuntimeImage image, string filter)
    {
        foreach (var reference in VisibleReferences(image.References))
        {
            var normalized = ImageReference.Parse(reference).Normalize();
            var familiar = normalized.Familiar();
            var qualified = normalized.ToString();

            // The familiar spelling is what dockerd matches; the fully qualified one is accepted too
            // so `filter=docker.io/library/busybox` behaves like `filter=busybox`.
            if (GlobMatches(filter, familiar) ||
                GlobMatches(filter, FamiliarNameOf(familiar)) ||
                GlobMatches(filter, qualified) ||
                GlobMatches(filter, FamiliarNameOf(qualified)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops the <c>:tag</c> / <c>@digest</c> suffix of a familiar reference.</summary>
    private static string FamiliarNameOf(string familiar)
    {
        var digest = familiar.IndexOf('@', StringComparison.Ordinal);
        var name = digest < 0 ? familiar : familiar[..digest];
        var colon = name.LastIndexOf(':');
        return colon > name.LastIndexOf('/') ? name[..colon] : name;
    }

    /// <summary>
    /// Go's <c>path.Match</c> semantics as used by Docker's reference filter: <c>*</c> matches any
    /// run of characters other than '/', <c>?</c> matches exactly one, everything else is literal.
    /// </summary>
    private static bool GlobMatches(string pattern, string value)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }

        var regex = new System.Text.StringBuilder("^");
        foreach (var c in pattern)
        {
            regex.Append(c switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
            });
        }

        regex.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            regex.ToString(),
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// dockerd's <c>filters.Args.GetBoolOrDefault("dangling", true)</c>: a bare "0"/"false" clears
    /// dangling-only, "1"/"true" (or the filter's absence) keeps it, anything else — including both a
    /// true-ish and a false-ish value together — is rejected with the same <c>invalidFilter</c>
    /// dockerd raises for it.
    /// </summary>
    private static bool ResolveDanglingOnly(Filters filters)
    {
        var values = filters.Get("dangling");
        if (values.Count == 0)
        {
            return true;
        }

        var isFalse = values.Contains("0") || values.Contains("false");
        var isTrue = values.Contains("1") || values.Contains("true");
        if (isFalse == isTrue)
        {
            throw DockerErrors.InvalidFilterValue("dangling", values);
        }

        return isTrue;
    }

    /// <summary>
    /// An image is dangling once none of its references are real: either it has no references at
    /// all, or every reference is one <see cref="VisibleReferences"/> itself would hide — the
    /// synthetic tag the runtime adapter mints for a <c>docker build</c> with no <c>-t</c> (real
    /// Docker shows that as <c>&lt;none&gt;:&lt;none&gt;</c>), or a reference that fails to parse as
    /// an image reference at all. Defined in terms of <see cref="VisibleReferences"/> itself
    /// (cider-ede.32) rather than restating its own condition: the two used to diverge for a
    /// reference that is neither a synthetic tag nor parseable — <c>VisibleReferences</c> hid it (the
    /// image displayed as <c>&lt;none&gt;</c>) while this checked only <see cref="SyntheticBuildTag"/>
    /// and so still counted it as a real tag, so an image could show as <c>&lt;none&gt;</c> yet be
    /// excluded from <c>--filter dangling=true</c>. Defining dangling as "nothing visible" makes that
    /// drift impossible by construction: whatever <c>VisibleReferences</c> hides from display also
    /// stops counting as a tag here.
    /// </summary>
    private static bool IsDangling(RuntimeImage image) =>
        !VisibleReferences(image.References).Any();

    private static IEnumerable<string> VisibleReferences(IReadOnlyList<string> references) =>
        references.Where(r => !SyntheticBuildTag.IsSyntheticBuildTag(r) && ImageReference.TryParse(r, out _));

    /// <summary>
    /// The classic builder's closing <c>Successfully built|tagged …</c> lines, which this manager
    /// synthesises itself, so a runtime that reports them too must not have them relayed.
    /// </summary>
    private static bool IsBuildTerminalLine(string stream) =>
        stream.StartsWith("Successfully built ", StringComparison.Ordinal) ||
        stream.StartsWith("Successfully tagged ", StringComparison.Ordinal);

    private static ImageSummary ToSummary(RuntimeImage image) => new()
    {
        Id = image.Id,
        ParentId = "",
        RepoTags = VisibleReferences(image.References)
            .Select(r => ImageReference.Parse(r).Normalize().Familiar())
            .Distinct(StringComparer.Ordinal)
            .ToList(),
        RepoDigests = BuildRepoDigests(image),
        Created = image.Created.HasValue ? Time.DockerTime.UnixSeconds(image.Created.Value) : 0,
        Size = image.Size,
        Labels = new Dictionary<string, string>(image.Labels),
    };

    /// <summary>
    /// Docker's <c>RepoDigests</c> names the manifest digest the reference actually resolves to, not
    /// the image id — before cider-ger.19 those happened to be the same value (Apple's raw index
    /// digest, which is what <c>image.Id</c> used to be), so keying off <c>image.Id</c> here read as
    /// correct by coincidence. Now that <c>image.Id</c> is the content-addressed config digest instead
    /// (stable across reloads, but not a digest a registry would ever hand back for a pull/push of this
    /// reference), this prefers <see cref="RuntimeImage.IndexDigests"/> — the raw digest(s) the engine
    /// actually associates with the reference — falling back to <c>image.Id</c> only when the engine
    /// reported none (orchestrator follow-up item 3: reconcile, not revert, since <c>image.Id</c> is
    /// still a better fallback than nothing).
    /// </summary>
    private static List<string> BuildRepoDigests(RuntimeImage image)
    {
        var digest = image.IndexDigests.Count > 0 ? image.IndexDigests[0] : image.Id;
        var names = VisibleReferences(image.References)
            .Select(r => ImageReference.Parse(r).Normalize().Name)
            .Distinct(StringComparer.Ordinal);
        return names.Select(name => $"{name}@{digest}").ToList();
    }

    private static ImageInspectResponse ToInspectResponse(RuntimeImageDetail detail) => new()
    {
        Id = detail.Id,
        RepoTags = VisibleReferences(detail.References)
            .Select(r => ImageReference.Parse(r).Normalize().Familiar())
            .Distinct(StringComparer.Ordinal)
            .ToList(),
        RepoDigests = BuildRepoDigests(detail),
        Parent = "",

        // Docker's image `Comment` is the comment of the newest history entry — which is exactly what
        // `docker commit --message` writes and what Apple carries through, so it can be reported for
        // real instead of always answering "".
        Comment = detail.History.Count > 0 ? detail.History[^1].Comment : "",
        Created = detail.Created.HasValue ? Time.DockerTime.Format(detail.Created.Value) : Time.DockerTime.ZeroTime,
        Container = "",
        DockerVersion = "",
        Author = detail.Author ?? "",
        Config = new ContainerConfig
        {
            Env = detail.Config.Env.ToList(),
            Cmd = detail.Config.Cmd.ToList(),
            Entrypoint = detail.Config.Entrypoint.Count > 0 ? detail.Config.Entrypoint.ToList() : null,
            WorkingDir = detail.Config.WorkingDir ?? "",
            User = detail.Config.User ?? "",
            ExposedPorts = detail.Config.ExposedPorts.ToDictionary(p => p, _ => EmptyStruct.Instance),
            Volumes = detail.Config.Volumes.ToDictionary(v => v, _ => EmptyStruct.Instance),
            Labels = new Dictionary<string, string>(detail.Config.Labels),
            StopSignal = detail.Config.StopSignal,
            Healthcheck = detail.Config.Healthcheck is null
                ? null
                : new HealthConfig
                {
                    Test = detail.Config.Healthcheck.Test.ToList(),
                    Interval = detail.Config.Healthcheck.Interval,
                    Timeout = detail.Config.Healthcheck.Timeout,
                    Retries = detail.Config.Healthcheck.Retries,
                    StartPeriod = detail.Config.Healthcheck.StartPeriod,
                },
        },
        Architecture = string.IsNullOrEmpty(detail.Architecture) ? "arm64" : detail.Architecture,
        Variant = detail.Variant ?? "",
        Os = string.IsNullOrEmpty(detail.Os) ? "linux" : detail.Os,
        Size = detail.Size,
        // VirtualSize is deliberately left unset: it is the one image field dockerd gates on the
        // requested API version, so ImageRoutes fills it in for <= 1.43 callers and leaves it
        // omitted for >= 1.44.
        GraphDriver = new GraphDriverData(),
        RootFS = new RootFS { Layers = detail.Layers.ToList() },
        Metadata = new ImageMetadata(),
    };

    private static JsonMessage ToJsonMessage(ProgressEvent e)
    {
        var message = new JsonMessage { Status = e.Status, Id = e.Id, Stream = e.Stream };
        if (e.Current is not null && e.Total is > 0)
        {
            message.ProgressDetail = new JsonProgress { Current = e.Current.Value, Total = e.Total!.Value };
            message.Progress = RenderProgressBar(e.Current.Value, e.Total.Value);
        }

        if (e.Error is not null)
        {
            message.Error = e.Error;
            message.ErrorDetail = new JsonError { Message = e.Error };
        }

        return message;
    }

    private static string RenderProgressBar(long current, long total)
    {
        const int width = 30;
        var ratio = total > 0 ? Math.Clamp((double)current / total, 0, 1) : 0;
        var filled = (int)Math.Round(ratio * width);
        var bar = filled <= 0
            ? new string(' ', width)
            : (new string('=', Math.Max(0, filled - 1)) + (filled < width ? ">" : "=")).PadRight(width, ' ');
        return $"[{bar}] {current}B/{total}B";
    }

    private static bool ReferencesImage(string imageReference, RuntimeImage image)
    {
        if (string.IsNullOrEmpty(imageReference))
        {
            return false;
        }

        if (string.Equals(imageReference, image.Id, StringComparison.Ordinal))
        {
            return true;
        }

        if (!ImageReference.TryParse(imageReference, out var parsed))
        {
            return false;
        }

        var familiar = parsed.Normalize().Familiar();
        return image.References.Any(r => ImageReference.TryParse(r, out var rp) && string.Equals(rp.Normalize().Familiar(), familiar, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a container is using this image — the shared test both the rmi in-use guard
    /// (<see cref="ThrowIfUsedByRunningContainerAsync"/>) and <see cref="PruneAsync"/> apply, so a
    /// running container is recognized the same way in both places (cider-ger.19 orchestrator
    /// follow-up: before this, prune's own join only compared <see cref="RuntimeContainer.ImageDigest"/>
    /// against <see cref="RuntimeImage.Id"/> with no reference fallback at all — once <c>Id</c> became
    /// the content-addressed config digest instead of Apple's raw index digest, that join went stale
    /// for every image, and <c>docker image prune -a</c> could delete an image still backing a running
    /// container). <paramref name="container"/>.ImageDigest is the raw digest Apple handed the
    /// container at creation time (still the index digest on the Apple transports — see
    /// <see cref="RuntimeContainer.ImageDigest"/>'s own callers), so it is checked against both
    /// <see cref="RuntimeImage.Id"/> (equal only if the engine ever reports the config digest there
    /// too) and <see cref="RuntimeImage.IndexDigests"/> (the value that actually matches on the Apple
    /// transports), before falling back to matching the container's own image *reference* against the
    /// image's tags/id — the same fallback the rmi guard always had, for a container whose digest was
    /// never captured at all.
    /// </summary>
    private static bool IsBoundTo(RuntimeContainer container, RuntimeImage image)
    {
        if (!string.IsNullOrEmpty(container.ImageDigest) &&
            (string.Equals(container.ImageDigest, image.Id, StringComparison.Ordinal) ||
             image.IndexDigests.Contains(container.ImageDigest, StringComparer.Ordinal)))
        {
            return true;
        }

        return ReferencesImage(container.ImageReference, image);
    }

    private static string? TryNormalizedTag(string reference)
    {
        var stripped = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference["sha256:".Length..] : reference;
        if (DockerId.IsFullId(stripped) || DockerId.IsHexPrefix(stripped))
        {
            return null;
        }

        return ImageReference.TryParse(reference, out var parsed) ? parsed.Normalize().ToString() : null;
    }

    private static string NormalizedOf(string reference) =>
        ImageReference.TryParse(reference, out var parsed) ? parsed.Normalize().ToString() : reference;

    /// <summary>
    /// Domain + path, without tag or digest — the "repository name" Docker's classic store
    /// counts in <c>isSingleReference</c> to decide whether a removal has to answer 409 for a
    /// running container (see <see cref="RemoveAsync"/>); it does not decide untag vs. delete.
    /// Callers pass an already-normalized reference, so this never needs to normalize itself.
    /// </summary>
    private static string RepositoryNameOf(string reference) =>
        ImageReference.TryParse(reference, out var parsed) ? parsed.Name : reference;

    /// <summary>
    /// The string Apple <c>container image tag|save|delete</c> can actually resolve. Those verbs
    /// only take a *reference*: handed a <c>sha256:…</c> id — of *any* kind, Apple's own raw digest
    /// included, not just cider's — they fail with "image with reference sha256:… not found", so the
    /// caller's own reference (when it names this image) or the image's first known reference is
    /// used instead, and a bare id is only a last resort for an image with no reference at all
    /// (verified: cider-ger.19 orchestrator follow-up item 6 asked whether switching <c>image.Id</c>
    /// from Apple's raw index digest to the content-addressed config digest broke this fallback —
    /// it did not, because Apple already refused a bare digest reference of *either* kind before this
    /// task; that limitation is pre-existing and unrelated, covered by every locally built image
    /// always carrying at least a synthetic tag (<see cref="SyntheticBuildTag"/>), so this fallback
    /// in practice is never actually asked to resolve one against the real engine).
    /// </summary>
    private static string RuntimeReferenceFor(RuntimeImage image, string requested)
    {
        var normalized = TryNormalizedTag(requested);
        if (normalized is not null
            && image.References.Any(r => string.Equals(NormalizedOf(r), normalized, StringComparison.Ordinal)))
        {
            return normalized;
        }

        return image.References.FirstOrDefault() ?? image.Id;
    }

    private static string IdWithoutPrefix(string id) =>
        id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;

    private static string ShortDigest(string id) => DockerId.Short(IdWithoutPrefix(id));

    private static string? ExtractDigest(string repoDigest)
    {
        var at = repoDigest.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? repoDigest[(at + 1)..] : null;
    }

    /// <summary>
    /// Exports/loads one image the commit and import paths share: the root filesystem is staged to
    /// disk (it can be hundreds of megabytes), turned into an OCI-layout tar and handed to the
    /// runtime's <c>image load</c>. The id is the one <see cref="OciImageWriter"/> computed — Apple
    /// keys the loaded image by exactly that index digest (verified against the real CLI).
    /// </summary>
    private async Task<string> BuildAndLoadAsync(
        OciImageSpec spec,
        Func<Stream, CancellationToken, Task> writeRootFs,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_options.TmpDir);
        var workDir = Path.Combine(_options.TmpDir, $"commit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var rootFsPath = Path.Combine(workDir, "rootfs.tar");
        var ociPath = Path.Combine(workDir, "image.tar");

        try
        {
            await using (var rootFs = File.Create(rootFsPath))
            {
                await writeRootFs(rootFs, ct).ConfigureAwait(false);
            }

            string id;
            await using (var rootFs = File.OpenRead(rootFsPath))
            {
                id = await OciImageWriter.WriteAsync(spec, rootFs, ociPath, Path.Combine(workDir, "layout"), ct).ConfigureAwait(false);
            }

            await using (var oci = File.OpenRead(ociPath))
            {
                try
                {
                    await _runtime.LoadImagesAsync(oci, ct).ConfigureAwait(false);
                }
                catch (RuntimeException ex)
                {
                    throw ex.ToDockerError();
                }
            }

            InvalidateImageCache();
            return id;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>The container's effective configuration, in the shape an image config carries it.</summary>
    private static ImageConfig ConfigOf(ContainerRecord container, RuntimeImageDetail? source)
    {
        // ContainerManager.CreateAsync writes the *resolved* values (image ∘ request) back into the
        // stored request, so these are already the merged ones — except Volumes, which stays as the
        // client sent it and therefore still needs the image's own declarations unioned in.
        var request = container.Request;
        var volumes = request.Volumes.Keys.ToList();
        foreach (var volume in source?.Config.Volumes ?? [])
        {
            if (!volumes.Contains(volume, StringComparer.Ordinal))
            {
                volumes.Add(volume);
            }
        }

        return new ImageConfig
        {
            Env = request.Env ?? [],
            Cmd = container.Cmd,
            Entrypoint = container.Entrypoint,
            WorkingDir = request.WorkingDir,
            User = request.User,
            ExposedPorts = [.. request.ExposedPorts.Keys],
            Volumes = volumes,
            Labels = new Dictionary<string, string>(request.Labels, StringComparer.Ordinal),
            StopSignal = container.StopSignal,
        };
    }

    /// <summary>
    /// The normalized reference a commit/import result is stored under. Docker leaves a commit with
    /// no <c>repo</c> as <c>&lt;none&gt;:&lt;none&gt;</c>, but Apple needs *some* name to look the
    /// image up by, so it gets the same synthetic tag an untagged <c>docker build</c> does — which
    /// every reference-facing path already hides and treats as dangling.
    /// </summary>
    private static string TargetReference(string? repo, string? tag)
    {
        if (string.IsNullOrEmpty(repo))
        {
            return ImageReference.Parse(SyntheticBuildTag.New()).Normalize().ToString();
        }

        var target = repo + (string.IsNullOrEmpty(tag) ? "" : $":{tag}");
        var parsed = ImageReference.Parse(target);
        if (parsed.Digest is not null)
        {
            throw DockerErrors.BadParameter($"refusing to create an image tagged by digest: {target}");
        }

        return parsed.Normalize().ToString();
    }

    private static string EventNameFor(string reference, string id) =>
        SyntheticBuildTag.IsSyntheticBuildTag(reference) ? id : ImageReference.Parse(reference).Familiar();

    /// <summary>Transparently unwraps a gzipped stream, replaying the two magic bytes it had to peek at.</summary>
    private static async Task<Stream> MaybeDecompressAsync(Stream source, CancellationToken ct)
    {
        var buffer = new byte[2];
        var read = await ReadFullyAsync(source, buffer, ct).ConfigureAwait(false);
        var effective = new PrefixStream(buffer.AsMemory(0, read), source);
        return read == 2 && buffer[0] == 0x1f && buffer[1] == 0x8b
            ? new GZipStream(effective, CompressionMode.Decompress)
            : effective;
    }

    private static async Task ExtractContextAsync(Stream tarContext, string contextDir, CancellationToken ct)
    {
        var buffer = new byte[2];
        var read = await ReadFullyAsync(tarContext, buffer, ct).ConfigureAwait(false);
        var effective = new PrefixStream(buffer.AsMemory(0, read), tarContext);

        Stream source = effective;
        var isGzip = read == 2 && buffer[0] == 0x1f && buffer[1] == 0x8b;
        if (isGzip)
        {
            source = new GZipStream(effective, CompressionMode.Decompress);
        }

        // macOS `tar` embeds xattrs — notably `com.apple.provenance`, which every file on a modern
        // macOS carries automatically — as pax extended-header records whose values are raw binary,
        // not text (it also writes an AppleDouble `._name` sidecar entry next to the file that
        // originally had the xattr). Go's archive/tar — and so dockerd — accepts that without
        // complaint: its PAX parser only rejects a NUL byte in a handful of well-known string
        // fields ("Keys and values should be UTF-8, but the number of bad writers out there forces
        // us to be more liberal.", golang.org/x/tools src/archive/tar/strconv.go). .NET's TarReader
        // is stricter and throws InvalidDataException("The extended header contains invalid
        // records.") on the very first entry of practically any context tar a modern macOS `tar`
        // produces. Separately, TarFile.ExtractToDirectoryAsync (net10) throws that same exception
        // for an entry describing the archive's own root, and again for any `._name` AppleDouble
        // sidecar once the destination directory is reached through a symlink (`/tmp`,
        // `/var/folders` — both symlinks on macOS). A build context has no use for xattrs, the
        // archive root, or AppleDouble sidecars, so all three are dropped before TarFile ever sees
        // them; entries with valid (UTF-8) pax records — e.g. long path names — pass through
        // untouched.
        var sanitizedPath = contextDir + ".sanitized.tar";
        try
        {
            await using (var sanitizedOut = new FileStream(sanitizedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await SanitizePaxHeadersAsync(source, sanitizedOut, ct).ConfigureAwait(false);
            }

            await using var sanitizedIn = new FileStream(sanitizedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            await TarFile.ExtractToDirectoryAsync(sanitizedIn, contextDir, overwriteFiles: true, ct).ConfigureAwait(false);
        }
        finally
        {
            if (isGzip)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            TryDeleteFile(sanitizedPath);
        }
    }

    /// <summary>
    /// Copies a tar stream across, dropping three things a macOS `tar cf` context tar carries that
    /// <see cref="TarFile"/> chokes on: any pax extended-header entry (<c>x</c>, or the archive-wide
    /// <c>g</c>) whose records are not valid UTF-8 — see the remark on the caller — an explicit
    /// entry for the archive's own root (<c>"."</c>/<c>"./"</c>), and AppleDouble <c>._name</c>
    /// sidecar entries. Anything this pass does not recognize as a well-formed header is copied
    /// through verbatim, end of archive padding included, and left for <see cref="TarFile"/> to
    /// judge.
    /// </summary>
    private static async Task SanitizePaxHeadersAsync(Stream tarIn, Stream tarOut, CancellationToken ct)
    {
        var block = new byte[TarBlockLength];

        while (true)
        {
            var read = await ReadFullyAsync(tarIn, block, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            if (read < TarBlockLength || IsZeroBlock(block) || !TryReadTarHeader(block, out var size, out var typeFlag))
            {
                await tarOut.WriteAsync(block.AsMemory(0, read), ct).ConfigureAwait(false);
                await tarIn.CopyToAsync(tarOut, ct).ConfigureAwait(false);
                return;
            }

            var dataLength = (size + TarBlockLength - 1) / TarBlockLength * TarBlockLength;
            if (typeFlag is (byte)'x' or (byte)'g')
            {
                var data = await ReadExactAsync(tarIn, dataLength, ct).ConfigureAwait(false);
                var records = data.AsSpan(0, (int)Math.Min(size, data.Length));
                if (System.Text.Unicode.Utf8.IsValid(records))
                {
                    await tarOut.WriteAsync(block, ct).ConfigureAwait(false);
                    await tarOut.WriteAsync(data, ct).ConfigureAwait(false);
                }

                // Else: drop this extended header entirely. The entry it describes falls back to
                // its own ustar header fields (name/size/mode/mtime), exactly as if pax had never
                // been written for it.
                continue;
            }

            if (IsArchiveRootEntry(block))
            {
                // macOS `tar cf x -C dir .` writes an explicit entry for the context root itself
                // ("./"), which dockerd's own extraction skips (a cleaned relative path of "."
                // names the destination directory, not something inside it) and which
                // TarFile.ExtractToDirectoryAsync (net10) throws ArgumentOutOfRangeException on —
                // it never gets the chance to skip it.
                await CopyExactAsync(tarIn, Stream.Null, dataLength, ct).ConfigureAwait(false);
                continue;
            }

            if (IsAppleDoubleEntry(block))
            {
                // The `._name` sidecar macOS `tar` writes next to a file that had xattrs/a resource
                // fork carries none of that back out anywhere useful in a build context, and on
                // net10 TarFile.ExtractToDirectoryAsync throws that same ArgumentOutOfRangeException
                // for a `._`-prefixed entry the moment the destination directory is reached through
                // a symlink — which is exactly what `/tmp` and `/var/folders` are on macOS. Dropping
                // it here is both what a build has any use for and what keeps every macOS context
                // tar extractable regardless of where the daemon's tmp dir happens to live.
                await CopyExactAsync(tarIn, Stream.Null, dataLength, ct).ConfigureAwait(false);
                continue;
            }

            await tarOut.WriteAsync(block, ct).ConfigureAwait(false);
            await CopyExactAsync(tarIn, tarOut, dataLength, ct).ConfigureAwait(false);
        }
    }

    private const int TarBlockLength = 512;

    /// <summary><c>true</c> when a header's (non-pax-overridden) name field is exactly the
    /// archive's own root — <c>"."</c> or <c>"./"</c> — the entry `tar cf x -C dir .` writes for the
    /// directory that was tarred.</summary>
    private static bool IsArchiveRootEntry(byte[] block)
    {
        var nameField = block.AsSpan(0, 100);
        var nul = nameField.IndexOf((byte)0);
        var name = System.Text.Encoding.ASCII.GetString(nul < 0 ? nameField : nameField[..nul]);
        return name is "." or "./" or "/";
    }

    /// <summary><c>true</c> when a header's (non-pax-overridden) name field's last path segment is an
    /// AppleDouble sidecar (<c>._name</c>) — the file macOS `tar` writes next to an entry that
    /// carried xattrs or a resource fork to smuggle them out-of-band.</summary>
    private static bool IsAppleDoubleEntry(byte[] block)
    {
        var nameField = block.AsSpan(0, 100);
        var nul = nameField.IndexOf((byte)0);
        var name = System.Text.Encoding.ASCII.GetString(nul < 0 ? nameField : nameField[..nul]);
        var lastSlash = name.LastIndexOf('/');
        var baseName = lastSlash < 0 ? name : name[(lastSlash + 1)..];
        return baseName.StartsWith("._", StringComparison.Ordinal);
    }

    /// <summary>Parses a tar header's type flag and size field; <c>false</c> when it is not one.</summary>
    private static bool TryReadTarHeader(byte[] block, out long size, out byte typeFlag)
    {
        typeFlag = block[156];
        return TryParseTarSize(block, out size) && size >= 0;
    }

    /// <summary>Parses the 12-byte size field at the standard tar header offset: NUL/space-terminated
    /// octal, or base-256 for values too big for that.</summary>
    private static bool TryParseTarSize(byte[] block, out long value)
    {
        value = 0;
        var field = block.AsSpan(124, 12);

        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x7F) != 0)
            {
                return false;
            }

            for (var i = 1; i < field.Length; i++)
            {
                if (value > (long.MaxValue >> 8))
                {
                    return false;
                }

                value = (value << 8) | field[i];
            }

            return true;
        }

        var seenDigit = false;
        foreach (var raw in field)
        {
            if (raw is 0 or (byte)' ')
            {
                break;
            }

            if (raw is < (byte)'0' or > (byte)'7')
            {
                return false;
            }

            seenDigit = true;
            value = (value << 3) | (long)(raw - (byte)'0');
        }

        return seenDigit;
    }

    private static bool IsZeroBlock(byte[] block)
    {
        foreach (var value in block)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes (the stream is expected to have them — a
    /// tar data section is always block-padded to its declared size).</summary>
    private static async Task<byte[]> ReadExactAsync(Stream source, long length, CancellationToken ct)
    {
        var data = new byte[length];
        var total = 0;
        while (total < data.Length)
        {
            var read = await source.ReadAsync(data.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == data.Length ? data : data[..total];
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, long length, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(length, 64 * 1024)];
        var remaining = length;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(remaining, buffer.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string ResolveDockerfilePath(string contextDir, string dockerfile)
    {
        var full = Path.GetFullPath(Path.Combine(contextDir, dockerfile));
        var root = Path.GetFullPath(contextDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw DockerErrors.BadParameter($"Forbidden path outside the build context: {dockerfile}");
        }

        return full;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread.
    /// <see cref="Progress{T}"/> captures the ambient <see cref="SynchronizationContext"/> (or falls
    /// back to <see cref="ThreadPool.QueueUserWorkItem(WaitCallback)"/>) and always dispatches
    /// asynchronously — used as an in-process relay from the runtime's progress events to the NDJSON
    /// writer, that reordering silently interleaves and reorders the wire stream (progress lines,
    /// and even the terminal "Status:"/"Successfully built" line, can land out of order). Docker's
    /// NDJSON contract is a single ordered stream, so relays must report in call order.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    /// <summary>Wraps a stream, replaying a small already-consumed prefix before the rest of the stream.</summary>
    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_prefix.IsEmpty)
            {
                var n = Math.Min(_prefix.Length, buffer.Length);
                _prefix.Span[..n].CopyTo(buffer.Span);
                _prefix = _prefix[n..];
                return n;
            }

            return await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Parsed body of <c>POST /build</c> (CONTRACTS.md §F).</summary>
public sealed record BuildRequest
{
    public string Dockerfile { get; init; } = "Dockerfile";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> BuildArgs { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public string? Target { get; init; }
    public string? Platform { get; init; }
    public bool NoCache { get; init; }
    public bool Pull { get; init; }
    public bool Quiet { get; init; }
    public string? Remote { get; init; }
}
