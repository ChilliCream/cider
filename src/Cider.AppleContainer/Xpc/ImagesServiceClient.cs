using System.Text.Json;
using Cider.AppleContainer.Xpc.Models;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// The three <c>com.apple.container.core.container-core-images</c> routes task cider-ede.6 needs to
/// satisfy <c>containerCreate</c>'s preconditions (docs/spikes/xpc/02-apiserver-xpc-protocol.md §6,
/// §8.3: "the image snapshot must already exist"). Deliberately minimal — <c>imagePull</c>,
/// <c>imageTag</c>, <c>imageDelete</c>, <c>contentGet</c> and the rest of the images service belong to
/// cider-ede.10 (images over the images service), which extends this same class rather than
/// duplicating the route plumbing (task's file-scope note).
/// </summary>
internal sealed class ImagesServiceClient(XpcClient images)
{
    /// <summary><c>imageUnpack</c> decompresses/unpacks a whole image layer set and can legitimately
    /// take much longer than the 60s default (§1.4) on a large image — still on the shared connection
    /// (task's binding ruling in <see cref="XpcClient"/> reserves a dedicated connection for
    /// wait/logs/dial specifically, not this route), just with more headroom.</summary>
    private static readonly XpcCallOptions UnpackOptions = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary><c>imageList</c> — no request payload (§6). <c>[]</c> when the reply carries no
    /// <c>imageDescriptions</c> key.</summary>
    public async Task<List<ImageDescription>> ImageListAsync(CancellationToken ct)
    {
        using var request = new XpcMessage("imageList");
        using var reply = await images.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

        var bytes = reply.GetData("imageDescriptions");
        return bytes is null ? [] : XpcJson.Deserialize<List<ImageDescription>>(bytes);
    }

    /// <summary><c>snapshotGet</c> — <c>ociPlatform</c> is required (§6), unlike <c>imageUnpack</c>'s
    /// optional one. Throws <see cref="XpcException"/> with code <c>notFound</c> when no snapshot
    /// exists yet — the caller's cue to <see cref="ImageUnpackAsync"/> then retry (§3.2 item 3).</summary>
    public async Task<Filesystem> SnapshotGetAsync(ImageDescription image, Platform platform, CancellationToken ct)
    {
        using var request = new XpcMessage("snapshotGet");
        request.SetData("imageDescription", XpcJson.SerializeToUtf8Bytes(image));
        request.SetData("ociPlatform", XpcJson.SerializeToUtf8Bytes(platform));
        using var reply = await images.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

        var bytes = reply.GetData("filesystem")
            ?? throw new JsonException("snapshotGet reply carried no filesystem");
        return XpcJson.Deserialize<Filesystem>(bytes);
    }

    /// <summary><c>imageUnpack</c> — no reply payload beyond the route key (§6); omits the optional
    /// <c>progressUpdateEndpoint</c> (out of scope here, cider-ede.10's job per §5).</summary>
    public async Task ImageUnpackAsync(ImageDescription image, Platform platform, CancellationToken ct)
    {
        using var request = new XpcMessage("imageUnpack");
        request.SetData("imageDescription", XpcJson.SerializeToUtf8Bytes(image));
        request.SetData("ociPlatform", XpcJson.SerializeToUtf8Bytes(platform));
        using var reply = await images.SendAsync(request, UnpackOptions, ct).ConfigureAwait(false);
    }
}
