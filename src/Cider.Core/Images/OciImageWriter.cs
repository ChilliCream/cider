using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cider.Core.DockerApi;
using Cider.Core.Runtime;

namespace Cider.Core.Images;

/// <summary>
/// Builds a single-layer OCI-layout tarball out of a raw root-filesystem tar, which is how
/// <c>docker commit</c> and <c>docker import</c> are emulated: Apple <c>container</c> has no commit
/// primitive, but <c>container image load</c> happily accepts an OCI layout
/// (docs/apple-container-notes.md §2), so the daemon turns the rootfs snapshot
/// (<see cref="IContainerRuntime.ExportContainerAsync"/> / the client's import body) into a real
/// image itself.
/// <para>
/// The layout mirrors what Apple's own <c>container image save</c> writes, because Apple keys an
/// image by the digest of the <em>index</em> blob (<c>container image ls</c>'s <c>DIGEST</c> column
/// == <c>configuration.descriptor.digest</c>, media type
/// <c>application/vnd.oci.image.index.v1+json</c>): <c>index.json</c> holds one descriptor for a
/// nested image index — annotated with the reference name — which in turn holds the single
/// platform manifest. Writing the manifest straight into <c>index.json</c> would give the loaded
/// image a manifest-shaped descriptor instead, so the nesting is deliberate, not accidental.
/// </para>
/// </summary>
public static class OciImageWriter
{
    private const string IndexMediaType = "application/vnd.oci.image.index.v1+json";
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string ConfigMediaType = "application/vnd.oci.image.config.v1+json";
    private const string LayerMediaType = "application/vnd.oci.image.layer.v1.tar+gzip";
    private const string RefNameAnnotation = "org.opencontainers.image.ref.name";

    /// <summary>
    /// Writes the OCI-layout tar for <paramref name="spec"/> to <paramref name="tarPath"/>, using
    /// <paramref name="rootFsTar"/> (an uncompressed tar of the whole root filesystem) as its one
    /// and only layer, and returns the image id the runtime will know it by
    /// (<c>sha256:</c> + the digest of the index blob).
    /// </summary>
    /// <param name="workDir">
    /// Scratch directory for the blob files; created if missing and left behind for the caller to
    /// delete (the blobs are as large as the image itself, so they are never buffered in memory).
    /// </param>
    public static async Task<string> WriteAsync(
        OciImageSpec spec,
        Stream rootFsTar,
        string tarPath,
        string workDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rootFsTar);
        ArgumentException.ThrowIfNullOrEmpty(tarPath);
        ArgumentException.ThrowIfNullOrEmpty(workDir);

        var blobDir = Path.Combine(workDir, "blobs", "sha256");
        Directory.CreateDirectory(blobDir);

        var layer = await WriteLayerAsync(rootFsTar, blobDir, ct).ConfigureAwait(false);

        var config = new OciImageConfigBlob
        {
            Created = Time.DockerTime.Format(spec.Created),
            Author = string.IsNullOrEmpty(spec.Author) ? null : spec.Author,
            Architecture = spec.Architecture,
            Os = spec.Os,
            Variant = string.IsNullOrEmpty(spec.Variant) ? null : spec.Variant,
            Config = ToConfigBlock(spec.Config),
            RootFs = new OciRootFs { DiffIds = [layer.DiffId] },
            History =
            [
                new OciHistory
                {
                    Created = Time.DockerTime.Format(spec.Created),
                    CreatedBy = spec.CreatedBy ?? "",
                    Comment = string.IsNullOrEmpty(spec.Comment) ? null : spec.Comment,
                },
            ],
        };

        var configBlob = await WriteJsonBlobAsync(config, blobDir, ct).ConfigureAwait(false);

        var manifest = new OciManifest
        {
            MediaType = ManifestMediaType,
            Config = new OciDescriptor { MediaType = ConfigMediaType, Digest = configBlob.Digest, Size = configBlob.Size },
            Layers =
            [
                new OciDescriptor { MediaType = LayerMediaType, Digest = layer.Digest, Size = layer.Size },
            ],
        };

        var manifestBlob = await WriteJsonBlobAsync(manifest, blobDir, ct).ConfigureAwait(false);

        var index = new OciIndex
        {
            MediaType = IndexMediaType,
            Manifests =
            [
                new OciDescriptor
                {
                    MediaType = ManifestMediaType,
                    Digest = manifestBlob.Digest,
                    Size = manifestBlob.Size,
                    Platform = new OciPlatform
                    {
                        Architecture = spec.Architecture,
                        Os = spec.Os,
                        Variant = string.IsNullOrEmpty(spec.Variant) ? null : spec.Variant,
                    },
                },
            ],
        };

