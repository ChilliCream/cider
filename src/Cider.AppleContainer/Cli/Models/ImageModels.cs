using Cider.AppleContainer.ContentStore;

namespace Cider.AppleContainer.Cli.Models;

// Shapes of `container image ls --format json` / `container image inspect <ref>` on 1.2.2
// (docs/apple-container-notes.md §2). One array entry per image *reference*; each entry
// carries a `variants[]` array with one entry per platform plus one attestation entry per
// platform whose `platform.architecture == "unknown"` (to be filtered out).

internal sealed class AppleImageJson
{
    public AppleImageConfiguration? Configuration { get; set; }

    /// <summary>The index digest as bare hex — no <c>sha256:</c> prefix. Never mutated after
    /// deserialization; <see cref="ContentAddressedId"/> carries the recovered, Docker-shaped id
    /// separately so this always stays the raw value a container's own
    /// <c>configuration.image.descriptor.digest</c> would carry (cider-ger.19).</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Set by <c>AppleContainerRuntime.RecoverContentAddressedIdsAsync</c> (cider-ger.19) to the
    /// picked variant's config blob digest, read back from Apple's local content store — the value
    /// that is actually stable across reloads of identical content, unlike <see cref="Id"/>. Null
    /// until recovery runs, or when the local store had nothing to recover.
    /// </summary>
    public string? ContentAddressedId { get; set; }

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

// AppleOciImageDocument/AppleOciHistory/AppleOciConfig/AppleOciManifest/AppleOciHealthcheck/
// AppleOciRootFs moved to Cider.AppleContainer.ContentStore.LocalBlobReader.cs (task cider-ede.10's
// file scope: "mechanical move; CLI runtime keeps using it" — the XPC transport's own image routes
// need the same OCI blob shapes to walk imageList's index→manifest→config chain, so they now live
// in one place both transports reference).
