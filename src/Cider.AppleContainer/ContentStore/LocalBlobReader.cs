using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.ContentStore;

/// <summary>
/// Reads and parses OCI JSON blobs out of Apple's local content-addressed store — shared by both
/// transports (task cider-ede.10's file-scope note: "mechanical move; CLI runtime keeps using it").
/// The CLI transport resolves a blob's local path itself from AppRoot + digest (no <c>contentGet</c>
/// route exists over the CLI); the XPC transport resolves it with <c>contentGet</c>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §6's content-store table:
/// <c>ImagesServiceClient.ContentGetAsync</c>). Either way, once a path is known the read is the
/// same: "Blob bytes never traverse XPC... a digest is resolved to a local file path, then a normal
/// file read" (§6's closing note) — so the parsing rules, and the moved OCI models below, live in
/// exactly one place regardless of which transport found the path.
/// </summary>
internal static class LocalBlobReader
{
    /// <summary>Reads and parses one JSON blob already resolved to an absolute local path (the XPC
    /// transport's own <c>contentGet</c> reply). Never throws — any failure just means nothing was
    /// read; the caller treats that identically to "blob not present".</summary>
    public static async Task<T?> TryReadAsync<T>(string? path, ILogger logger, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return Deserialize<T>(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "could not read local OCI blob {Path} from the content store", path);
            return null;
        }
    }

    /// <summary>
    /// <c>{appRoot}/content/blobs/{algorithm}/{hex}</c> — the CLI transport's own path construction
    /// (there is no <c>contentGet</c> route over the CLI, only over the images service, §6), unchanged
    /// from before this mechanical move.
    /// </summary>
    public static string BlobPath(string appRoot, string digest)
    {
        var colon = digest.IndexOf(':', StringComparison.Ordinal);
        var algorithm = colon < 0 ? "sha256" : digest[..colon];
        var hex = colon < 0 ? digest : digest[(colon + 1)..];
        return Path.Combine(appRoot, "content", "blobs", algorithm, hex);
    }

    /// <summary>CLI-transport convenience: resolves <paramref name="digest"/> to its AppRoot path
    /// itself, then reads it — the same shape <c>AppleContainerRuntime.Images.cs</c>'s own
    /// <c>TryReadLocalBlobAsync</c> had before this move.</summary>
    public static Task<T?> TryReadBlobAsync<T>(string appRoot, string? digest, ILogger logger, CancellationToken ct)
        where T : class =>
        string.IsNullOrEmpty(digest) ? Task.FromResult<T?>(null) : TryReadAsync<T>(BlobPath(appRoot, digest), logger, ct);

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)ContentStoreJsonContext.Default.Options.GetTypeInfo(typeof(T)));
}

// ---- OCI documents read from the content store ------------------------------------------------
// Moved here verbatim from Cli/Models/ImageModels.cs (mechanical move, task cider-ede.10's file
// scope) except AppleOciManifest's Config/Layers, which now point at OciDescriptor (below) instead
// of Cli.Models.AppleDescriptor, so this namespace does not have to depend back on Cli.Models —
// the two shapes were already field-for-field identical (digest/mediaType/size).

/// <summary>The OCI image config document (a manifest variant's <c>config</c> blob).</summary>
internal sealed class AppleOciImageDocument
{
    public string? Architecture { get; set; }

    public string? Os { get; set; }

    public string? Variant { get; set; }

    public string? Author { get; set; }

    public DateTimeOffset? Created { get; set; }

    /// <summary>The Docker/OCI config block; its keys are PascalCase on the wire.</summary>
    public AppleOciConfig? Config { get; set; }

    public AppleOciRootFs? Rootfs { get; set; }

    /// <summary>
    /// The OCI config's build history, one entry per Dockerfile instruction. Carried through
    /// verbatim, and what <c>docker history</c> is made of.
    /// </summary>
    public List<AppleOciHistory>? History { get; set; }
}

/// <summary>One <c>config.history[]</c> entry — snake_case on the wire.</summary>
internal sealed class AppleOciHistory
{
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    public string? Comment { get; set; }

    public string? Author { get; set; }

    [JsonPropertyName("empty_layer")]
    public bool EmptyLayer { get; set; }
}

internal sealed class AppleOciConfig
{
    public List<string>? Env { get; set; }

    public List<string>? Cmd { get; set; }

    public List<string>? Entrypoint { get; set; }