        var indexBlob = await WriteJsonBlobAsync(index, blobDir, ct).ConfigureAwait(false);

        var top = new OciIndex
        {
            MediaType = IndexMediaType,
            Manifests =
            [
                new OciDescriptor
                {
                    MediaType = IndexMediaType,
                    Digest = indexBlob.Digest,
                    Size = indexBlob.Size,
                    Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [RefNameAnnotation] = spec.Reference,
                    },
                },
            ],
        };

        var indexJsonPath = Path.Combine(workDir, "index.json");
        await File.WriteAllBytesAsync(indexJsonPath, JsonSerializer.SerializeToUtf8Bytes(top, OciBlobJsonContext.Default.OciIndex), ct).ConfigureAwait(false);

        var layoutPath = Path.Combine(workDir, "oci-layout");
        await File.WriteAllBytesAsync(layoutPath, "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray(), ct).ConfigureAwait(false);

        await using (var tarStream = File.Create(tarPath))
        await using (var tar = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: false))
        {
            await AddFileAsync(tar, layoutPath, "oci-layout", ct).ConfigureAwait(false);
            await AddFileAsync(tar, indexJsonPath, "index.json", ct).ConfigureAwait(false);
            foreach (var blob in Directory.EnumerateFiles(blobDir).Order(StringComparer.Ordinal))
            {
                await AddFileAsync(tar, blob, $"blobs/sha256/{Path.GetFileName(blob)}", ct).ConfigureAwait(false);
            }
        }

        return indexBlob.Digest;
    }

    // ---- helpers ------------------------------------------------------

    private static async Task<LayerBlob> WriteLayerAsync(Stream rootFsTar, string blobDir, CancellationToken ct)
    {
        var staging = Path.Combine(blobDir, $"layer-{Guid.NewGuid():N}.tmp");
        using var rawHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var gzipHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long compressed;
        await using (var file = File.Create(staging))
        {
            await using (var hashing = new HashingStream(file, gzipHash))
            // Fastest, not Optimal: unlike a real `docker commit` this layer is the *whole* root
            // filesystem (Apple gives no layer diff), it never leaves this machine, and the extra
            // ratio would cost seconds of commit latency on every ordinary image.
            await using (var gzip = new GZipStream(hashing, CompressionLevel.Fastest, leaveOpen: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await rootFsTar.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    rawHash.AppendData(buffer.AsSpan(0, read));
                    await gzip.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            compressed = file.Length;
        }

        var digest = "sha256:" + Convert.ToHexStringLower(gzipHash.GetHashAndReset());
        var diffId = "sha256:" + Convert.ToHexStringLower(rawHash.GetHashAndReset());
        var final = Path.Combine(blobDir, digest["sha256:".Length..]);
        File.Move(staging, final, overwrite: true);

        return new LayerBlob(digest, diffId, compressed);
    }

    private static async Task<Blob> WriteJsonBlobAsync<T>(T value, string blobDir, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            (JsonTypeInfo<T>)OciBlobJsonContext.Default.Options.GetTypeInfo(typeof(T)));
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload));
        await File.WriteAllBytesAsync(Path.Combine(blobDir, digest["sha256:".Length..]), payload, ct).ConfigureAwait(false);
        return new Blob(digest, payload.Length);
    }

    private static async Task AddFileAsync(TarWriter tar, string path, string entryName, CancellationToken ct)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };
        await using var content = File.OpenRead(path);
        entry.DataStream = content;
        await tar.WriteEntryAsync(entry, ct).ConfigureAwait(false);
    }

    private static OciConfigBlock ToConfigBlock(ImageConfig config) => new()
    {
        Env = config.Env.Count > 0 ? [.. config.Env] : null,
        Cmd = config.Cmd.Count > 0 ? [.. config.Cmd] : null,
        Entrypoint = config.Entrypoint.Count > 0 ? [.. config.Entrypoint] : null,
        WorkingDir = string.IsNullOrEmpty(config.WorkingDir) ? null : config.WorkingDir,
        User = string.IsNullOrEmpty(config.User) ? null : config.User,
        ExposedPorts = config.ExposedPorts.Count > 0
            ? config.ExposedPorts.Distinct(StringComparer.Ordinal).ToDictionary(p => p, _ => new Dictionary<string, string>(), StringComparer.Ordinal)
            : null,
        Volumes = config.Volumes.Count > 0
            ? config.Volumes.Distinct(StringComparer.Ordinal).ToDictionary(v => v, _ => new Dictionary<string, string>(), StringComparer.Ordinal)
            : null,
        Labels = config.Labels.Count > 0 ? new Dictionary<string, string>(config.Labels, StringComparer.Ordinal) : null,
        StopSignal = string.IsNullOrEmpty(config.StopSignal) ? null : config.StopSignal,
    };

    private readonly record struct Blob(string Digest, long Size);

    private readonly record struct LayerBlob(string Digest, string DiffId, long Size);

    /// <summary>Writes through to an inner stream while digesting everything that passes.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Everything <see cref="OciImageWriter"/> needs besides the root filesystem itself.</summary>
