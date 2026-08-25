using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Satisfies <c>containerCreate</c>'s first precondition (docs/spikes/xpc/02-apiserver-xpc-protocol.md
/// §8.3: "the image snapshot must already exist — call snapshotGet/imageUnpack on the images service
/// first") for the container's own image. By the time <c>CreateContainerAsync</c> runs, the image
/// itself is already pulled — <c>ContainerManager.CreateAsync</c> calls
/// <c>ImageManager.EnsureImageAsync</c> before ever building a <c>ContainerSpec</c> — so this never
/// needs <c>imagePull</c>, only the match + snapshot-unpack sequence (task fix direction §2, §3.2
/// item 3).
/// </summary>
internal sealed class ImageSnapshotEnsurer(ImagesServiceClient images)
{
    /// <summary>
    /// <c>imageList</c> → match <paramref name="reference"/> → <c>snapshotGet</c>; on <c>notFound</c>
    /// → <c>imageUnpack</c> then <c>snapshotGet</c> again (§3.2 item 3, §6). Throws
    /// <see cref="RuntimeException"/> <see cref="RuntimeErrorKind.NotFound"/> when
    /// <paramref name="reference"/> is not in <c>imageList</c> at all — this ensurer never pulls.
    /// </summary>
    public async Task<ImageDescription> EnsureAsync(string reference, Platform platform, CancellationToken ct)
    {
        var descriptions = await images.ImageListAsync(ct).ConfigureAwait(false);
        var match = Match(descriptions, reference)
            ?? throw RuntimeException.NotFound($"cider: image '{reference}' is not present locally");

        try
        {
            await images.SnapshotGetAsync(match, platform, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) == RuntimeErrorKind.NotFound)
        {
            await images.ImageUnpackAsync(match, platform, ct).ConfigureAwait(false);
            await images.SnapshotGetAsync(match, platform, ct).ConfigureAwait(false);
        }

        return match;
    }

    /// <summary>
    /// <c>ClientImage.get(reference:)</c>'s own matching rule (docs/spikes/xpc/02-apiserver-xpc-protocol.md
    /// §6, "How ClientImage.get / config() read image configs"): prefer the index descriptor
    /// annotation <c>containerizationImageName</c>, else exact <c>reference</c> equality. Shared with
    /// <see cref="InitImageResolver"/>, which runs the identical match for the vminit image.
    /// </summary>
    internal static ImageDescription? Match(List<ImageDescription> descriptions, string reference)
    {
        foreach (var description in descriptions)
        {
            if (description.Descriptor.Annotations is { } annotations &&
                annotations.TryGetValue("containerizationImageName", out var name) &&
                string.Equals(name, reference, StringComparison.Ordinal))
            {
                return description;
            }
        }

        foreach (var description in descriptions)
        {
            if (string.Equals(description.Reference, reference, StringComparison.Ordinal))
            {
                return description;
            }
        }

        return null;
    }
}
