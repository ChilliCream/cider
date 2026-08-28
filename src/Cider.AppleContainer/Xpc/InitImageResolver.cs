using System.Text.Json;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Resolves the vminit image reference <c>containerCreate</c>'s <c>initImage</c> field wants
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.2 item 5: <c>management.initImage ?? containerSystemConfig.vminit.image</c>,
/// "the CLI fetches AND unpacks it itself") and ensures it is unpacked, once, caching both for the
/// daemon's lifetime (task fix direction §2).
/// </summary>
/// <remarks>
/// Two-step resolution:
/// 1. <b>Reference</b> — <c>ContainerSystemConfig.vminit.image</c> lives in a TOML file that, on a
///    default install, usually does not exist at all (confirmed live on this machine: no
///    <c>config.toml</c> under <c>~/.config/container</c> or <c>~/Library/Application Support/com.apple.container/config</c>
///    — the reference is a compile-time default baked into the daemon binary, not discoverable from
///    a static file in the common case). <see cref="TryReadFromConfigFiles"/> is a best-effort,
///    zero-cost check for the uncommon case an operator did set <c>[vminit] image = "…"</c>; the real
///    path for everyone else is <see cref="ReadFromCliAsync"/> — <c>container system property list
///    --format json</c>, which reports the daemon's own effective value (confirmed live:
///    <c>{"vminit":{"image":"ghcr.io/apple/containerization/vminit:0.41.0"}}</c>) — exactly the "fall
///    back to `container system property get` via the CLI once, cache" the task's fix direction calls
///    for. This is not "a new CLI dependency for something the apiserver can do" (the ground rules'
///    ban): there is no XPC route that reports this value at all (§2 has no such route), only a
///    config file this task cannot rely on being present.
/// 2. <b>Pull + unpack</b> — <c>imageList</c> → match → <c>snapshotGet</c>/<c>imageUnpack</c>,
///    identical to <see cref="ImageSnapshotEnsurer"/>. Unlike the container's own image, nothing
///    above the runtime seam ever pulls the init image — so when it is not present locally at all
///    (live failure mode: the store-index repairs left vminit absent and every XPC create degraded
///    to the whole-create CLI path), this pulls it itself over the SAME <c>imagePull</c> route
///    <see cref="XpcContainerRuntime.PullImageAsync"/> uses for normal images (cider-eqa.2; the
///    historical "cannot pull yet" blocker was cider-ede.10 predating pull support, gone since
///    540c493 fixed <c>maxConcurrentDownloads</c>), entering the runtime's <see cref="BlobSweepGate"/>
///    as a write first (cider-ede.31). Only when that pull itself fails does this throw
///    <see cref="RuntimeErrorKind.Unavailable"/> with the pull failure as the reason —
///    <see cref="XpcContainerRuntime.CreateContainerAsync"/> treats that exactly like an
///    apiserver-unavailable read (task fix direction §4's Fallback rule) and falls back to the CLI
///    runtime as last resort, whose own <c>container create</c> still pulls the init image for itself.
/// </remarks>
internal sealed class InitImageResolver
{
    private static readonly string[] ConfigFilePaths =
    [
        // .home — user source (docs/spikes reference: ConfigurationLoader.swift:53-54).
        Path.Combine(HomeDirectory(), ".config", "container", "config.toml"),
        // .appRoot — read-only copy of user config (:55-56).
        Path.Combine(HomeDirectory(), "Library", "Application Support", "com.apple.container", "config", "config.toml"),
        // .installRoot — system defaults shipped with install (:57-58).
        "/usr/local/etc/container/config.toml",
    ];

    private readonly ContainerCli _cli;
    private readonly ImagesServiceClient _images;
    private readonly BlobSweepGate _blobSweepGate;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task<string>>? _referenceResolver;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _reference;
    private bool _ensured;

    /// <param name="blobSweepGate">The owning runtime's own <see cref="BlobSweepGate"/> instance —
    /// the pull-on-absent path below writes blobs exactly like
    /// <see cref="XpcContainerRuntime.PullImageAsync"/> does, so it must enter the same gate that
    /// runtime's sweep (the apiserver-unavailable CLI delete fallback) contends on (cider-ede.31).</param>
    /// <param name="referenceResolver">Test-only seam (cider-eqa.2): overrides the whole
    /// config-file/CLI reference resolution so a unit test can drive the ensure/pull step without a
    /// <c>container</c> binary on the machine. <c>null</c> (every production call site) keeps the
    /// normal resolution.</param>
    public InitImageResolver(
        AppleContainerOptions options,
        ImagesServiceClient images,
        BlobSweepGate blobSweepGate,
        ILogger logger,
        Func<CancellationToken, Task<string>>? referenceResolver = null)
    {
        _cli = new ContainerCli(options, logger);
        _images = images;
        _blobSweepGate = blobSweepGate;
        _logger = logger;
        _referenceResolver = referenceResolver;
    }