public sealed record OciImageSpec
{
    /// <summary>The fully normalized reference the loaded image is tagged with, e.g. <c>docker.io/library/app:1</c>.</summary>
    public required string Reference { get; init; }

    /// <summary>The image's <c>config</c> block (already overlaid with any <c>changes</c>).</summary>
    public ImageConfig Config { get; init; } = new();

    public string Architecture { get; init; } = "arm64";

    public string Os { get; init; } = "linux";

    public string? Variant { get; init; }

    public string? Author { get; init; }

    /// <summary>Free-text comment, e.g. <c>docker commit -m</c> / <c>docker import -m</c>.</summary>
    public string? Comment { get; init; }

    /// <summary>The single history entry's <c>created_by</c>.</summary>
    public string? CreatedBy { get; init; }

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The <c>changes</c> parameter of <c>POST /commit</c> and <c>POST /images/create?fromSrc=</c>: a list
/// of Dockerfile instructions applied on top of the source configuration. Docker itself accepts a
/// small subset there and rejects everything else with a 400; this is that subset —
/// <c>CMD</c>, <c>ENTRYPOINT</c>, <c>ENV</c>, <c>EXPOSE</c>, <c>WORKDIR</c>, <c>USER</c>,
/// <c>LABEL</c>, <c>VOLUME</c>.
/// </summary>
public static class ImageChanges
{
    /// <summary>Applies <paramref name="changes"/> to <paramref name="config"/>; throws a 400 for anything unsupported.</summary>
    public static ImageConfig Apply(ImageConfig config, IReadOnlyList<string> changes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(changes);

        var result = config;
        foreach (var change in changes)
        {
            var line = change.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0)
            {
                throw DockerErrors.BadParameter($"Dockerfile directive '{line}' requires an argument");
            }

            var instruction = line[..space].ToUpperInvariant();
            var argument = line[(space + 1)..].Trim();
            if (argument.Length == 0)
            {
                throw DockerErrors.BadParameter($"Dockerfile directive '{instruction}' requires an argument");
            }

            result = instruction switch
            {
                "CMD" => result with { Cmd = ParseCommand(argument) },
                "ENTRYPOINT" => result with { Entrypoint = ParseCommand(argument) },
                "ENV" => result with { Env = MergeEnv(result.Env, ParseKeyValue(instruction, argument)) },
                "EXPOSE" => result with { ExposedPorts = MergePorts(result.ExposedPorts, argument) },
                "WORKDIR" => result with { WorkingDir = Unquote(argument) },
                "USER" => result with { User = Unquote(argument) },
                "LABEL" => result with { Labels = MergeLabels(result.Labels, ParseKeyValue(instruction, argument)) },
                "VOLUME" => result with { Volumes = MergeVolumes(result.Volumes, argument) },
                _ => throw DockerErrors.BadParameter(
                    $"cider: unsupported Dockerfile directive in changes: {instruction} " +
                    "(supported: CMD, ENTRYPOINT, ENV, EXPOSE, WORKDIR, USER, LABEL, VOLUME)"),
            };
        }

        return result;
    }

    /// <summary>Splits raw <c>changes</c> query values (Docker allows several per value, newline separated).</summary>
    public static IReadOnlyList<string> Split(IEnumerable<string?> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);

        var result = new List<string>();
        foreach (var value in rawValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var line in value.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }

    private static List<string> ParseCommand(string argument)
    {
        if (TryParseJsonArray(argument, out var parsed))
        {
            return parsed;
        }

        // Shell form, exactly like a Dockerfile's: run it through the default shell.
        return ["/bin/sh", "-c", argument];
    }