    public string? WorkingDir { get; set; }

    public string? User { get; set; }

    public Dictionary<string, JsonElement>? ExposedPorts { get; set; }

    public Dictionary<string, JsonElement>? Volumes { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public string? StopSignal { get; set; }

    public AppleOciHealthcheck? Healthcheck { get; set; }
}

/// <summary>
/// The shape of the OCI image manifest blob — enough to chase its <c>config.digest</c> down to the
/// real config blob and to read each layer's real byte size (<c>layers[].size</c>) — neither the CLI
/// transport's own <c>container image inspect</c> nor the XPC transport's <c>imageList</c> report a
/// per-layer breakdown or a real total image size on their own (docs/spikes/xpc/02-apiserver-xpc-protocol.md
/// §6: <c>getFullImageSize</c> walks this same manifest).
/// </summary>
internal sealed class AppleOciManifest
{
    public OciDescriptor? Config { get; set; }

    /// <summary>The manifest's layer descriptors, oldest first, each carrying a real <c>size</c>.</summary>
    public List<OciDescriptor>? Layers { get; set; }
}

internal sealed class AppleOciHealthcheck
{
    public List<string>? Test { get; set; }

    public long? Interval { get; set; }

    public long? Timeout { get; set; }

    public int? Retries { get; set; }

    public long? StartPeriod { get; set; }

    public long? StartInterval { get; set; }
}

internal sealed class AppleOciRootFs
{
    public string? Type { get; set; }

    [JsonPropertyName("diff_ids")]
    public List<string>? DiffIds { get; set; }
}

// ---- The OCI image index blob (new: cider-ede.10 — the CLI transport never needed this, since
// `container image ls`/`image inspect` already do the index→manifest merge server-side; the XPC
// transport's `imageList` reply only carries the index descriptor per image, so cider must walk the
// index itself — docs/spikes/xpc/02-apiserver-xpc-protocol.md §6, verified live against a real
// buildkit-built image's on-disk index blob: {"schemaVersion":2,"mediaType":"application/vnd.oci.
// image.index.v1+json","manifests":[{"mediaType":"...manifest.v1+json","digest":"sha256:...",
// "size":668,"platform":{"architecture":"arm64","os":"linux"}}]}.

/// <summary>An OCI image index blob — one <c>manifests[]</c> entry per platform (plus, for a
/// buildkit-built image, one attestation entry per platform whose <c>platform.architecture ==
/// "unknown"</c>, to be filtered out exactly like the CLI transport's own <c>IsAttestation</c>).</summary>
internal sealed class OciIndex
{
    public List<OciDescriptor>? Manifests { get; set; }
}

/// <summary>A bare OCI descriptor — <c>{mediaType, digest, size}</c> plus, on an index's manifest
/// entries only, <c>platform</c>. Verified live (see <see cref="OciIndex"/>'s own doc comment) to be
/// field-for-field the same shape whether it names a manifest (index → manifest) or a config blob
/// (manifest → config), so one type serves both, instead of importing <c>Cli.Models.AppleDescriptor</c>
/// across namespaces for what is the same four fields.</summary>
internal sealed class OciDescriptor
{
    public string? MediaType { get; set; }

    public string? Digest { get; set; }

    public long? Size { get; set; }

    public OciPlatform? Platform { get; set; }
}

internal sealed class OciPlatform
{
    public string? Os { get; set; }

    public string? Architecture { get; set; }

    public string? Variant { get; set; }
}

/// <summary>
/// The source-generated contracts for on-disk OCI blobs — tolerant and case-insensitive, exactly
/// like <c>Cli.AppleJsonContext</c> (the same on-disk JSON world, not the strict wire Codable rules
/// <c>Xpc.XpcJsonContext</c> enforces for the apiserver's own protocol).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppleOciImageDocument))]
[JsonSerializable(typeof(AppleOciManifest))]
[JsonSerializable(typeof(AppleOciHistory))]
[JsonSerializable(typeof(AppleOciConfig))]
[JsonSerializable(typeof(AppleOciHealthcheck))]
[JsonSerializable(typeof(AppleOciRootFs))]
[JsonSerializable(typeof(OciIndex))]
[JsonSerializable(typeof(OciDescriptor))]
[JsonSerializable(typeof(OciPlatform))]
internal sealed partial class ContentStoreJsonContext : JsonSerializerContext;