    /// <summary>Returns the cached, already-unpacked init image reference, resolving and unpacking it
    /// on first call. A failure to unpack (init image missing locally, see remarks on this type)
    /// leaves the resolved <em>reference</em> cached but retries the unpack step on the next call —
    /// only a fully successful run is cached as "ensured".</summary>
    public async Task<string> ResolveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured && _reference is { } cached)
            {
                return cached;
            }

            var reference = _reference ??= await ResolveReferenceAsync(ct).ConfigureAwait(false);
            await EnsureUnpackedAsync(reference, ct).ConfigureAwait(false);
            _ensured = true;
            return reference;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ResolveReferenceAsync(CancellationToken ct)
    {
        if (_referenceResolver is not null)
        {
            return await _referenceResolver(ct).ConfigureAwait(false);
        }

        foreach (var path in ConfigFilePaths)
        {
            if (TryReadVminitImage(path) is { Length: > 0 } fromConfig)
            {
                return fromConfig;
            }
        }

        return await ReadFromCliAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureUnpackedAsync(string reference, CancellationToken ct)
    {
        var platform = Platform.Current;
        var descriptions = await _images.ImageListAsync(ct).ConfigureAwait(false);
        var match = ImageSnapshotEnsurer.Match(descriptions, reference)
            ?? await PullOverXpcAsync(reference, platform, ct).ConfigureAwait(false);

        try
        {
            await _images.SnapshotGetAsync(match, platform, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) == RuntimeErrorKind.NotFound)
        {
            await _images.ImageUnpackAsync(match, platform, ct).ConfigureAwait(false);
            await _images.SnapshotGetAsync(match, platform, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pulls the absent init image over the SAME <c>imagePull</c> route
    /// <see cref="XpcContainerRuntime.PullImageAsync"/> uses for normal images (cider-eqa.2), pinned
    /// to <paramref name="platform"/> (the current platform — the only variant
    /// <see cref="EnsureUnpackedAsync"/> ever unpacks, matching the CLI's own per-arch vminit fetch).
    /// No progress endpoint: like <see cref="ImageSnapshotEnsurer"/>'s unpack, a create precondition
    /// has no progress stream to report onto. A pull failure — apiserver unreachable, registry
    /// unreachable, anything — throws <see cref="RuntimeErrorKind.Unavailable"/> with the reason in
    /// the message, so <see cref="XpcContainerRuntime.CreateContainerAsync"/>'s existing catch arm
    /// logs it (<c>WarnFallback</c>) and takes the CLI fallback as last resort.
    /// </summary>
    private async Task<ImageDescription> PullOverXpcAsync(string reference, Platform platform, CancellationToken ct)
    {
        _logger.LogInformation(
            "init image '{Reference}' is not present locally; pulling it over the xpc images service (cider-eqa.2)",
            reference);

        // cider-ede.31: a pull writes blobs before its index entry is committed — enter the owning
        // runtime's own gate as a write, exactly like XpcContainerRuntime.PullImageAsync, so this
        // pull can never race the transport's one remaining store-wide sweep (the
        // apiserver-unavailable CLI delete fallback in RemoveImageAsync).
        await using var write = await _blobSweepGate.EnterImageWriteAsync(ct).ConfigureAwait(false);
        try
        {
            return await _images.ImagePullAsync(reference, platform, progressEndpoint: null, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw RuntimeException.Unavailable(
                $"cider: the init image '{reference}' is not present locally and the xpc pull failed " +
                $"({ex.Message}); falling back to the CLI, which will pull it itself");
        }
    }

    /// <summary><c>container system property list --format json</c> → <c>vminit.image</c> — the CLI's
    /// own effective config (<c>Application.loadContainerSystemConfig</c>, task fix direction §2),
    /// used once and cached by the caller.</summary>
    private async Task<string> ReadFromCliAsync(CancellationToken ct)
    {
        var result = await _cli.RunAsync(["system", "property", "list", "--format", "json"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw RuntimeException.Unavailable(
                $"cider: could not resolve the init image (vminit) reference: 'container system property list' failed: {result.Stderr.Trim()}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.TryGetProperty("vminit", out var vminit) &&
                vminit.TryGetProperty("image", out var image) &&
                image.ValueKind == JsonValueKind.String &&
                image.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Falls through to the exception below — malformed CLI output is reported the same way
            // as a missing key.
        }

        throw RuntimeException.Unavailable(
            "cider: 'container system property list' did not report a vminit.image reference");
    }

    /// <summary>Best-effort <c>[vminit]\nimage = "…"</c> read from one TOML file — deliberately not a
    /// general TOML parser (single-key scan, only for the one section this needs); returns
    /// <c>null</c> on any read failure, missing file, or absent key, exactly like every other
    /// best-effort fallback in this class.</summary>
    private static string? TryReadVminitImage(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var inVminitSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inVminitSection = string.Equals(line, "[vminit]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inVminitSection)
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            if (!string.Equals(key, "image", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim().Trim('"');
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string HomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