    private static bool TryParseJsonArray(string argument, out List<string> values)
    {
        values = [];
        if (!argument.StartsWith('[') || !argument.EndsWith(']'))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(argument, OciBlobJsonContext.Default.ListString);
            if (parsed is null)
            {
                return false;
            }

            values = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            throw DockerErrors.BadParameter($"invalid JSON array in changes: {argument} ({ex.Message})");
        }
    }

    private static (string Key, string Value) ParseKeyValue(string instruction, string argument)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        var space = argument.IndexOfAny([' ', '\t']);
        if (equals >= 0 && (space < 0 || equals < space))
        {
            return (Unquote(argument[..equals]), Unquote(argument[(equals + 1)..]));
        }

        if (space > 0)
        {
            return (Unquote(argument[..space]), Unquote(argument[(space + 1)..].Trim()));
        }

        throw DockerErrors.BadParameter($"Dockerfile directive '{instruction} {argument}' needs a key and a value");
    }

    private static List<string> MergeEnv(IReadOnlyList<string> env, (string Key, string Value) entry)
    {
        var prefix = entry.Key + "=";
        var result = env.Where(e => !e.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        result.Add(prefix + entry.Value);
        return result;
    }

    private static Dictionary<string, string> MergeLabels(IReadOnlyDictionary<string, string> labels, (string Key, string Value) entry)
    {
        var result = new Dictionary<string, string>(labels, StringComparer.Ordinal) { [entry.Key] = entry.Value };
        return result;
    }

    private static List<string> MergePorts(IReadOnlyList<string> ports, string argument)
    {
        var result = ports.ToList();
        foreach (var raw in argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var port = Unquote(raw);
            var normalized = port.Contains('/', StringComparison.Ordinal) ? port : port + "/tcp";
            if (!result.Contains(normalized, StringComparer.Ordinal))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static List<string> MergeVolumes(IReadOnlyList<string> volumes, string argument)
    {
        var result = volumes.ToList();
        var paths = TryParseJsonArray(argument, out var parsed)
            ? parsed
            : [.. argument.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unquote)];

        foreach (var path in paths)
        {
            if (path.Length > 0 && !result.Contains(path, StringComparer.Ordinal))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}

// ---- OCI blob shapes -------------------------------------------------------

internal sealed class OciDescriptor
{
    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("digest")]
    public string Digest { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("platform")]
    public OciPlatform? Platform { get; init; }

    [JsonPropertyName("annotations")]
    public Dictionary<string, string>? Annotations { get; init; }
}

internal sealed class OciPlatform
{
    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = "";

    [JsonPropertyName("os")]
    public string Os { get; init; } = "";

    [JsonPropertyName("variant")]
    public string? Variant { get; init; }
}

internal sealed class OciIndex
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 2;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("manifests")]
    public List<OciDescriptor> Manifests { get; init; } = [];
}

internal sealed class OciManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 2;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("config")]
    public OciDescriptor Config { get; init; } = new();

    [JsonPropertyName("layers")]
    public List<OciDescriptor> Layers { get; init; } = [];
}

internal sealed class OciImageConfigBlob
{
    [JsonPropertyName("created")]
    public string Created { get; init; } = "";

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = "";

    [JsonPropertyName("os")]
    public string Os { get; init; } = "";

    [JsonPropertyName("variant")]
    public string? Variant { get; init; }

    [JsonPropertyName("config")]
    public OciConfigBlock Config { get; init; } = new();

    [JsonPropertyName("rootfs")]
    public OciRootFs RootFs { get; init; } = new();

    [JsonPropertyName("history")]
    public List<OciHistory> History { get; init; } = [];
}

internal sealed class OciConfigBlock
{
    [JsonPropertyName("User")]
    public string? User { get; init; }

    [JsonPropertyName("ExposedPorts")]
    public Dictionary<string, Dictionary<string, string>>? ExposedPorts { get; init; }

    [JsonPropertyName("Env")]
    public List<string>? Env { get; init; }

    [JsonPropertyName("Entrypoint")]
    public List<string>? Entrypoint { get; init; }

    [JsonPropertyName("Cmd")]
    public List<string>? Cmd { get; init; }

    [JsonPropertyName("Volumes")]
    public Dictionary<string, Dictionary<string, string>>? Volumes { get; init; }

    [JsonPropertyName("WorkingDir")]
    public string? WorkingDir { get; init; }

    [JsonPropertyName("Labels")]
    public Dictionary<string, string>? Labels { get; init; }

    [JsonPropertyName("StopSignal")]
    public string? StopSignal { get; init; }
}

internal sealed class OciRootFs
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "layers";

    [JsonPropertyName("diff_ids")]
    public List<string> DiffIds { get; init; } = [];
}

internal sealed class OciHistory
{
    [JsonPropertyName("created")]
    public string Created { get; init; } = "";

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; init; } = "";

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

/// <summary>
/// The source-generated contracts for the blobs above. The settings are the ones the hand-built
/// <c>BlobJson</c> options carried: OCI blob keys all come from explicit
/// <c>[JsonPropertyName]</c> attributes, and a <c>null</c> optional member must be omitted rather
/// than written (an OCI descriptor with <c>"platform": null</c> is not the same document, and the
/// image id is the digest of these exact bytes).
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OciIndex))]
[JsonSerializable(typeof(OciManifest))]
[JsonSerializable(typeof(OciImageConfigBlob))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class OciBlobJsonContext : JsonSerializerContext;
