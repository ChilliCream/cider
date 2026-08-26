using System.Diagnostics;
using System.Globalization;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Cider.AppleContainer.ContentStore;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer;

public sealed partial class AppleContainerRuntime
{
    private const string DefaultRegistry = "docker.io";

    /// <summary>
    /// Gates this runtime's own image writes (pull/load/build) against its own deletes (cider-ede.31):
    /// every <c>container image delete</c> invocation sweeps the whole content store as an unavoidable
    /// step of its single process (Apple's <c>ImageDelete.swift</c> — no CLI flag skips it), so on this
    /// transport a "sweep" is not something cider chooses to run separately, it is what
    /// <see cref="RemoveImageAsync"/> already does every time. This instance is shared with every write
    /// path below and with <see cref="RemoveImageAsync"/> — including when this runtime is used purely
    /// as the XPC transport's CLI fallback (<c>XpcContainerRuntime</c>'s own <c>_cliFallback</c>), so a
    /// pull/load/build funnelled through *this* runtime is never mid-write while *this* runtime's own
    /// delete subprocess is sweeping. This instance is a separate <see cref="BlobSweepGate"/> from the
    /// one <c>XpcContainerRuntime</c> keeps for its own direct-XPC pulls/loads/builds/
    /// <c>PruneImagesAsync</c> — the two do not coordinate with each other. That split was a live hole
    /// (cider-ede.31 correction), not a harmless one: an XPC-transport
    /// build used to delegate straight to <em>this</em> runtime's <see cref="BuildImageAsync"/> with no
    /// XPC-side gate entry first, so it was invisible to the XPC transport's own
    /// <c>PruneImagesAsync</c> sweep even though it commits new content the same way a pull/load does.
    /// <c>XpcContainerRuntime.BuildImageAsync</c> now enters its own gate before delegating here, closing
    /// that hole — every write this daemon can perform on either transport is covered by whichever
    /// gate that transport's own sweep (this runtime's delete, or the XPC transport's
    /// <c>PruneImagesAsync</c>) actually takes.
    /// </summary>
    private readonly BlobSweepGate _blobSweepGate = new();

    /// <summary>
    /// <c>image ls</c> fails hard when Apple's store holds even one dangling content reference,
    /// even though every other entry is fine (cider-ede.24, verified live on this machine: <c>Error:
    /// content with digest sha256:…</c>, the blob gone but <c>state.json</c> still naming it). Cider
    /// must not repair another tool's store, but it also must not let one bad row 500 every
    /// <c>docker images</c> call — nor may it turn that failure into a false "no images" success
    /// (planner ruling on cider-ede.24, comment 66: never synthesize an empty 200 out of a failure cider
    /// could not read — an empty list is a positive assertion, "this machine has no images", that no
    /// caller can then tell apart from a genuinely empty store). The two outcomes are an explicit
    /// branch, not <c>count == 0</c>: if the failed call still printed one or more parseable rows on
    /// stdout, that is <em>enumerated-with-skips</em> — log one Warning naming the digest and the
    /// operator remedy, and answer 200 with what is enumerable. If nothing parses (no partial output to
    /// salvage, or what came back is not valid JSON), that is a <em>total</em> failure — the Warning
    /// still logs once, but the call throws exactly as any other failure would, so a caller after one
    /// specific image can still fall back to <c>image inspect &lt;ref&gt;</c>, which keeps working.
    /// Every non-dangling failure throws exactly as before, with no Warning. Only a genuinely
    /// successful, empty listing (the store really has no images) returns an empty list.
    /// </summary>
    public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var result = await _cli.RunAsync(["image", "ls", "--format", "json"], ct);
        if (!result.Succeeded)
        {
            if (CliErrorMapper.IsDanglingContent(result.Stderr))
            {
                var digest = CliErrorMapper.ExtractDanglingDigest(result.Stderr) ?? result.Stderr;
                _logger.LogWarning("{Message}", CliErrorMapper.DanglingContentRemedy(digest));

                List<AppleImageJson>? partial = null;
                try
                {
                    partial = ContainerCli.ParseJson<List<AppleImageJson>>(result.Stdout, "container image ls");
                }
                catch (RuntimeException)
                {
                    // Malformed stdout alongside the dangling-content failure: treated as nothing
                    // parsed, not a second, confusing error on top of the one already logged above.
                }

                if (partial is { Count: > 0 })
                {
                    // Enumerated-with-skips: Apple still printed the rows it could, even though the
                    // call as a whole exited non-zero over the one dangling entry.
                    await RecoverContentAddressedIdsAsync(partial, ct);
                    return RuntimeMapper.ToImages(partial);
                }
            }

            // TOTAL failure to enumerate (not dangling at all, or dangling with nothing to salvage).
            throw CliErrorMapper.ToException(result, "image ls");
        }

