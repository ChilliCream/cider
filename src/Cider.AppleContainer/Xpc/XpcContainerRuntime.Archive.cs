using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <c>CopyFromContainerAsync</c>/<c>CopyToContainerAsync</c>/<c>ExportContainerAsync</c> over XPC
/// (task cider-ede.12). Routes: <c>containerCopyIn</c>/<c>containerCopyOut</c>/<c>containerExport</c>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.1 row, §3.7). All three still require a running
/// container — the same guard the CLI transport hits, enforced daemon-side
/// (<c>ContainersService.swift:766-786</c>: <c>invalidState</c> "container … is not running" unless
/// <c>state.snapshot.status == .running</c>) — except <c>containerExport</c>, whose daemon-side
/// <c>EXT4Reader.export(archive:)</c> reads the container's ext4 image directly and works on a
/// stopped container without booting a VM (docs/spikes/xpc/03-limitations-audit-1.3.md commit row;
/// §3.7: "since 1.2.x it snapshots the disk first if the container is running"). The generic
/// <c>invalidState</c>/"not running" → <see cref="RuntimeErrorReason.ContainerNotRunning"/> mapping
/// already lives in <see cref="XpcErrorMapper.ToRuntimeErrorReason"/> — nothing extra is needed here
/// for <see cref="ContainerManager.Archive"/>'s stopped-container cp emulation
/// (<c>ContainerManager.Archive.cs</c>'s <c>CopyOutOfStoppedContainerAsync</c>) to keep triggering on
/// this transport exactly as it does on the CLI one.
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>
    /// <c>ContainerClient.copyIn</c>/<c>copyOut</c> both use a 300 s client-side timeout, not the
    /// default 60 s (<c>ContainerClient.swift:326,345</c>; docs/spikes/xpc/02-apiserver-xpc-protocol.md
    /// §1.4's per-route table). Built directly here rather than added to
    /// <see cref="XpcCallOptions"/>'s shared presets, since no other route needs it.
    /// </summary>
    private static readonly XpcCallOptions CopyCallOptions = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>The Swift CLI's own default file mode for a copy-in (<c>ContainerClient.swift:317</c>:
    /// <c>mode: UInt32 = 0o644</c>) — cider has no source-file-mode input to preserve either, so this
    /// mirrors the CLI transport's own behavior (<c>container cp</c> never passes a mode of its own).</summary>
    private const ulong DefaultCopyFileMode = 0b110_100_100; // 0o644

    /// <summary>
    /// <c>containerCopyOut{id, sourcePath, destinationPath, createParents}</c> (§2.1). Unlike the CLI
    /// transport's own <c>container cp</c> invocation, <c>destinationPath</c> here is never a bare
    /// directory: the apiserver's runtime-side handler opens it directly
    /// (<c>RuntimeService.swift</c>'s <c>copyOut</c> → <c>Containerization</c>'s guest copy, which
    /// <c>open()</c>s the destination path itself) and fails with "Is a directory" if it already
    /// exists as one (verified live against the real apiserver, 1.3.0). <c>container cp</c>'s local
    /// CLI process resolves this client-side (<c>ContainerCopy.swift:66-93</c>: when the local
    /// destination already exists as a directory, it appends the source's own last path component
    /// before ever calling <c>copyOut</c>) — since going straight to <c>containerCopyOut</c> skips
    /// that CLI, the same resolution is done here: <paramref name="localDestinationDir"/> is created
    /// if missing, and the concrete file/directory path
    /// <c>localDestinationDir/&lt;last component of containerPath&gt;</c> is sent instead of the bare
    /// directory. <c>createParents</c> defaults to <c>true</c> on the Swift client
    /// (<c>ContainerClient.swift:337</c>) and is sent that way here too.
    /// </summary>
    public Task CopyFromContainerAsync(
        string runtimeId,
        string containerPath,
        string localDestinationDir,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);
        ArgumentException.ThrowIfNullOrEmpty(localDestinationDir);

        Directory.CreateDirectory(localDestinationDir);

        var lastComponent = containerPath.TrimEnd('/').Split('/').LastOrDefault(static c => c.Length > 0);
        if (string.IsNullOrEmpty(lastComponent))
        {
            // Same wording ContainerCopy.swift itself throws for this case, and the exact text
            // ContainerManager.Archive.cs's own stopped-container fallback already reuses.
            throw RuntimeException.InvalidArgument($"source path has no last component: {containerPath}");
        }

        var destination = Path.Combine(localDestinationDir, lastComponent);

        try
        {
            using var request = new XpcMessage("containerCopyOut");
            request.SetString("id", runtimeId);
            request.SetString("sourcePath", containerPath);
            request.SetString("destinationPath", destination);
            request.SetBool("createParents", true);
            using var reply = await _apiserver.SendAsync(request, CopyCallOptions, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerCopyOut", ex);
            await _cliFallback.CopyFromContainerAsync(runtimeId, containerPath, localDestinationDir, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ToCopyRuntimeException(ex, $"cp from {runtimeId}");
        }
    });

    /// <summary>
    /// <c>containerCopyIn{id, sourcePath, destinationPath, fileMode, createParents}</c> (§2.1).
    /// <paramref name="localSourcePath"/> is always a path the daemon itself staged on disk
    /// (<c>ContainerManager.Archive.cs</c>), never one a client supplies directly. <c>fileMode</c> and
    /// <c>createParents</c> both take the Swift CLI's own defaults (<c>ContainerClient.swift:317</c>:
    /// <c>mode: UInt32 = 0o644, createParents: Bool = true</c>) — <c>container cp</c> has no per-call
    /// override for either, so there is nothing more specific to send.
    /// </summary>
    public Task CopyToContainerAsync(
        string runtimeId,
        string localSourcePath,
        string containerPath,
        CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentException.ThrowIfNullOrEmpty(localSourcePath);
        ArgumentException.ThrowIfNullOrEmpty(containerPath);

        try
        {
            using var request = new XpcMessage("containerCopyIn");
            request.SetString("id", runtimeId);
            request.SetString("sourcePath", localSourcePath);
            request.SetString("destinationPath", containerPath);
            request.SetUInt64("fileMode", DefaultCopyFileMode);
            request.SetBool("createParents", true);
            using var reply = await _apiserver.SendAsync(request, CopyCallOptions, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerCopyIn", ex);
            await _cliFallback.CopyToContainerAsync(runtimeId, localSourcePath, containerPath, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ToCopyRuntimeException(ex, $"cp to {runtimeId}");
        }
    });

    /// <summary>
    /// <c>containerExport{id, archive:&lt;plain path&gt;}</c> (§2.1) — no client-side timeout, same as
    /// the Swift client's own <c>export</c> (<c>ContainerClient.swift:378-392</c>: plain
    /// <c>xpcClient.send(request)</c>, §1.4's "no timeout" row). <c>archive</c> is a plain filesystem
    /// path, never a <c>file://</c> URL (§2.1's request-row note); the daemon writes the tar there
    /// itself (<c>EXT4Reader.export(archive:)</c>, §3.7), so this exports to a temp file under
    /// <see cref="AppleContainerOptions.TmpDir"/> exactly as
    /// <c>AppleContainerRuntime.ExportContainerAsync</c> does for the CLI's <c>-o</c> flag, then
    /// streams it into <paramref name="tarOutput"/> and deletes it. Works on a stopped container
    /// without booting a VM — the daemon reads the ext4 image directly rather than going through the
    /// runtime channel <c>copyIn</c>/<c>copyOut</c> use, which is why this route (unlike those two)
    /// carries no running-container guard.
    /// </summary>
    public Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentNullException.ThrowIfNull(tarOutput);

        Directory.CreateDirectory(_options.TmpDir);
        var tmp = Path.Combine(_options.TmpDir, $"cider-export-{Guid.NewGuid():N}.tar");
        try
        {
            try
            {
                using var request = new XpcMessage("containerExport");
                request.SetString("id", runtimeId);
                request.SetString("archive", tmp);
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
            }
            catch (XpcException ex) when (IsUnavailable(ex))
            {
                WarnFallback("containerExport", ex);
                await _cliFallback.ExportContainerAsync(runtimeId, tarOutput, ct).ConfigureAwait(false);
                return;
            }
            catch (XpcException ex)
            {
                throw ex.ToRuntimeException($"export {runtimeId}");
            }

            await using var file = File.OpenRead(tmp);
            await file.CopyToAsync(tarOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(tmp);
        }
    });

    /// <summary>
    /// <c>containerCopyIn</c>/<c>containerCopyOut</c>'s "container is not running" failure does not
    /// arrive as the plain apiserver <c>invalidState</c> the generic
    /// <see cref="XpcErrorMapper.ToRuntimeErrorReason"/> table recognizes: the apiserver's own
    /// internal <c>RuntimeClient</c> re-wraps the runtime-side <c>invalidState</c> "cannot
    /// copyOut/copyIn: container is not running" (<c>RuntimeService.swift:736,780</c>) one level
    /// deeper as its own <c>internalError</c> "failed to copy from/into container &lt;id&gt; (cause:
    /// …)" (<c>RuntimeClient.swift:299-303,316-320</c>) before it ever reaches this route's reply —
    /// so the code alone (here, <c>internalError</c>, not <c>invalidState</c>) can't disambiguate it.
    /// Verified live against the real apiserver (1.3.0): <c>docker cp</c> from an exited container
    /// reproduces exactly this shape. Reads the message text instead, the same reason
    /// <c>XpcContainerRuntime.Resources.cs</c>'s <c>ToVolumeRuntimeException</c> does for
    /// <c>VolumeError</c> — this is still below the <c>IContainerRuntime</c> seam, so sniffing text
    /// here (never above it) does not violate the "nothing above the seam reads exception message
    /// text" rule.
    /// </summary>
    private static RuntimeException ToCopyRuntimeException(XpcException ex, string context) =>
        ex.Message.Contains("not running", StringComparison.OrdinalIgnoreCase)
            ? RuntimeException.ContainerNotRunning($"{context}: {ex.Message}")
            : ex.ToRuntimeException(context);

    /// <summary>Best-effort cleanup of the temp export file — a leftover is harmless, matching
    /// <c>AppleContainerRuntime</c>'s own <c>DeleteQuietly</c>.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are harmless.
        }
    }
}
