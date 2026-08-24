using System.Formats.Tar;
using System.Globalization;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Core.Time;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    /// <summary>Directory under the data dir holding archives staged for a not-yet-running container.</summary>
    private const string StagingDirectoryName = "archive-staging";

    /// <summary>Name of the extracted tree inside one staged batch.</summary>
    private const string StagedPayloadName = "payload";

    /// <summary>Name of the file inside one staged batch holding the destination path in the container.</summary>
    private const string StagedTargetName = "target";

    /// <summary>Name of the marker file that records a batch as mounted rather than copied.</summary>
    private const string StagedMountedName = "mounted";

    /// <summary>
    /// Most staged files that are turned into bind mounts at start; a bigger <c>docker cp</c> is
    /// copied in after the start instead of being spread over that many engine mounts.
    /// </summary>
    private const int MaxStagedMounts = 64;

    /// <summary>How long <see cref="FlushOneBatchAsync"/> keeps retrying a just-started container.</summary>
    public TimeSpan StagedArchiveFlushBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause between two attempts at copying a staged batch in.</summary>
    public TimeSpan StagedArchiveFlushBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary><c>HEAD /containers/{id}/archive</c>: stats one path inside the container.</summary>
    public async Task<ContainerPathStat> StatPathAsync(string idOrName, string path, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        RequirePath(path);

        // `docker cp` stats its destination before it sends anything, and a copy into the container
        // root (which is what Aspire/DCP does) stats "/". The stat is served by copying the path
        // out, and Apple `container cp <name>:/ …` refuses the root outright with "source path has
        // no last component: /", so the root is answered synthetically: it is a directory in every
        // container that exists, which is all the caller wanted to know.
        if (IsRoot(path))
        {
            return new ContainerPathStat
            {
                Name = "/",
                Size = 0,
                Mode = DirectoryMode,
                Mtime = DockerTime.Format(record.Created),
                LinkTarget = "",
            };
        }

        var staging = CreateStagingDirectory();
        try
        {
            await CopyOutAsync(record.RuntimeId, path, staging, ct);
            var copied = FindSingleEntry(staging, path);
            return Stat(copied, Path.GetFileName(path.TrimEnd('/')));
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary><c>GET /containers/{id}/archive</c>: tars one path out of the container.</summary>
    public async Task GetArchiveAsync(string idOrName, string path, Stream tarOut, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tarOut);

        var record = Resolve(idOrName);
        RequirePath(path);

        var staging = CreateStagingDirectory();
        try
        {
            await CopyOutAsync(record.RuntimeId, path, staging, ct);

            // Must be the async overload: tarOut is Kestrel's response body, and the daemon runs
            // with AllowSynchronousIO = false, so the synchronous CreateFromDirectory throws
            // "Synchronous operations are disallowed" after the headers are already on the wire —
            // the client then sees a 200 with an empty tar (`docker cp` silently copying nothing).
            await TarFile.CreateFromDirectoryAsync(staging, tarOut, includeBaseDirectory: false, ct);
            await tarOut.FlushAsync(ct);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary><c>PUT /containers/{id}/archive</c>: extracts a tar into a directory in the container.</summary>
    public async Task PutArchiveAsync(string idOrName, string path, Stream tarIn, bool noOverwriteDirNonDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tarIn);

        var record = Resolve(idOrName);
        RequirePath(path);

        var staging = CreateStagingDirectory();
        var staged = false;
        var extracted = false;
        try
        {
            await ExtractTarAsync(tarIn, staging, overwrite: !noOverwriteDirNonDir, ct);
            extracted = true;

            // Apple `container cp` refuses a container that is not running ("invalidState: container
            // … is not running"), while `docker cp` into a created or stopped container is ordinary
            // Docker — and it is exactly how Aspire/DCP injects its development certificates, right
            // between create and start. The extracted tree is parked under the data dir and replayed
            // by StartAsync the moment the container runs, before whoever started it is told so.
            if (!record.State.Running)
            {
                StageForReplay(record, staging, path);
                staged = true;
                return;
            }

            try
            {
                await CopyTreeIntoContainerAsync(record.RuntimeId, staging, path, ct);
            }
            catch (RuntimeException ex) when (IsNotRunning(ex))
            {
                // The record can be a moment behind the engine (the container exited while the tar
                // was on the wire); the staged replay covers that too.
                StageForReplay(record, staging, path);
                staged = true;
            }
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }
        catch (Exception ex) when (!extracted && ex is InvalidDataException or IOException)
        {
            throw DockerErrors.BadParameter($"invalid tar archive: {ex.Message}");
        }
        finally
        {
            if (!staged)
            {
                TryDelete(staging);
            }
        }
    }

    /// <summary><c>GET /containers/{id}/export</c>.</summary>
    public async Task ExportAsync(string idOrName, Stream tarOut, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tarOut);

        var record = Resolve(idOrName);
        try
        {
            await _runtime.ExportContainerAsync(record.RuntimeId, tarOut, ct);
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Copies everything <see cref="PutArchiveAsync"/> staged while the container was not running
    /// into it, oldest batch first, and drops each batch once it is in. Called by <c>StartAsync</c>
    /// with the container running and its gate held, so the client that started it does not see it
    /// running before its files are there.
    /// </summary>
    /// <remarks>
    /// A batch that cannot be copied stays on disk and is retried on the next start (and survives a
    /// daemon restart, like the container record itself); the replay stops at the first failure so
    /// later batches cannot overtake an earlier one.
    /// </remarks>
    private async Task FlushStagedArchivesAsync(ContainerRecord record, CancellationToken ct)
    {
        var root = StagingRootFor(record.Id);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var batch in StagedBatches(record.Id))
        {
            var payload = Path.Combine(batch, StagedPayloadName);
            var target = Path.Combine(batch, StagedTargetName);
            if (!Directory.Exists(payload) || !File.Exists(target))
            {
                // Interrupted mid-staging: nothing was ever acknowledged to a client, so it goes.
                TryDelete(batch);
                continue;
            }

            if (File.Exists(Path.Combine(batch, StagedMountedName)))
            {
                // Already in the container as bind mounts (see TryMountStagedArchivesAsync); the
                // files stay where they are, because the engine container mounts them from here.
                continue;
            }

            try
            {
                var containerPath = await File.ReadAllTextAsync(target, ct);
                await FlushOneBatchAsync(record, payload, containerPath, ct);
            }
            catch (Exception ex) when (ex is RuntimeException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    ex,
                    "the files staged for container {Container} could not be copied in after it started; they stay staged and are retried on the next start",
                    record.Id);
                return;
            }

            TryDelete(batch);
        }

        if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Turns everything staged for a container that has never run into bind mounts and re-creates the
    /// engine container with them, so the files are already in place when its entrypoint runs.
    /// </summary>
    /// <remarks>
    /// Copying them in after the start (<see cref="FlushStagedArchivesAsync"/>) is too late for an
    /// image that reads them immediately — the redis Aspire configures with TLS dies with "Failed to
    /// load certificate" before the copy can land — and Apple <c>container cp</c> cannot write into a
    /// container that is not running. Apple's <c>-v</c> does take a single file, creates the missing
    /// parent directories and leaves the rest of the directory it lands in alone (probed on 1.2.2),
    /// which is exactly what is needed. The mount sources are the staged files themselves, so the
    /// batch stays on disk until the container is removed.
    /// <para>
    /// Anything this cannot do — a container that has already run, a foreign container, more files
    /// than <see cref="MaxStagedMounts"/>, an engine that refuses the re-create — falls back to the
    /// copy after start, which is still right for every image that does not read the files at once.
    /// </para>
    /// <para>
    /// The batches it mounted are handed back rather than marked here: the marker is what stops the
    /// copy fallback from running for them, and it must only be written once the start has actually
    /// succeeded (see <see cref="MarkStagedBatchesMounted"/>). If the start fails the engine
    /// container is re-created again later from the record alone, which carries no staged mounts, so
    /// an unmarked batch is correctly mounted or copied in on the next start.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> TryMountStagedArchivesAsync(ContainerRecord record, CancellationToken ct)
    {
        if (record.State.StartedAt is not null || !record.Managed)
        {
            return [];
        }

        var batches = new List<string>();
        var mounts = new Dictionary<string, MountSpec>(StringComparer.Ordinal);
        foreach (var batch in StagedBatches(record.Id))
        {
            var payload = Path.Combine(batch, StagedPayloadName);
            var target = Path.Combine(batch, StagedTargetName);
            if (!Directory.Exists(payload) || !File.Exists(target) || File.Exists(Path.Combine(batch, StagedMountedName)))
            {
                continue;
            }

            var containerPath = (await File.ReadAllTextAsync(target, ct)).TrimEnd('/');
            foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                if (!File.Exists(file))
                {
                    // A symbolic link with no target on this side: nothing to mount from.
                    continue;
                }

                var relative = Path.GetRelativePath(payload, file).Replace(Path.DirectorySeparatorChar, '/');

                // A later batch copied over an earlier one, so its mount wins.
                mounts[containerPath + "/" + relative] = new MountSpec
                {
                    Kind = MountKind.Bind,
                    Source = file,
                    Target = containerPath + "/" + relative,
                };
            }

            // A directory with no file anywhere beneath it has nothing in the loop above to carry it
            // in, so `docker cp` of a tree containing empty directories used to lose exactly those
            // directories on this path while every other entry arrived. Binding the directory itself
            // restores it (and anything empty below it). Directories that do contain files are
            // already implied by their file mounts, and binding those would shadow the files.
            foreach (var directory in Directory.EnumerateDirectories(payload, "*", SearchOption.AllDirectories))
            {
                if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
                {
                    continue;
                }

                var relative = Path.GetRelativePath(payload, directory).Replace(Path.DirectorySeparatorChar, '/');
                var directoryTarget = containerPath + "/" + relative;

                // Only the topmost file-less directory is mounted; its own empty children come along
                // inside it.
                if (mounts.Keys.Any(existing => directoryTarget.StartsWith(existing + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                mounts[directoryTarget] = new MountSpec
                {
                    Kind = MountKind.Bind,
                    Source = directory,
                    Target = directoryTarget,
                };
            }

            batches.Add(batch);
        }

        if (batches.Count == 0 || mounts.Count == 0 || mounts.Count > MaxStagedMounts)
        {
            return [];
        }

        var spec = await BuildSpecForNetworksAsync(record, [.. record.Networks.Keys], ct);
        spec = spec with { Mounts = [.. spec.Mounts, .. mounts.Values] };

        try
        {
            await _runtime.RemoveContainerAsync(record.RuntimeId, force: false, ct);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
        {
            // Gone on the engine side already; the create below is the repair that needs.
        }
        catch (RuntimeException ex)
        {
            _logger.LogWarning(
                ex,
                "container {Container} could not be re-created with its staged files mounted; they are copied in after the start instead",
                record.Id);
            return [];
        }

        try
        {
            await _runtime.CreateContainerAsync(spec, CancellationToken.None);
        }
        catch (RuntimeException ex)
        {
            _logger.LogWarning(
                ex,
                "container {Container} could not be re-created with its staged files mounted; they are copied in after the start instead",
                record.Id);
            await TryRestoreAsync(record, CancellationToken.None);
            return [];
        }

        _logger.LogDebug(
            "container {Container} was re-created with {Count} staged file(s) mounted in place",
            record.Id,
            mounts.Count);
        return batches;
    }

    /// <summary>
    /// Marks the batches <see cref="TryMountStagedArchivesAsync"/> mounted, once the start they were
    /// mounted for has succeeded.
    /// </summary>
    /// <remarks>
    /// The marker permanently disables the copy fallback for a batch, so it is only written when the
    /// mount is known to be on a container that really started: after a successful start no re-create
    /// can drop it again (the never-started guards block every re-create path), while a failed start
    /// leaves the batch unmarked so the next start mounts or copies it in.
    /// </remarks>
    private async Task MarkStagedBatchesMounted(IReadOnlyList<string> batches, string runtimeId)
    {
        foreach (var batch in batches)
        {
            await File.WriteAllTextAsync(Path.Combine(batch, StagedMountedName), runtimeId, CancellationToken.None);
        }
    }

    /// <summary>One container's staged batches, oldest first (the names carry a UTC timestamp).</summary>
    private List<string> StagedBatches(string containerId)
    {
        var root = StagingRootFor(containerId);
        return Directory.Exists(root)
            ? [.. Directory.GetDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// Copies one staged batch in, retrying while Apple still answers "is not running". Apple keeps
    /// saying that for a moment after <c>container start</c> has already handed over the init
    /// process (the same race <c>container exec</c> has, see <c>AppleContainerRuntime</c>), and the
    /// files have to be in before <c>StartAsync</c> returns — an image that reads them at startup,
    /// like the redis Aspire configures with TLS, otherwise dies before the retry would help.
    /// </summary>
    private async Task FlushOneBatchAsync(ContainerRecord record, string payload, string containerPath, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StagedArchiveFlushBudget;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await CopyTreeIntoContainerAsync(record.RuntimeId, payload, containerPath, ct);
                return;
            }
            catch (RuntimeException ex) when (IsNotRunning(ex) && DateTimeOffset.UtcNow < deadline)
            {
                _logger.LogDebug(
                    "the archive staged for container {Container} raced its start (attempt {Attempt}): {Message}",
                    record.Id,
                    attempt,
                    ex.Message);
                await Task.Delay(StagedArchiveFlushBackoff, ct);
            }
        }
    }

    /// <summary>Drops everything staged for a container; called when the container is removed.</summary>
    private void DropStagedArchives(string containerId) => TryDelete(StagingRootFor(containerId));

    /// <summary>Moves an extracted tree into the container's staging area, together with its destination path.</summary>
    private void StageForReplay(ContainerRecord record, string extracted, string containerPath)
    {
        var batch = Path.Combine(
            StagingRootFor(record.Id),
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(batch);

        // The target file is written first: the payload directory appearing is what marks a batch
        // complete, so an interrupted staging can never be replayed against an unknown destination.
        File.WriteAllText(Path.Combine(batch, StagedTargetName), containerPath);
        Directory.Move(extracted, Path.Combine(batch, StagedPayloadName));

        _logger.LogDebug(
            "container {Container} is not running; the archive for {Path} was staged and will be copied in when it starts",
            record.Id,
            containerPath);
    }

    private string StagingRootFor(string containerId) =>
        Path.Combine(_options.DataDir, StagingDirectoryName, containerId);

    private async Task CopyTreeIntoContainerAsync(string runtimeId, string tree, string containerPath, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(tree))
        {
            await _runtime.CopyToContainerAsync(runtimeId, entry, containerPath, ct);
        }
    }

    private async Task CopyOutAsync(string runtimeId, string path, string destination, CancellationToken ct)
    {
        try
        {
            await _runtime.CopyFromContainerAsync(runtimeId, path, destination, ct);
        }
        catch (RuntimeException ex) when (IsNotRunning(ex))
        {
            // `docker cp exited-container:/artefact .` is ordinary Docker — it is how people get
            // build output and post-mortem files out — while Apple `container cp` refuses a
            // container that is not running outright. The container's own rootfs export still
            // answers the question, so the read is served from there.
            await CopyOutOfStoppedContainerAsync(runtimeId, path, destination, ct);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
        {
            throw new DockerApiException(
                System.Net.HttpStatusCode.NotFound,
                $"Could not find the file {path} in container {runtimeId}",
                ex);
        }
        catch (RuntimeException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Serves a read out of a container that is not running from its rootfs export, laying the
    /// requested path into <paramref name="destination"/> exactly as <c>container cp</c> would have.
    /// </summary>
    /// <remarks>
    /// Apple <c>container cp</c> refuses a container that is not running, but <c>container export</c>
    /// does not: it tars the container's own rootfs — including everything the container wrote while
    /// it ran — for a <c>stopped</c> container as happily as for a running one (probed on 1.2.2:
    /// a container that had exited after writing <c>/out.txt</c> exported in 0.1 s with the file in
    /// it). The stopped container is only read from, so its <c>Status</c> and <c>ExitCode</c> cannot
    /// change; nothing is started, and nothing writes into Apple's on-disk layout.
    /// <para>
    /// The cost is O(rootfs) rather than O(path): the whole export is written to the tmp dir before
    /// one path is selected out of it. That is only ever paid on this fallback — a running container
    /// still copies directly — and it is the price of not starting anything.
    /// </para>
    /// <para>
    /// A path that reaches its target through a symbolic link (<c>/var/log/…</c> where <c>/var</c> is
    /// a link) is not resolved: the export carries the link entry, not the directory it names, so the
    /// lookup misses and the caller gets the same 404 as for a path that is not there.
    /// </para>
    /// </remarks>
    private async Task CopyOutOfStoppedContainerAsync(string runtimeId, string path, string destination, CancellationToken ct)
    {
        string wanted;
        try
        {
            wanted = NormalizeEntryName(path).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (InvalidDataException ex)
        {
            throw DockerErrors.BadParameter(ex.Message);
        }

        if (wanted.Length == 0)
        {
            // The container root, which `container cp` refuses for its own reasons; nothing about a
            // stopped container makes that answer different (StatPathAsync answers "/" itself).
            throw Translate(RuntimeException.InvalidArgument($"source path has no last component: {path}"));
        }

        Directory.CreateDirectory(_options.TmpDir);
        var export = Path.Combine(_options.TmpDir, "cp-out-" + Guid.NewGuid().ToString("n") + ".tar");
        try
        {
            try
            {
                await using var file = new FileStream(export, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await _runtime.ExportContainerAsync(runtimeId, file, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }

            await using var source = new FileStream(export, FileMode.Open, FileAccess.Read, FileShare.None);
            if (!await ExtractPathFromExportAsync(source, wanted, destination, ct))
            {
                throw new DockerApiException(
                    System.Net.HttpStatusCode.NotFound,
                    $"Could not find the file {path} in container {runtimeId}");
            }
        }
        finally
        {
            TryDeleteFile(export);
        }
    }

    /// <summary>
    /// Copies the entry named <paramref name="wanted"/>, or the whole subtree under it, out of a
    /// rootfs export and into <paramref name="destination"/> under its own last component — the shape
    /// <see cref="FindSingleEntry"/> and <see cref="GetArchiveAsync"/> expect from
    /// <c>container cp</c>. <c>false</c> when the export carries no such path.
    /// </summary>
    private static async Task<bool> ExtractPathFromExportAsync(Stream export, string wanted, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        var name = wanted[(wanted.LastIndexOf('/') + 1)..];
        var prefix = wanted + "/";
        var found = false;

        await using var reader = new TarReader(export, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (MapExportEntry(entry.Name, wanted, prefix, name) is not { } relative)
            {
                continue;
            }

            found = true;
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory or TarEntryType.DirectoryList:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.SymbolicLink when !string.IsNullOrEmpty(entry.LinkName):
                    if (File.Exists(target) || new FileInfo(target).LinkTarget is not null)
                    {
                        File.Delete(target);
                    }

                    File.CreateSymbolicLink(target, entry.LinkName);
                    continue; // Mode and mtime belong to the link's target, not the link.

                case TarEntryType.HardLink:
                {
                    // The entry it points at is elsewhere in the same export, so it is only in hand
                    // here when it sits inside the subtree that was asked for.
                    var mapped = MapExportEntry(entry.LinkName, wanted, prefix, name);
                    if (mapped is null)
                    {
                        continue;
                    }

                    var linked = Path.Combine(root, mapped.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(linked))
                    {
                        continue;
                    }

                    File.Copy(linked, target, overwrite: true);
                    break;
                }

                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    await entry.ExtractToFileAsync(target, overwrite: true, ct);
                    break;

                default:
                    // A device node, socket or fifo: nothing a host directory can hold.
                    continue;
            }

            ApplyEntryMetadata(entry, target);
        }

        return found;
    }

    /// <summary>
    /// Where one export entry lands under the destination: the requested path itself keeps
    /// its last component, everything below it keeps its position under that component, and anything
    /// outside the requested path is <c>null</c> (not wanted).
    /// </summary>
    private static string? MapExportEntry(string entryName, string wanted, string prefix, string name)
    {
        string key;
        try
        {
            key = NormalizeEntryName(entryName).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (InvalidDataException)
        {
            // A '..' in an entry name: not something an export should carry, and never wanted.
            return null;
        }

        if (string.Equals(key, wanted, StringComparison.Ordinal))
        {
            return name;
        }

        return key.StartsWith(prefix, StringComparison.Ordinal) ? name + "/" + key[prefix.Length..] : null;
    }

    /// <summary>
    /// Puts one export entry's mode and modification time on the file or directory it was extracted
    /// to, so the tar the client is handed back describes what was in the container.
    /// </summary>
    private static void ApplyEntryMetadata(TarEntry entry, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, entry.Mode);
            }

            if (entry.ModificationTime > DateTimeOffset.UnixEpoch)
            {
                File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Cosmetic on the way out; the bytes are what the caller asked for.
        }
    }

    /// <summary>
    /// Extracts a <c>docker cp</c> tar, normalising every entry name on the way in.
    /// <see cref="TarFile.ExtractToDirectoryAsync(Stream, string, bool, CancellationToken)"/> cannot
    /// be used: it refuses absolute entry names outright ("would have resulted in a file outside…"),
    /// and clients do send them — Aspire/DCP's certificate tar names
    /// <c>/usr/lib/ssl/aspire/private/&lt;thumbprint&gt;.crt</c>. Real dockerd strips the leading
    /// slash and so does this; <c>..</c> is still refused.
    /// <para>
    /// The archive goes through <see cref="CopyRepairingModesAsync"/> first, because
    /// <see cref="TarReader"/> cannot even parse the header of a Go-written directory entry — see
    /// there.
    /// </para>
    /// </summary>
    private async Task ExtractTarAsync(Stream tarIn, string destination, bool overwrite, CancellationToken ct)
    {
        var repaired = Path.Combine(_options.TmpDir, "cp-" + Guid.NewGuid().ToString("n") + ".tar");
        try
        {
            await using (var buffer = new FileStream(repaired, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyRepairingModesAsync(tarIn, buffer, ct);
            }

            await using var source = new FileStream(repaired, FileMode.Open, FileAccess.Read, FileShare.None);
            await ExtractRepairedTarAsync(source, destination, overwrite, ct);
        }
        finally
        {
            TryDeleteFile(repaired);
        }
    }

    private static async Task ExtractRepairedTarAsync(Stream tarIn, string destination, bool overwrite, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        await using var reader = new TarReader(tarIn, leaveOpen: true);

        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            // A pax global header describes the archive, not a file, and cannot be extracted.
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            var relative = NormalizeEntryName(entry.Name);
            if (relative.Length == 0)
            {
                // "/" or "./": the archive's own root, which the destination directory already is.
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"the tar entry '{entry.Name}' would have resulted in a file outside {root}");
            }

            if (entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ExtractLink(entry, root, target, overwrite);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await entry.ExtractToFileAsync(target, overwrite, ct);
        }
    }

    /// <summary>
    /// Creates one link entry.
    /// <see cref="TarEntry.ExtractToFile(string, bool)"/> refuses these outright ("Entry type
    /// 'SymbolicLink' not supported for extraction"), while dockerd creates them — and Aspire/DCP's
    /// certificate tar carries one (the <c>c_rehash</c> style <c>&lt;hash&gt;.0</c> link next to the
    /// PEM it names). A symbolic link is written verbatim, exactly as it arrived, because its target
    /// is resolved inside the container and not here; a hard link becomes a copy of the file it
    /// points at, which is as close as .NET can get to one.
    /// </summary>
    private static void ExtractLink(TarEntry entry, string root, string target, bool overwrite)
    {
        if (string.IsNullOrEmpty(entry.LinkName))
        {
            throw new InvalidDataException($"the tar entry '{entry.Name}' is a link with no target");
        }

        var existing = new FileInfo(target);
        if (existing.Exists || existing.LinkTarget is not null)
        {
            if (!overwrite)
            {
                throw new IOException($"the tar entry '{entry.Name}' already exists in the destination");
            }

            File.Delete(target);
        }

        if (entry.EntryType == TarEntryType.SymbolicLink)
        {
            File.CreateSymbolicLink(target, entry.LinkName);
            return;
        }

        // A hard link's target is another entry of this same archive, by path from its root.
        var source = Path.GetFullPath(Path.Combine(root, NormalizeEntryName(entry.LinkName)));
        if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(source))
        {
            throw new InvalidDataException(
                $"the tar entry '{entry.Name}' hard-links to '{entry.LinkName}', which the archive does not carry");
        }

        File.Copy(source, target, overwrite: true);
    }

    // ---- tar header repair ------------------------------------------------------------------
    // Field offsets and lengths of the POSIX tar header block (ustar and GNU alike).
    private const int TarBlockLength = 512;
    private const int TarModeOffset = 100;
    private const int TarModeLength = 8;
    private const int TarSizeOffset = 124;
    private const int TarSizeLength = 12;
    private const int TarChecksumOffset = 148;
    private const int TarChecksumLength = 8;
    private const int TarTypeFlagOffset = 156;
    private const long TarPermissionMask = 0xFFF; // 07777: everything below the file-type bits.

    /// <summary>
    /// Copies an archive across, masking the file-type bits out of any header whose mode field
    /// carries them, and fixing that header's checksum.
    /// </summary>
    /// <remarks>
    /// Go's <c>archive/tar</c> writers hand <c>os.FileMode</c> straight to the header, so a
    /// directory entry's mode is <c>os.ModeDir|0755</c> = 0x800001ED, written base-256 because it no
    /// longer fits in the octal field. Go's own reader masks the type bits off again, but
    /// <see cref="TarReader"/> parses the mode as an <see cref="int"/> and dies on the whole archive
    /// with <see cref="OverflowException"/>. Aspire/DCP writes exactly such an entry
    /// (<c>/usr/lib/ssl/aspire/certs</c>) in the certificate tar it copies into every container, so
    /// without this the copy 500s and the resource never starts.
    /// <para>
    /// Anything that does not parse as a header is copied through untouched: this pass only ever
    /// repairs what it fully understands, and leaves judging a malformed archive to the tar reader.
    /// </para>
    /// </remarks>
    private static async Task CopyRepairingModesAsync(Stream tarIn, Stream output, CancellationToken ct)
    {
        var block = new byte[TarBlockLength];
        long? paxSize = null;

        while (true)
        {
            var read = await ReadBlockAsync(tarIn, block, ct);
            if (read == 0)
            {
                return;
            }

            if (read < TarBlockLength || IsAllZero(block) || !TryRepairHeaderBlock(block, out var size, out var typeFlag))
            {
                // End-of-archive padding, or something this pass does not understand: hand the rest
                // over verbatim.
                await output.WriteAsync(block.AsMemory(0, read), ct);
                await tarIn.CopyToAsync(output, ct);
                return;
            }

            if (paxSize is { } overridden)
            {
                size = overridden;
                paxSize = null;
            }

            await output.WriteAsync(block, ct);
            var data = await CopyBlocksAsync(tarIn, output, RoundUpToBlock(size), ct);

            // A pax extended header carries the real size of the entry that follows when it does not
            // fit the octal field, which is what the walk above has to skip by.
            if (typeFlag is (byte)'x' or (byte)'g')
            {
                paxSize = ReadPaxSize(data);
            }
        }
    }

    /// <summary>Masks the file-type bits out of one header's mode; <c>false</c> when it is not a header.</summary>
    private static bool TryRepairHeaderBlock(byte[] block, out long size, out byte typeFlag)
    {
        size = 0;
        typeFlag = block[TarTypeFlagOffset];

        if (!HasValidChecksum(block) ||
            !TryParseTarNumber(block, TarSizeOffset, TarSizeLength, out size) ||
            size < 0 ||
            !TryParseTarNumber(block, TarModeOffset, TarModeLength, out var mode))
        {
            return false;
        }

        if ((mode & ~TarPermissionMask) != 0)
        {
            WriteOctalField(block, TarModeOffset, TarModeLength, mode & TarPermissionMask);
            WriteChecksum(block);
        }

        return true;
    }

    /// <summary>Parses one tar numeric field: NUL/space-terminated octal, or base-256 for large values.</summary>
    private static bool TryParseTarNumber(byte[] block, int offset, int length, out long value)
    {
        value = 0;
        var field = block.AsSpan(offset, length);

        if ((field[0] & 0x80) != 0)
        {
            // Base-256: the low seven bits of the first byte plus the rest, big endian. Negative
            // values (leading 0xFF) are not something a mode or a size can be.
            if ((field[0] & 0x7F) != 0)
            {
                return false;
            }

            for (var i = 1; i < field.Length; i++)
            {
                if (value > (long.MaxValue >> 8))
                {
                    return false;
                }

                value = (value << 8) | field[i];
            }

            return true;
        }

        var seenDigit = false;
        foreach (var raw in field)
        {
            if (raw is 0 or (byte)' ')
            {
                break;
            }

            if (raw is < (byte)'0' or > (byte)'7')
            {
                return false;
            }

            seenDigit = true;
            value = (value << 3) | (long)(raw - (byte)'0');
        }

        return seenDigit;
    }

    /// <summary>
    /// <c>true</c> when the block's checksum matches, counting the checksum field as spaces. Both the
    /// unsigned and the (historical) signed sum are accepted, exactly as tar readers do.
    /// </summary>
    private static bool HasValidChecksum(byte[] block)
    {
        if (!TryParseTarNumber(block, TarChecksumOffset, TarChecksumLength, out var stored))
        {
            return false;
        }

        var (unsigned, signed) = Checksums(block);
        return stored == unsigned || stored == signed;
    }

    private static (long Unsigned, long Signed) Checksums(byte[] block)
    {
        long unsigned = 0;
        long signed = 0;
        for (var i = 0; i < TarBlockLength; i++)
        {
            var value = i >= TarChecksumOffset && i < TarChecksumOffset + TarChecksumLength ? (byte)' ' : block[i];
            unsigned += value;
            signed += (sbyte)value;
        }

        return (unsigned, signed);
    }

    private static void WriteChecksum(byte[] block)
    {
        var (unsigned, _) = Checksums(block);
        WriteOctalField(block, TarChecksumOffset, TarChecksumLength - 1, unsigned);
        block[TarChecksumOffset + TarChecksumLength - 1] = (byte)' ';
    }

    /// <summary>Writes a zero-padded octal field of <paramref name="length"/> bytes, NUL terminated.</summary>
    private static void WriteOctalField(byte[] block, int offset, int length, long value)
    {
        var digits = Convert.ToString(value, 8);
        var padded = digits.PadLeft(length - 1, '0');
        for (var i = 0; i < length - 1; i++)
        {
            block[offset + i] = (byte)padded[i];
        }

        block[offset + length - 1] = 0;
    }

    private static long RoundUpToBlock(long size) => (size + TarBlockLength - 1) / TarBlockLength * TarBlockLength;

    private static bool IsAllZero(byte[] block)
    {
        foreach (var value in block)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one whole block, or fewer bytes at the end of the stream.</summary>
    private static async Task<int> ReadBlockAsync(Stream source, byte[] block, CancellationToken ct)
    {
        var read = 0;
        while (read < block.Length)
        {
            var chunk = await source.ReadAsync(block.AsMemory(read), ct);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return read;
    }

    /// <summary>Copies <paramref name="length"/> bytes across and returns them (they are one entry's data).</summary>
    private static async Task<byte[]> CopyBlocksAsync(Stream source, Stream output, long length, CancellationToken ct)
    {
        // Only pax records are ever looked at again, and those are a few hundred bytes.
        var captured = length <= 64 * 1024 ? new MemoryStream() : null;
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(remaining, buffer.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), ct);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            captured?.Write(buffer, 0, read);
            remaining -= read;
        }

        return captured?.ToArray() ?? [];
    }

    /// <summary>The <c>size=</c> record of a pax extended header, when it carries one.</summary>
    private static long? ReadPaxSize(byte[] data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data);
        foreach (var record in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = record.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                continue;
            }

            var pair = record[(space + 1)..];
            if (pair.StartsWith("size=", StringComparison.Ordinal) &&
                long.TryParse(pair["size=".Length..], CultureInfo.InvariantCulture, out var size) &&
                size >= 0)
            {
                return size;
            }
        }

        return null;
    }

    /// <summary>
    /// Turns a tar entry name into a path relative to the extraction root: leading <c>/</c> and
    /// <c>./</c> segments go, <c>..</c> is refused.
    /// </summary>
    internal static string NormalizeEntryName(string name)
    {
        var segments = new List<string>();
        foreach (var segment in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"the tar entry '{name}' would have resulted in a file outside the destination");
            }

            segments.Add(segment);
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private string CreateStagingDirectory()
    {
        var directory = Path.Combine(_options.TmpDir, "cp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw DockerErrors.BadParameter("path cannot be empty");
        }
    }

    /// <summary><c>true</c> for the container root, in any of the spellings a client may send.</summary>
    private static bool IsRoot(string path) => path.Trim().Trim('/').Length == 0;

    /// <summary>
    /// <c>true</c> when the runtime turned an operation down because the container is not running.
    /// Recognised by <see cref="RuntimeErrorReason"/>, never by the message text: the runtime that
    /// read the backend's wording is the only layer entitled to interpret it, and a future backend
    /// with different phrasing must not silently lose the staging fallback below.
    /// </summary>
    private static bool IsNotRunning(RuntimeException ex) => ex.IsContainerNotRunning;

    private static string FindSingleEntry(string staging, string path)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1)
        {
            return entries[0];
        }

        var candidate = Path.Combine(staging, Path.GetFileName(path.TrimEnd('/')));
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            return candidate;
        }

        throw new DockerApiException(
            System.Net.HttpStatusCode.NotFound,
            $"Could not find the file {path} in container");
    }

    /// <summary>Go's <c>os.ModeDir|0755</c>, as <c>docker cp</c> expects it in the path stat.</summary>
    private const uint DirectoryMode = 0x800001EDu;

    private static ContainerPathStat Stat(string localPath, string name)
    {
        var info = new FileInfo(localPath);
        var isDirectory = Directory.Exists(localPath);
        var mode = isDirectory ? DirectoryMode : 0x1A4u; // Go's os.ModeDir|0755 and 0644.
        var mtime = isDirectory ? Directory.GetLastWriteTimeUtc(localPath) : info.LastWriteTimeUtc;

        return new ContainerPathStat
        {
            Name = name.Length > 0 ? name : Path.GetFileName(localPath),
            Size = isDirectory ? 0 : info.Length,
            Mode = mode,
            Mtime = DockerTime.Format(new DateTimeOffset(mtime, TimeSpan.Zero)),
            LinkTarget = "",
        };
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Staging leftovers are harmless; the tmp dir is cleaned on the next daemon start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
