using System.Globalization;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer;

public sealed partial class AppleContainerRuntime
{
    private const string DefaultRegistry = "docker.io";

    public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var images = await _cli.RunJsonAsync<List<AppleImageJson>>(["image", "ls", "--format", "json"], ct);
        if (images is null)
        {
            return (IReadOnlyList<RuntimeImage>)Array.Empty<RuntimeImage>();
        }

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

        await RecoverExposedPortsAsync(images, ct);

        var detail = RuntimeMapper.ToImageDetail(images[0], platform: null);
        return detail is null ? null : await WithSiblingReferencesAsync(detail, ct);
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

    /// <summary>Follows a variant's manifest digest to its config blob and returns that config, or
    /// <c>null</c> if either blob is missing, unreadable or malformed — recovery is best-effort.</summary>
    private async Task<AppleOciConfig?> TryReadLocalConfigAsync(string appRoot, string? manifestDigest, CancellationToken ct)
    {
        var manifest = await TryReadLocalBlobAsync<AppleOciManifest>(appRoot, manifestDigest, ct).ConfigureAwait(false);
        var configDigest = manifest?.Config?.Digest;
        if (string.IsNullOrEmpty(configDigest))
        {
            return null;
        }

        var document = await TryReadLocalBlobAsync<AppleOciImageDocument>(appRoot, configDigest, ct).ConfigureAwait(false);
        return document?.Config;
    }

    /// <summary>Reads and parses one blob from Apple's local content-addressed store
    /// (<c>{appRoot}/content/blobs/{algorithm}/{hex}</c>). Never throws — any failure just means
    /// nothing was recovered.</summary>
    private async Task<T?> TryReadLocalBlobAsync<T>(string appRoot, string? digest, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }

        var colon = digest.IndexOf(':', StringComparison.Ordinal);
        var algorithm = colon < 0 ? "sha256" : digest[..colon];
        var hex = colon < 0 ? digest : digest[(colon + 1)..];
        var path = Path.Combine(appRoot, "content", "blobs", algorithm, hex);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return AppleJson.Deserialize<T>(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "could not recover local OCI blob {Digest} from the content store", digest);
            return null;
        }
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

    public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(tarInput);

        var tmp = NewTempFile("load", ".tar");
        try
        {
            await using (var file = File.Create(tmp))
            {
                await tarInput.CopyToAsync(file, ct);
            }

            var before = await ListReferencesAsync(ct);

            var result = await _cli.RunAsync(["image", "load", "-i", tmp], ct, _options.PullTimeout);
            ContainerCli.ThrowIfFailed(result, "image load");

            var after = await ListReferencesAsync(ct);
            var loaded = new List<string>();
            foreach (var reference in after)
            {
                if (!before.Contains(reference) && !loaded.Contains(reference, StringComparer.Ordinal))
                {
                    loaded.Add(reference);
                }
            }

            if (loaded.Count == 0)
            {
                // Nothing new appeared (the archive only refreshed known tags): fall back to the
                // references the CLI echoed on stdout.
                foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Trim();
                    if (candidate.Length > 0 && after.Contains(candidate))
                    {
                        loaded.Add(candidate);
                    }
                }
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

            // Apple's `-t` default is a random UUID we would not be able to look up afterwards.
            var tags = spec.Tags.Count > 0
                ? spec.Tags
                : (IReadOnlyList<string>)[SyntheticBuildTag.New()];

            var args = ArgBuilder.Build(spec, tags);

            string? imageId = null;
            var result = await _cli.RunStreamingAsync(
                args,
                (line, _) =>
                {
                    progress.Report(new ProgressEvent { Stream = line + "\n" });
                    var exported = ProgressParser.ParseBuiltImageId(line);
                    if (exported is not null)
                    {
                        imageId = exported;
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

            if (imageId is null)
            {
                var detail = await InspectImageAsync(tags[0], ct);
                imageId = detail?.Id;
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
}
