using System.Text.Json;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Every <c>com.apple.container.core.container-core-images</c> route cider uses
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §6). cider-ede.6 started this class with the three
/// routes <c>containerCreate</c>'s preconditions need (<c>imageList</c>, <c>snapshotGet</c>,
/// <c>imageUnpack</c>); cider-ede.10 extends it with the rest of the images service —
/// <c>imagePull</c>/<c>imagePush</c>/<c>imageTag</c>/<c>imageDelete</c>/<c>imageSave</c>/
/// <c>imageLoad</c> — plus the content-store's <c>contentGet</c> (task's file-scope note: "extends
/// the X5 stub with all routes"). The sweep routes this class used to carry are gone by design
/// (cider-ede.41): <c>imageCleanupOrphanedBlobs</c> (the whole-store sweep) and cider-ehn's scoped
/// <c>contentDelete</c> were removed with the prune-path sweep — a sweep from one process deletes
/// another process's mid-write pull blobs in the shared store (reproduced in ~2s, commit d63644b).
/// Do not re-wire them; see the prevention comment in <c>XpcContainerRuntime.Images.cs</c>.
/// </summary>
/// <remarks>
/// Not <c>sealed</c>, and <see cref="ImageListAsync"/>/<see cref="ContentGetAsync"/> are <c>virtual</c>
/// — the testability seam cider-ede.24 needs to drive <see cref="XpcContainerRuntime.ListImagesAsync"/>
/// against a fake that fails a specific digest, without a live apiserver connection (there is no
/// per-route interface here; a single override point on the real class is the minimal seam, matching
/// the shape <see cref="XpcContainerRuntime"/>'s own <c>internal</c> test constructor already uses).
/// cider-ede.31 extends the same seam to <see cref="ImagePullAsync"/>/<see cref="ImageUnpackAsync"/>/
/// <see cref="ImageDeleteAsync"/>, so a test can prove <see cref="BlobSweepGate"/> actually blocks a
/// pull against a concurrent sweep and back, without a live apiserver connection either.
/// </remarks>
internal class ImagesServiceClient(XpcClient images, TimeSpan pullTimeout)
{
    /// <summary><c>imageUnpack</c> decompresses/unpacks a whole image layer set and can legitimately
    /// take much longer than the 60s default (§1.4) on a large image — still on the shared connection
    /// (task's binding ruling in <see cref="XpcClient"/> reserves a dedicated connection for
    /// wait/logs/dial specifically, not this route), just with more headroom.</summary>
    private static readonly XpcCallOptions UnpackOptions = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// <c>imagePull</c>/<c>imagePush</c>/<c>imageSave</c>/<c>imageLoad</c> can each legitimately take
    /// as long as a full registry round trip or a large local unpack — bounded by
    /// <see cref="AppleContainerOptions.PullTimeout"/> (the same budget the CLI transport's own pull
    /// already uses), not <see cref="UnpackOptions"/>'s fixed 5 minutes, and deliberately still on the
    /// shared connection rather than <see cref="XpcCallOptions.DedicatedConnection"/> — that flag is
    /// reserved, by the task's own binding ruling on <see cref="XpcClient"/>, for
    /// <c>wait</c>/<c>logs</c>/<c>dial</c> specifically, not these routes.
    /// </summary>
    private readonly XpcCallOptions _longRunningOptions = new() { Timeout = pullTimeout };

    /// <summary><c>imageList</c> — no request payload (§6). <c>[]</c> when the reply carries no
    /// <c>imageDescriptions</c> key.</summary>
    public virtual async Task<List<ImageDescription>> ImageListAsync(CancellationToken ct)
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