        var images = ContainerCli.ParseJson<List<AppleImageJson>>(result.Stdout, "container image ls");
        if (images is null)
        {
            return (IReadOnlyList<RuntimeImage>)Array.Empty<RuntimeImage>();
        }

        await RecoverContentAddressedIdsAsync(images, ct);

        // Apple lists one row per reference; Docker wants one image per digest (RuntimeMapper.ToImages).
        return RuntimeMapper.ToImages(images);
    });

    public Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        var result = await _cli.RunAsync(["image", "inspect", reference], ct);
        if (!result.Succeeded)
        {
            if (CliErrorMapper.Classify(result.Stderr) == RuntimeErrorKind.NotFound)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, $"image inspect {reference}");
        }

        var images = ContainerCli.ParseJson<List<AppleImageJson>>(result.Stdout, "container image inspect");
        if (images is not { Count: > 0 })
        {
            return null;
        }

        await RecoverContentAddressedIdsAsync(images, ct);
        await RecoverExposedPortsAsync(images, ct);

        var detail = RuntimeMapper.ToImageDetail(images[0], platform: null);
        if (detail is null)
        {
            return null;
        }

        detail = await RecoverLayerSizesAsync(images[0], detail, ct);
        return await WithSiblingReferencesAsync(detail, ct);
    });

    /// <summary>
    /// <c>container image inspect</c> on 1.2.2 silently drops empty-object-valued dictionary fields of
    /// the OCI config — <c>ExposedPorts</c> and <c>Volumes</c> — even when the image genuinely declares
    /// them (verified: <c>postgres:18.3</c>'s real config carries <c>{"5432/tcp":{}}</c>, but every
    /// variant of its <c>image inspect</c> output omits the key entirely; <c>Cmd</c>, <c>Entrypoint</c>,
    /// <c>Env</c> and <c>StopSignal</c> of the same config.config survive). The true document is not
    /// gone: Apple keeps a content-addressed OCI blob store under AppRoot (<c>container system status
    /// --format json</c> → <c>appRoot</c>), and each variant's <c>digest</c> from <c>image inspect</c> is
    /// itself a manifest sitting in that store — its <c>config.digest</c> names the real, complete
    /// config blob. Reading that pair of local files is instant (no CLI subprocess, no network, no
    /// re-downloading the image), so it is done here to patch the gaps back in rather than trusting the
    /// CLI's truncated echo. See docs/apple-container-notes.md's ExposedPorts probe for the raw evidence.
    /// </summary>
    private async Task RecoverExposedPortsAsync(List<AppleImageJson> images, CancellationToken ct)
    {
        string? appRoot = null;
        var appRootResolved = false;

        foreach (var image in images)
        {
            if (image.Variants is not { Count: > 0 })
            {
                continue;
            }

            foreach (var variant in image.Variants)
            {
                if (variant.IsAttestation)
                {
                    continue;
                }

                var config = variant.Config?.Config;
                if (config is null)
                {
                    continue;
                }

                // Apple has already reported it (a future CLI might stop dropping it) — nothing to do.
                if (config.ExposedPorts is { Count: > 0 } && config.Volumes is { Count: > 0 })
                {
                    continue;
                }

                if (!appRootResolved)
                {
                    appRoot = await ResolveAppRootAsync(ct).ConfigureAwait(false);
                    appRootResolved = true;
                }

                if (string.IsNullOrEmpty(appRoot))
                {
                    return; // No local store to recover from; leave the CLI's (truncated) config as-is.
                }

                var recovered = await TryReadLocalConfigAsync(appRoot, variant.Digest, ct).ConfigureAwait(false);
                if (recovered is null)
                {
                    continue;
                }

                if (config.ExposedPorts is not { Count: > 0 } && recovered.ExposedPorts is { Count: > 0 })
                {
                    config.ExposedPorts = recovered.ExposedPorts;
                }

                if (config.Volumes is not { Count: > 0 } && recovered.Volumes is { Count: > 0 })
                {
                    config.Volumes = recovered.Volumes;
                }
            }
        }
    }

    /// <summary>
    /// Apple's own <c>id</c> (<see cref="AppleImageJson.Id"/>) is the OCI *index* digest — reproduced
    /// (cider-ger.19): loading the same byte-identical BuildKit-exported docker tar twice yields two
    /// different <c>container image ls</c> ids for the same tag, even though the tar's own
    /// manifest+config digests never change between the two exports. That id drift is exactly what
    /// surfaced as <c>docker images -q</c>/<c>--iidfile</c> disagreeing with each other for two builds
    /// of the same Dockerfile (tests/compat/run-buildkit.sh scenario 6).
    ///
    /// What is actually confirmed about the root cause (task comments, planner-1, run directly against
    /// Apple <c>container</c> 1.3.0 with no cider in the path): Apple's <c>image load</c> is itself
    /// deterministic and content-addressed — the same byte-identical tar loaded four different ways
    /// (image absent, image already present, after a delete, under a second tag) produced the exact
    /// same index id every time. So Apple does not simply "recompute a fresh id on every load" the way
    /// this comment used to claim; that hypothesis was tested and falsified. The instability this task
    /// reports comes from comparing *two separate BuildKit exports* of the same Dockerfile: their
    /// manifest and config blobs are byte-identical, but the OCI *index* blob each export produces is
    /// not — which specific field varies (annotation, entry order, or an extra entry) was never
    /// isolated, because it stopped mattering once the fix below was in place. What is not in question
    /// either way: Docker's own image id is the digest of the image *config* blob, not of an index, and
    /// Apple's local content-addressed store already keys that config blob by its real content digest —
    /// so reading it back (the same local-store lookup <see cref="RecoverLayerSizesAsync"/> already
    /// does for per-layer sizes) gives a value that is genuinely stable across separate exports of
    /// identical content, unlike the index id the CLI hands back directly. Recovery is best-effort,
    /// exactly like <see cref="RecoverExposedPortsAsync"/>: a store miss just leaves
    /// <see cref="AppleImageJson.ContentAddressedId"/> unset, and callers fall back to Apple's own
    /// (possibly unstable) id rather than the call failing.
    /// </summary>
    private async Task RecoverContentAddressedIdsAsync(List<AppleImageJson> images, CancellationToken ct)
    {
        string? appRoot = null;
        var appRootResolved = false;

        // Apple's `image ls` answers one row per *reference* (RuntimeMapper.ToImages' own doc
        // comment), so two tags of the same image are two rows naming the same manifest digest — cache
        // the blob read per digest rather than re-reading the same file for every tag (fix direction
        // item 8: don't let this recovery add an unbounded number of file reads to the hot listing
        // path; an image's manifest never changes once written, so nothing invalidates this cache and
        // scoping it to one call is enough).
        var manifestCache = new Dictionary<string, AppleOciManifest?>(StringComparer.Ordinal);

        foreach (var image in images)
        {
            var variant = RuntimeMapper.PickVariant(image, null);
            if (string.IsNullOrEmpty(variant?.Digest))
            {
                continue;
            }

            if (!appRootResolved)
            {
                appRoot = await ResolveAppRootAsync(ct).ConfigureAwait(false);
                appRootResolved = true;
            }

            if (string.IsNullOrEmpty(appRoot))
            {
                return; // No local store to recover from; leave every remaining row's id as-is.
            }

            if (!manifestCache.TryGetValue(variant.Digest, out var manifest))
            {
                manifest = await LocalBlobReader.TryReadBlobAsync<AppleOciManifest>(appRoot, variant.Digest, _logger, ct).ConfigureAwait(false);
                manifestCache[variant.Digest] = manifest;
            }

            var configDigest = manifest?.Config?.Digest;
            if (!string.IsNullOrEmpty(configDigest))
            {
                image.ContentAddressedId = configDigest;
            }
        }
    }

    /// <summary>
    /// <c>docker history</c>'s per-layer <c>Size</c>: dockerd gets it by walking the image config's
    /// <c>history[]</c> newest-first, consuming the manifest's <c>layers[]</c> from the end for every
    /// entry that is not <c>empty_layer</c> (<c>ImageManager.HistoryAsync</c> does the walk). Apple's
    /// <c>image inspect</c> reports only one total size per platform variant, never that per-layer
    /// breakdown, but the manifest blob in the local content store carries the real
    /// <c>layers[].size</c> array — read here the same way <see cref="RecoverExposedPortsAsync"/>
    /// reads the config blob (see docs/spikes/xpc/03-limitations-audit-1.3.md's history row). Recovery
    /// is best-effort: <paramref name="detail"/> comes back unchanged when the store or the manifest's
    /// layer sizes are unavailable.
    /// </summary>
    private async Task<RuntimeImageDetail> RecoverLayerSizesAsync(AppleImageJson image, RuntimeImageDetail detail, CancellationToken ct)
    {
        var variant = RuntimeMapper.PickVariant(image, null);
        if (string.IsNullOrEmpty(variant?.Digest))
        {
            return detail;
        }

        var appRoot = await ResolveAppRootAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(appRoot))
        {
            return detail;
        }

        var manifest = await LocalBlobReader.TryReadBlobAsync<AppleOciManifest>(appRoot, variant.Digest, _logger, ct).ConfigureAwait(false);
        if (manifest?.Layers is not { Count: > 0 } layers)
        {
            return detail;
        }

        return detail with { LayerSizes = [.. layers.Select(l => l.Size ?? 0)] };
    }

    /// <summary>Follows a variant's manifest digest to its config blob and returns that config, or
    /// <c>null</c> if either blob is missing, unreadable or malformed — recovery is best-effort.</summary>
    private async Task<AppleOciConfig?> TryReadLocalConfigAsync(string appRoot, string? manifestDigest, CancellationToken ct)
    {
        var manifest = await LocalBlobReader.TryReadBlobAsync<AppleOciManifest>(appRoot, manifestDigest, _logger, ct).ConfigureAwait(false);
        var configDigest = manifest?.Config?.Digest;
        if (string.IsNullOrEmpty(configDigest))
        {
            return null;
        }

        var document = await LocalBlobReader.TryReadBlobAsync<AppleOciImageDocument>(appRoot, configDigest, _logger, ct).ConfigureAwait(false);
        return document?.Config;
    }

    /// <summary>Apple's install-relative data root, cached after the first successful lookup — it does
    /// not change for the lifetime of the daemon.</summary>
    private string? _appRootCache;
    private readonly SemaphoreSlim _appRootGate = new(1, 1);

    private async Task<string?> ResolveAppRootAsync(CancellationToken ct)
    {
        if (_appRootCache is { Length: > 0 })
        {
            return _appRootCache;
        }

        await _appRootGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_appRootCache is { Length: > 0 })
            {
                return _appRootCache;
            }

            var status = await TryReadStatusAsync(ct).ConfigureAwait(false);
            _appRootCache = status?.AppRoot;
            return _appRootCache;
        }
        finally
        {
            _appRootGate.Release();
        }
    }

    /// <summary>
    /// <c>image inspect &lt;ref&gt;</c> answers with the single row for the reference that was asked
    /// for, so its <c>References</c> only ever holds that one name. Docker's <c>RepoTags</c> lists
    /// every tag of the digest, so the merged <c>image ls</c> view supplies the siblings.
    /// </summary>
    private async Task<RuntimeImageDetail> WithSiblingReferencesAsync(RuntimeImageDetail detail, CancellationToken ct)
    {
        if (detail.Id.Length == 0)
        {
            return detail;
        }

        IReadOnlyList<RuntimeImage> images;
        try
        {
            images = await ListImagesAsync(ct);
        }
        catch (RuntimeException ex)
        {
            // The sibling tags are a nicety; never fail an inspect that already succeeded over them.
            _logger.LogDebug(ex, "could not list images to complete the references of {Id}", detail.Id);
            return detail;
        }

        foreach (var image in images)
        {
            if (!string.Equals(image.Id, detail.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var references = RuntimeMapper.MergeReferences(detail.References, image.References);
            return ReferenceEquals(references, detail.References) ? detail : detail with { References = references };
        }

        return detail;
    }

    public Task PullImageAsync(
        string reference,
        string? platform,
        RegistryAuth? auth,
        IProgress<ProgressEvent> progress,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentNullException.ThrowIfNull(progress);

        // cider-ede.31: this daemon's own `container image delete` subprocess sweeps the whole store
        // as an unavoidable part of running at all — a pull must never be in flight while one of this
        // runtime's own deletes is doing that.
        await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);

        if (auth is not null && !string.IsNullOrEmpty(auth.Username))
        {
            await LoginAsync(auth, ct);
        }

        var args = new List<string> { "image", "pull", "--progress", "plain" };
        if (!string.IsNullOrEmpty(platform))
        {
            args.Add("--platform");
            args.Add(platform);
        }

        args.Add(reference);

        // Nothing may reach the caller until the pull is provably past the manifest lookup: a missing
        // tag prints only first-step "[1/2] Fetching image [0s]" lines for about a second before the
        // registry's 404, and anything reported in that window starts the NDJSON response and costs
        // the client its HTTP 404. Docker's "Pulling from ..." header is
        // ImageManager's to emit, so it is not reported here at all.
        var buffered = new List<ProgressEvent>();
        var underWay = false;

        var result = await _cli.RunStreamingAsync(
            args,
            (line, _) =>
            {
                var evt = ProgressParser.ParsePullLine(line);
                if (evt is null)
                {
                    return;
                }

                if (underWay)
                {
                    progress.Report(evt);
                    return;
                }

                buffered.Add(evt);
                if (!IsPullUnderWay(evt))
                {
                    return;
                }

                underWay = true;
                foreach (var pending in buffered)
                {
                    progress.Report(pending);
                }

                buffered.Clear();
            },
            ct,
            _options.PullTimeout);

        if (!result.Succeeded)
        {
            // Deliberately no progress.Report for the error: reporting it here is what would start the
            // response and turn the manager's 404 into an in-stream error.
            throw CliErrorMapper.ToException(result, $"pull {reference}");
        }

        foreach (var pending in buffered)
        {
            progress.Report(pending);
        }

        // No synthetic "Status: …" line here: the adapter reports only what the CLI produced, and
        // ImageManager owns the Docker-shaped header/digest/status lines (ARCHITECTURE §9). Emitting
        // one here put two contradictory terminal Status lines in a successful pull.
    });

    /// <summary>
    /// True once a pull progress line proves the pull got past the manifest lookup: it carries blob
    /// counts, or it belongs to a later step than the first (<c>[2/2] Unpacking image</c>). Lines that
    /// fail this test are held back, because a pull whose manifest is unknown emits nothing else.
    /// </summary>
    private static bool IsPullUnderWay(ProgressEvent evt)
    {
        if (evt.Current is not null || evt.Total is not null)
        {
            return true;
        }

        var id = evt.Id;
        var slash = id?.IndexOf('/') ?? -1;
        return slash > 0
            && int.TryParse(id!.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
            && step > 1;
    }

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
            await LoginAsync(auth, ct);
        }

        progress.Report(new ProgressEvent { Status = $"The push refers to repository [{reference}]" });

        var result = await _cli.RunStreamingAsync(
            ["image", "push", "--progress", "plain", reference],
            (line, _) =>
            {
                var evt = ProgressParser.ParsePullLine(line);
                if (evt is not null)
                {
                    progress.Report(evt);
                }
            },
            ct,
            _options.PullTimeout);

        if (!result.Succeeded)
        {
            var exception = CliErrorMapper.ToException(result, $"push {reference}");
            progress.Report(new ProgressEvent { Error = exception.Message });
            throw exception;
        }
    });

    public Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrEmpty(sourceReference);
            ArgumentException.ThrowIfNullOrEmpty(targetReference);

            var result = await _cli.RunAsync(["image", "tag", sourceReference, targetReference], ct);
            ContainerCli.ThrowIfFailed(result, $"image tag {sourceReference}");
        });

    public Task RemoveImageAsync(string reference, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        // cider-ede.31: `container image delete` sweeps the whole content store as an unavoidable part
        // of the one subprocess this spawns (Apple's own ImageDelete.swift; there is no flag to skip
        // it) — from this daemon's point of view that subprocess *is* a sweep, so it takes the gate
        // exclusively, the same as an XPC-transport PruneImagesAsync, rather than running unguarded
        // against this runtime's own concurrent pulls/loads/builds.
        //
        // cider-ede.31 fix direction §4: on this transport every delete genuinely is a sweep, so a
        // CLI-transport rmi that appears hung needs to be attributable — log Debug when acquiring the
        // gate actually had to wait on this runtime's own in-flight writes (vs. acquiring it free), and
        // log Information once the delete/sweep itself starts running, naming the reference, so "stuck
        // waiting on the gate" and "the subprocess itself is slow" are distinguishable from the log.
        var gateWait = Stopwatch.StartNew();
        await using var sweep = await _blobSweepGate.EnterSweepAsync(ct).ConfigureAwait(false);
        gateWait.Stop();
        if (gateWait.Elapsed > TimeSpan.FromMilliseconds(5))
        {
            _logger.LogDebug(
                "image delete {Reference}: waited {ElapsedMs}ms for in-flight image write(s) to finish before sweeping",
                reference, gateWait.ElapsedMilliseconds);
        }

        _logger.LogInformation("image delete {Reference}: running (sweeps the whole content store)", reference);

        var args = new List<string> { "image", "delete" };
        if (force)
        {
            // Apple's -f means "ignore images that are not found", not Docker's "remove anyway".
            args.Add("-f");
        }

        args.Add(reference);

        var result = await _cli.RunAsync(args, ct);
        ContainerCli.ThrowIfFailed(result, $"image delete {reference}");
    });

    public Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(references);
            ArgumentNullException.ThrowIfNull(tarOutput);

            if (references.Count == 0)
            {
                throw RuntimeException.InvalidArgument("no image references given to save");
            }

            var tmp = NewTempFile("save", ".tar");
            try
            {
                var args = new List<string> { "image", "save", "-o", tmp };
                args.AddRange(references);

                var result = await _cli.RunAsync(args, ct, _options.PullTimeout);
                ContainerCli.ThrowIfFailed(result, "image save");

                await using var file = File.OpenRead(tmp);
                await file.CopyToAsync(tarOutput, ct);
            }
            finally
            {
                DeleteQuietly(tmp);
            }
        });

    /// <summary>Prefix Apple's <c>image load</c> puts in front of each reference it names on stdout.</summary>
    private const string LoadedImagePrefix = "Loaded image:";

    /// <summary>
    /// Must not depend on a healthy full listing (cider-ede.24): <see cref="ListImagesAsync"/> can now
    /// throw on a total enumeration failure (comment 66's ruling — see its doc comment), so a before/after
    /// diff of it can no longer be trusted to prove what a successful <c>image load</c> just loaded, nor
    /// may that throw be allowed to turn a load that genuinely succeeded on Apple's side into a reported
    /// failure (comment 66, other half: never turn a success into a failure either). The primary source
    /// is Apple's own stdout echo (<c>Loaded image: &lt;ref&gt;</c>), gated on both the line actually
    /// carrying that prefix (review correction: treating *any* non-empty stdout line as a loaded
    /// reference swallowed unrelated CLI chatter) and the text after it parsing as a real
    /// <see cref="ImageReference"/> — a malformed prefixed line is dropped, not trusted. The before/after
    /// <see cref="ListReferencesAsync"/> diff is kept only as a secondary source, unioned in and
    /// contributing nothing when it found nothing — including when the listing call itself throws
    /// (poisoned store): that is caught and logged at Debug, not allowed to fail an otherwise-successful
    /// load. If a successful <c>image load</c> still leaves both sources empty, that names a real gap
    /// (cider could not identify what it just loaded) but not a failed load: the archive did land, so
    /// this logs a Warning naming the condition rather than throwing.
    /// </summary>
    public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(tarInput);

        // cider-ede.31: same reasoning as PullImageAsync's own gate entry above.
        await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);

        var tmp = NewTempFile("load", ".tar");
        try
        {
            await using (var file = File.Create(tmp))
            {
                await tarInput.CopyToAsync(file, ct);
            }

            var before = await ListReferencesToleratingFailureAsync(ct);

            var result = await _cli.RunAsync(["image", "load", "-i", tmp], ct, _options.PullTimeout);
            ContainerCli.ThrowIfFailed(result, "image load");

            // Deduped on the *normalized* reference (a stable key regardless of spelling), not the raw
            // string: otherwise the same image could be returned twice under 'foo:latest' from stdout
            // and 'docker.io/library/foo:latest' from the after-listing. The first spelling seen for a
            // given normalized key wins, so stdout's spelling (the primary source) is preferred over the
            // secondary listing diff.
            var loaded = new List<string>();
            var loadedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(LoadedImagePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidate = trimmed[LoadedImagePrefix.Length..].Trim();
                if (candidate.Length > 0 &&
                    ImageReference.TryParse(candidate, out var parsedCandidate) &&
                    loadedKeys.Add(parsedCandidate.Normalize().ToString()))
                {
                    loaded.Add(candidate);
                }
            }

            if (before is not null)
            {
                var after = await ListReferencesToleratingFailureAsync(ct);
                if (after is not null)
                {
                    foreach (var reference in after)
                    {
                        if (before.Contains(reference))
                        {
                            continue;
                        }

                        var key = ImageReference.TryParse(reference, out var parsedReference)
                            ? parsedReference.Normalize().ToString()
                            : reference;
                        if (loadedKeys.Add(key))
                        {
                            loaded.Add(reference);
                        }
                    }
                }
            }

            if (loaded.Count == 0)
            {
                _logger.LogWarning(
                    "`image load` succeeded but no loaded reference could be identified from the CLI's " +
                    "own output or the image listing; the load itself is not reported as a failure, but " +
                    "callers that rely on this result to know what was loaded will see nothing");
            }

            return (IReadOnlyList<string>)loaded;
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    });

    public Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(progress);

            // cider-ede.31: a build commits new content the same way a pull/load does — same reasoning
            // as PullImageAsync's own gate entry above.
            await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);

            // Apple's `-t` default is a random UUID we would not be able to look up afterwards.
            //
            // cider-imz: this must be normalized the same way ImageManager.BuildAsync normalizes
            // every explicit `-t` tag (and SolveRewriter.NormalizeName mints its own synthetic tag
            // for the BuildKit path) before it ever reaches Apple's CLI. Handed the bare
            // `cider-build-<uuid>` name, Apple's `container build -t` stores the image under that
            // exact bare name — but ImageManager's delete/prune paths always compute their target via
            // ImageReference.Normalize(), which unconditionally rewrites a domain-less single-segment
            // reference to `docker.io/library/cider-build-<uuid>:latest`. That mismatched reference is
            // one Apple's own store never held, so `imageDelete` silently no-ops on it: `rmi -f` and
            // `prune -f` both report success while the synthetic tag never actually leaves the store.
            // Minting it already-normalized here keeps the mint and every delete path speaking the
            // same reference, exactly like every other locally-created image already does.
            var tags = spec.Tags.Count > 0
                ? spec.Tags
                : (IReadOnlyList<string>)[ImageReference.Parse(SyntheticBuildTag.New()).Normalize().ToString()];

            var args = ArgBuilder.Build(spec, tags);

            string? scrapedId = null;
            var result = await _cli.RunStreamingAsync(
                args,
                (line, _) =>
                {
                    progress.Report(new ProgressEvent { Stream = line + "\n" });
                    var exported = ProgressParser.ParseBuiltImageId(line);
                    if (exported is not null)
                    {
                        scrapedId = exported;
                    }
                },
                ct,
                _options.PullTimeout);

            if (!result.Succeeded)
            {
                var exception = CliErrorMapper.ToException(result, "build");
                progress.Report(new ProgressEvent { Error = exception.Message });
                throw exception;
            }

            // cider-ger.20: `scrapedId` is an OCI manifest/manifest-list digest off the "exporting
            // manifest[ list] …" progress lines, never the content-addressed *config* digest that
            // RuntimeImage.Id actually is (RecoverContentAddressedIdsAsync above, and its XPC
            // transport twin) -- a classic build's own freshly-printed "Successfully built <id>"
            // then could never appear in that same build's `docker images --filter dangling=true`
            // listing, because the two were never the same kind of digest to begin with. The
            // authoritative id is whatever InspectImageAsync resolves the tag Apple's CLI was just
            // told to apply to -- the very same content-addressed lookup ListImagesAsync/InspectImageAsync
            // already do for every other image, so this is guaranteed to agree with what a listing
            // reports right afterward. That lookup is best-effort, not load-bearing, the same way
            // WithSiblingReferencesAsync's own listing call is: the build itself already succeeded
            // (the throw above would have fired otherwise), so a transient failure resolving its id
            // afterward must never turn that success into a reported failure (the same "never turn a
            // success into a failure" rule ListImagesAsync's own doc comment credits to cider-ede.24) —
            // it just leaves the scraped digest as the answer instead.
            RuntimeImageDetail? detail = null;
            RuntimeException? inspectFailure = null;
            try
            {
                detail = await InspectImageAsync(tags[0], ct);
            }
            catch (RuntimeException ex)
            {
                inspectFailure = ex;
            }

            var imageId = string.IsNullOrEmpty(detail?.Id) ? scrapedId : detail.Id;

            // Covers both the exception path above and InspectImageAsync succeeding with no usable
            // id: either way the reported id is the scraped manifest digest, not the content-addressed
            // config digest a subsequent listing would report, so this is worth a Warning, not silence
            // (matching this file's own precedent for degraded results at line 41/657). Logged once,
            // here, rather than also inside the catch block above, so the exception path never double-logs.
            if (string.IsNullOrEmpty(detail?.Id))
            {
                _logger.LogWarning(
                    inspectFailure,
                    "could not resolve the content-addressed id of image {Tag} just built; falling back to the scraped build output {ScrapedId}, which is a manifest digest that will not match a subsequent listing",
                    tags[0],
                    scrapedId);
            }

            if (imageId is null)
            {
                throw RuntimeException.Internal("the build succeeded but no image id could be determined");
            }

            // The adapter reports only the raw build output above; the Docker-shaped terminal
            // lines ("Successfully built"/"Successfully tagged", and hiding the synthetic tag
            // when none was requested) are ImageManager's job (ARCHITECTURE §9).
            return imageId;
        });

    public Task LoginAsync(RegistryAuth auth, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(auth);

        if (string.IsNullOrEmpty(auth.Username))
        {
            throw RuntimeException.InvalidArgument("registry login requires a username");
        }

        var server = NormalizeRegistry(auth.ServerAddress);
        var password = auth.Password ?? auth.IdentityToken ?? "";

        var result = await _cli.RunAsync(
            ["registry", "login", server, "-u", auth.Username, "--password-stdin"],
            ct,
            _options.CommandTimeout,
            password + "\n");

        ContainerCli.ThrowIfFailed(result, $"registry login {server}");
        _logger.LogDebug("logged in to registry {Server}", server);
    });

    /// <summary>Turns Docker's <c>serveraddress</c> forms into the bare host Apple's CLI wants.</summary>
    internal static string NormalizeRegistry(string? serverAddress)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return DefaultRegistry;
        }

        var value = serverAddress.Trim();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[scheme.Length..];
            }
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            value = value[..slash];
        }

        if (value.Length == 0 ||
            string.Equals(value, "index.docker.io", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "registry-1.docker.io", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRegistry;
        }

        return value;
    }

    private async Task<HashSet<string>> ListReferencesAsync(CancellationToken ct)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var images = await ListImagesAsync(ct);
        foreach (var image in images)
        {
            foreach (var reference in image.References)
            {
                references.Add(reference);
            }
        }

        return references;
    }

    /// <summary>
    /// <see cref="ListReferencesAsync"/> for callers that use it only as a secondary, best-effort
    /// source (<see cref="LoadImagesAsync"/>'s before/after diff): <see cref="ListImagesAsync"/> can now
    /// throw on a total enumeration failure (comment 66). A caught failure here means "no diff
    /// available" — it is NOT the same as a genuinely empty listing, and must not be conflated with one
    /// (comment 66's ban on synthesizing an empty success out of a failure). Returns null when the
    /// listing could not be obtained at all, so callers can tell "nothing" from "unknown" and skip the
    /// diff entirely rather than treat an unreadable store as proof it holds no images.
    /// </summary>
    private async Task<HashSet<string>?> ListReferencesToleratingFailureAsync(CancellationToken ct)
    {
        try
        {
            return await ListReferencesAsync(ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "could not list images while identifying an image load's references");
            return null;
        }
    }
}
