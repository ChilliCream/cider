using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Cli.Models;

// Shapes of `container image ls --format json` / `container image inspect <ref>` on 1.2.2
// (docs/apple-container-notes.md §2). One array entry per image *reference*; each entry
// carries a `variants[]` array with one entry per platform plus one attestation entry per
// platform whose `platform.architecture == "unknown"` (to be filtered out).

internal sealed class AppleImageJson
{
    public AppleImageConfiguration? Configuration { get; set; }

    /// <summary>The index digest as bare hex — no <c>sha256:</c> prefix.</summary>
    public string? Id { get; set; }

    public List<AppleImageVariant>? Variants { get; set; }

    /// <summary>Newer CLIs may print a short display reference; 1.2.2 does not.</summary>
    public string? DisplayReference { get; set; }

    public string? Name { get; set; }
}

internal sealed class AppleImageConfiguration
{
    /// <summary>Full normalized reference, e.g. <c>docker.io/library/alpine:3.22</c>.</summary>
    public string? Name { get; set; }

    public AppleDescriptor? Descriptor { get; set; }

    public DateTimeOffset? CreationDate { get; set; }
}

internal sealed class AppleImageVariant
{
    public AppleOciImageDocument? Config { get; set; }

    public string? Digest { get; set; }

    public AplePlatform? Platform { get; set; }

    public long? Size { get; set; }

    /// <summary>True for buildkit's attestation/provenance manifests, which must be skipped.</summary>
    public bool IsAttestation =>
        string.Equals(Platform?.Architecture, "unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Platform?.Os, "unknown", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The OCI image config document (<c>variants[i].config</c>).</summary>
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
    /// The OCI config's build history, one entry per Dockerfile instruction. Apple carries it
    /// through from the image config verbatim, and it is what <c>docker history</c> is made of.
    /// </summary>
    public List<AppleOciHistory>? History { get; set; }
}

/// <summary>One <c>variants[i].config.history[]</c> entry — snake_case on the wire.</summary>
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
/// The shape of the OCI image manifest blob (<c>content/blobs/sha256/&lt;variant.digest&gt;</c> under
/// AppRoot) — enough to chase its <c>config.digest</c> down to the real config blob (see
/// <c>AppleContainerRuntime.RecoverExposedPortsAsync</c>) and to read each layer's real byte size
/// (see <c>AppleContainerRuntime.RecoverLayerSizesAsync</c>) — <c>container image inspect</c> reports
/// only one total size per platform variant, never a per-layer breakdown.
/// </summary>
internal sealed class AppleOciManifest
{
    public AppleDescriptor? Config { get; set; }

    /// <summary>The manifest's layer descriptors, oldest first, each carrying a real <c>size</c>.</summary>
    public List<AppleDescriptor>? Layers { get; set; }
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
