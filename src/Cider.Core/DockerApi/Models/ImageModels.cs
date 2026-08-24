using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary>One entry of <c>GET /images/json</c>.</summary>
public sealed class ImageSummary
{
    public string Id { get; set; } = "";
    public string ParentId { get; set; } = "";
    public List<string> RepoTags { get; set; } = [];
    public List<string> RepoDigests { get; set; } = [];
    public long Created { get; set; }
    public long Size { get; set; }
    /// <summary>
    /// Docker's "not computed" sentinel. Real dockerd fills this in for
    /// <c>GET /images/json?shared-size=true</c> by summing the layers this image shares with
    /// another; Apple's <c>container</c> reports only a per-image total size and layer *diff ids*
    /// (no per-layer byte sizes anywhere in <c>container image inspect</c>), so there is nothing to
    /// derive the shared bytes from. The sentinel stays -1 even when the client asks rather than
    /// reporting a fabricated number.
    /// </summary>
    public long SharedSize { get; set; } = -1;

    /// <summary>
    /// The deprecated alias of <see cref="Size"/>: dockerd stopped emitting it in API 1.44, so it is
    /// populated only for requests on <c>&lt;= 1.43</c> and omitted entirely — not written as
    /// <c>null</c> — otherwise. See <c>ImageRoutes.ApplyVirtualSize</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? VirtualSize { get; set; }

    public Dictionary<string, string> Labels { get; set; } = [];
    public long Containers { get; set; } = -1;
}

/// <summary><c>GET /images/{name}/json</c>.</summary>
public sealed class ImageInspectResponse
{
    public string Id { get; set; } = "";
    public List<string> RepoTags { get; set; } = [];
    public List<string> RepoDigests { get; set; } = [];
    public string Parent { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Created { get; set; } = "";
    public string Container { get; set; } = "";
    public ContainerConfig? ContainerConfig { get; set; }
    public string DockerVersion { get; set; } = "";
    public string Author { get; set; } = "";
    public ContainerConfig Config { get; set; } = new();
    public string Architecture { get; set; } = "arm64";
    public string Variant { get; set; } = "";
    public string Os { get; set; } = "linux";
    public string OsVersion { get; set; } = "";
    public long Size { get; set; }

    /// <summary>
    /// The deprecated alias of <see cref="Size"/>, gated exactly like
    /// <see cref="ImageSummary.VirtualSize"/>: present on <c>&lt;= 1.43</c>, omitted on
    /// <c>&gt;= 1.44</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? VirtualSize { get; set; }

    public GraphDriverData GraphDriver { get; set; } = new();
    public RootFS RootFS { get; set; } = new();
    public ImageMetadata Metadata { get; set; } = new();
}

/// <summary><c>ImageInspectResponse.RootFS</c>.</summary>
public sealed class RootFS
{
    public string Type { get; set; } = "layers";
    public List<string> Layers { get; set; } = [];
}

/// <summary><c>ImageInspectResponse.Metadata</c>.</summary>
public sealed class ImageMetadata
{
    public string LastTagTime { get; set; } = "0001-01-01T00:00:00Z";
}

/// <summary>
/// One entry of the <c>DELETE /images/{name}</c> response array. Go's <c>omitempty</c> means an
/// entry carries exactly one of the two keys; docker-py's <c>RemoveImageTest::test_remove</c> tests
/// dict membership, so the absent one must be omitted rather than written as <c>null</c>. This is a
/// per-member opt-out — the global <c>DockerJson.Options</c> default stays
/// <see cref="JsonIgnoreCondition.Never"/> on purpose (docs/ARCHITECTURE.md §4).
/// </summary>
public sealed class ImageDeleteResponseItem
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Untagged { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Deleted { get; set; }
}

/// <summary>One entry of <c>GET /images/{name}/history</c>.</summary>
public sealed class ImageHistoryItem
{
    public string Id { get; set; } = "";
    public long Created { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<string>? Tags { get; set; }
    public long Size { get; set; }
    public string Comment { get; set; } = "";
}

/// <summary><c>POST /images/prune</c>.</summary>
public sealed class ImagePruneResponse
{
    public List<ImageDeleteResponseItem> ImagesDeleted { get; set; } = [];
    public long SpaceReclaimed { get; set; }
}

/// <summary>Docker's <c>IDResponse</c>: the 201 body of <c>POST /commit</c>.</summary>
public sealed class IdResponse
{
    public string Id { get; set; } = "";
}

/// <summary><c>POST /images/load</c> — the body is an NDJSON progress stream, this carries the summary line.</summary>
public sealed class ImageLoadResponse
{
    public string Stream { get; set; } = "";
}

/// <summary>One entry of <c>GET /images/search</c> — Docker uses snake_case here.</summary>
public sealed class ImageSearchItem
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("is_official")]
    public bool IsOfficial { get; set; }

    [JsonPropertyName("is_automated")]
    public bool IsAutomated { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("star_count")]
    public int StarCount { get; set; }
}

/// <summary>The <c>aux</c> payload of a build progress message.</summary>
public sealed class BuildResultAux
{
    public string ID { get; set; } = "";
}

/// <summary>Docker's <c>jsonmessage.JSONMessage</c> — the NDJSON line of pull/push/build/load streams.</summary>
public sealed class JsonMessage
{
    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stream { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("progressDetail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonProgress? ProgressDetail { get; set; }

    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Progress { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Time { get; set; }

    [JsonPropertyName("timeNano")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TimeNano { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("errorDetail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonError? ErrorDetail { get; set; }

    [JsonPropertyName("aux")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Aux { get; set; }
}

/// <summary><c>JsonMessage.progressDetail</c>.</summary>
public sealed class JsonProgress
{
    [JsonPropertyName("current")]
    public long Current { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }
}

/// <summary><c>JsonMessage.errorDetail</c>.</summary>
public sealed class JsonError
{
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary><c>POST /build/prune</c> response — Apple <c>container</c> exposes no build cache to report on.</summary>
public sealed class BuildCachePruneResponse
{
    public List<string> CachesDeleted { get; set; } = [];
    public long SpaceReclaimed { get; set; }
}