    /// <summary><c>imageUnpack</c> — no reply payload beyond the route key (§6).
    /// <paramref name="progressEndpoint"/> is the optional <c>progressUpdateEndpoint</c> (§5: honored
    /// by this route) — <c>null</c> for cider-ede.6's own callers (<see cref="ImageSnapshotEnsurer"/>,
    /// <see cref="InitImageResolver"/>), which unpack a container's own/init image ahead of create and
    /// have no progress stream to report onto; <see cref="XpcContainerRuntime.PullImageAsync"/>
    /// (cider-ede.10) is the one caller that passes one, so the pull's progress bar covers the unpack
    /// step too, exactly as the real CLI's own combined pull+unpack progress bar does
    /// (§5's route table: <c>imagePull</c>/<c>imagePush</c>/<c>imageUnpack</c>/<c>installKernel</c>
    /// all honor it).</summary>
    public virtual async Task ImageUnpackAsync(ImageDescription image, Platform platform, CancellationToken ct, XpcObject? progressEndpoint = null)
    {
        using var request = new XpcMessage("imageUnpack");
        request.SetData("imageDescription", XpcJson.SerializeToUtf8Bytes(image));
        request.SetData("ociPlatform", XpcJson.SerializeToUtf8Bytes(platform));
        if (progressEndpoint is not null)
        {
            request.SetValue("progressUpdateEndpoint", progressEndpoint);
        }

        using var reply = await images.SendAsync(request, UnpackOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>maxConcurrentDownloads</c> on <c>imagePull</c> — Apple's own client always sends it
    /// (<c>ClientImage.swift:253-272</c> at 1.3.0: guarded <c>&gt; 0</c>, default 3, set
    /// unconditionally), and the server treats an ABSENT key as 0: <c>ImagesServiceHarness.swift:51</c>
    /// reads it with <c>message.int64</c> (0 for a missing key) and passes it straight through to
    /// Containerization's <c>ImportOperation</c>, whose <c>fetchAll</c> loop
    /// (<c>ImageStore+Import.swift:126</c>, containerization 0.41.0/5427fd2) starts
    /// <c>0..&lt;maxConcurrentDownloads</c> download tasks — with 0, NOTHING is ever downloaded, the
    /// manifest walk still "succeeds" via its in-memory registry fallback, the empty ingest commits,
    /// and the very next <c>imageUnpack</c> dies at <c>Image.index()</c> with
    /// <c>"content with digest sha256:…"</c> on the INDEX digest (cider-ede.43's exact failure: zero
    /// durable bytes, dangling committed entry, only when content is genuinely absent from disk).
    /// 3 matches Apple's default (<c>Flags.swift:428</c>).
    /// </summary>
    internal const long MaxConcurrentDownloads = 3;

    /// <summary>
    /// <c>imagePull{imageReference, ociPlatform?, insecureFlag:false, maxConcurrentDownloads,
    /// progressUpdateEndpoint?}</c> → <c>imageDescription</c> (§6; fix direction §2).
    /// <paramref name="reference"/> travels verbatim:
    /// <c>ImageManager.PullAsync</c> already normalized it (registry/<c>library/</c>/<c>:latest</c>
    /// defaults) above the <c>IContainerRuntime</c> seam before this is ever called — the very same
    /// normalized form Apple's own index annotates images with (verified live,
    /// docs/spikes/xpc-probe/out-experiments.txt: <c>"docker.io/library/alpine:3.20"</c>), so there is
    /// nothing left for this layer to normalize. <c>insecureFlag</c> is always <c>false</c> — cider has
    /// no insecure-registry option anywhere above this seam, matching the Swift client's own default
    /// scheme (§7: <c>.auto</c> → <c>.https</c>, wire field unchanged).
    /// </summary>
    public virtual async Task<ImageDescription> ImagePullAsync(string reference, Platform? platform, XpcObject? progressEndpoint, CancellationToken ct)
    {
        using var request = BuildImagePullRequest(reference, platform, progressEndpoint);
        using var reply = await images.SendAsync(request, _longRunningOptions, ct).ConfigureAwait(false);

        var bytes = reply.GetData("imageDescription")
            ?? throw new JsonException("imagePull reply carried no imageDescription");
        return XpcJson.Deserialize<ImageDescription>(bytes);
    }

    /// <summary>The <c>imagePull</c> request, built exactly as Apple's <c>ClientImage.pull</c> builds
    /// it (field-by-field diff, cider-ede.43) — split out so
    /// <c>tests/Cider.Tests/AppleContainer/Xpc/ImagesServiceClientPullRequestTests.cs</c> can assert
    /// the wire message (in particular that <c>maxConcurrentDownloads</c> is present and nonzero;
    /// see <see cref="MaxConcurrentDownloads"/>) without a live apiserver.</summary>
    internal static XpcMessage BuildImagePullRequest(string reference, Platform? platform, XpcObject? progressEndpoint)
    {
        var request = new XpcMessage("imagePull");
        request.SetString("imageReference", reference);
        if (platform is not null)
        {
            request.SetData("ociPlatform", XpcJson.SerializeToUtf8Bytes(platform));
        }

        request.SetBool("insecureFlag", false);
        request.SetInt64("maxConcurrentDownloads", MaxConcurrentDownloads);
        if (progressEndpoint is not null)
        {
            request.SetValue("progressUpdateEndpoint", progressEndpoint);
        }

        return request;
    }

    /// <summary><c>imagePush{imageReference, ociPlatform?, insecureFlag:false, progressUpdateEndpoint?}</c>
    /// — no reply payload beyond the route key (§6).</summary>
    public async Task ImagePushAsync(string reference, Platform? platform, XpcObject? progressEndpoint, CancellationToken ct)
    {
        using var request = new XpcMessage("imagePush");
        request.SetString("imageReference", reference);
        if (platform is not null)
        {
            request.SetData("ociPlatform", XpcJson.SerializeToUtf8Bytes(platform));
        }

        request.SetBool("insecureFlag", false);
        if (progressEndpoint is not null)
        {
            request.SetValue("progressUpdateEndpoint", progressEndpoint);
        }

        using var reply = await images.SendAsync(request, _longRunningOptions, ct).ConfigureAwait(false);
    }

    /// <summary><c>imageTag{imageReference, imageNewReference}</c> → <c>imageDescription</c> (§6).</summary>
    public async Task<ImageDescription> ImageTagAsync(string reference, string newReference, CancellationToken ct)
    {
        using var request = new XpcMessage("imageTag");
        request.SetString("imageReference", reference);
        request.SetString("imageNewReference", newReference);
        using var reply = await images.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

        var bytes = reply.GetData("imageDescription")
            ?? throw new JsonException("imageTag reply carried no imageDescription");
        return XpcJson.Deserialize<ImageDescription>(bytes);
    }

    /// <summary><c>imageDelete{imageReference, garbageCollect}</c> — no reply payload beyond the route
    /// key (§6). <c>virtual</c> only for the cider-ede.31 test seam
    /// (<c>tests/Cider.Tests/AppleContainer/Xpc/XpcContainerRuntimeRemoveImageTests.cs</c>), the same
    /// shape <see cref="ImageListAsync"/>/<see cref="ContentGetAsync"/> already use.</summary>
    public virtual async Task ImageDeleteAsync(string reference, bool garbageCollect, CancellationToken ct)
    {
        using var request = new XpcMessage("imageDelete");
        request.SetString("imageReference", reference);
        request.SetBool("garbageCollect", garbageCollect);
        using var reply = await images.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
    }

    /// <summary><c>imageSave{imageDescriptions, filePath}</c> — <c>ociPlatform</c> deliberately omitted
    /// (§6 marks it optional; cider saves every platform a multi-arch image carries, matching the CLI
    /// transport's own <c>image save</c> with no <c>--platform</c> flag) — no reply payload beyond the
    /// route key.</summary>
    public async Task ImageSaveAsync(List<ImageDescription> descriptions, string filePath, CancellationToken ct)
    {
        using var request = new XpcMessage("imageSave");
        request.SetData("imageDescriptions", XpcJson.SerializeToUtf8Bytes(descriptions));
        request.SetString("filePath", filePath);
        using var reply = await images.SendAsync(request, _longRunningOptions, ct).ConfigureAwait(false);
    }

    /// <summary><c>imageLoad{filePath, forceLoad}</c> → <c>imageDescriptions</c> (loaded) +
    /// <c>rejectedMembers</c> (§6). Either data key may legitimately be absent from the reply — an
    /// archive with nothing new loaded, or nothing rejected — so both default to empty rather than
    /// throwing.</summary>
    public async Task<(List<ImageDescription> Loaded, List<string> Rejected)> ImageLoadAsync(string filePath, bool forceLoad, CancellationToken ct)
    {
        using var request = new XpcMessage("imageLoad");
        request.SetString("filePath", filePath);
        request.SetBool("forceLoad", forceLoad);
        using var reply = await images.SendAsync(request, _longRunningOptions, ct).ConfigureAwait(false);

        var loadedBytes = reply.GetData("imageDescriptions");
        var loaded = loadedBytes is null ? [] : XpcJson.Deserialize<List<ImageDescription>>(loadedBytes);
        var rejectedBytes = reply.GetData("rejectedMembers");
        var rejected = rejectedBytes is null ? [] : XpcJson.Deserialize<List<string>>(rejectedBytes);
        return (loaded, rejected);
    }

    /// <summary>
    /// <c>contentGet{digest}</c> → <c>contentPath</c> — a plain string value, not JSON <c>data</c>
    /// (§6's content-store table) — the local absolute path to the blob, or <c>null</c> on
    /// <c>notFound</c> (§6: "or an error with code notFound"). Blob bytes never traverse XPC (§6's
    /// closing note: "a digest is resolved to a local file path... then a normal file read"); the
    /// caller reads the file itself (<see cref="ContentStore.LocalBlobReader"/>).
    /// </summary>
    public virtual async Task<string?> ContentGetAsync(string digest, CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("contentGet");
            request.SetString("digest", digest);
            using var reply = await images.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
            return reply.GetString("contentPath");
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) == RuntimeErrorKind.NotFound)
        {
            return null;
        }
    }
}
