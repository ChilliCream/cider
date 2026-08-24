using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cider.Daemon.Install;

/// <summary>
/// What <c>/var/run/docker.sock</c> looked like immediately before cider repointed it, so that
/// <c>cider uninstall</c> can put the previous engine's wiring back instead of deleting it.
/// </summary>
/// <param name="Path">The system socket path that was replaced (normally <see cref="SystemSocketLink.DockerSock"/>).</param>
/// <param name="Existed">False when nothing was at <paramref name="Path"/> before.</param>
/// <param name="WasSymlink">True when <paramref name="Path"/> was a symlink (the only restorable case).</param>
/// <param name="PreviousTarget">Absolute path the symlink pointed at, or null when it was not a symlink.</param>
/// <param name="LinkedTarget">The cider socket the path was repointed at.</param>
/// <param name="SavedAt">When the record was written.</param>
public sealed record SystemSocketBackup(
    string Path,
    bool Existed,
    bool WasSymlink,
    string? PreviousTarget,
    string LinkedTarget,
    DateTimeOffset SavedAt);

/// <summary>
/// Optionally symlinks the system-wide <c>/var/run/docker.sock</c> to the cider socket so that
/// tools which hardcode the Docker default path (instead of honoring DOCKER_HOST) keep working.
/// The previous target is saved to <c>&lt;DataDir&gt;/system-socket.backup.json</c> first and restored
/// on uninstall — cider never silently destroys another engine's wiring.
/// </summary>
public static partial class SystemSocketLink
{
    public const string DockerSock = "/var/run/docker.sock";

    /// <summary>Name of the backup record inside the data directory.</summary>
    public const string BackupFileName = "system-socket.backup.json";

    // The backup record's contract, source-generated so `cider install` carries no reflection-based
    // serializer into a Native AOT build. Settings as before.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true)]
    [JsonSerializable(typeof(SystemSocketBackup))]
    private sealed partial class BackupJsonContext : JsonSerializerContext;

    /// <summary>&lt;dataDir&gt;/system-socket.backup.json</summary>
    public static string BackupPath(string dataDir) => System.IO.Path.Combine(dataDir, BackupFileName);

    /// <summary>
    /// Human-readable instructions for manually creating the symlink and — crucially — for putting
    /// the previous target back afterwards.
    /// </summary>
    public static string Instructions(string socketPath, string dockerSockPath = DockerSock) => string.Join(
        '\n',
        $"{dockerSockPath} is normally owned by root, so cider cannot replace it without elevated privileges.",
        "First note what it points at today, so you can put it back later:",
        "",
        $"    readlink {dockerSockPath}",
        "",
        "To let plain `docker` commands (without setting DOCKER_HOST) reach cider, run:",
        "",
        $"    sudo ln -sf {socketPath} {dockerSockPath}",
        "",
        "To undo this later, restore the target you noted above:",
        "",
        $"    sudo ln -sf <the path readlink printed> {dockerSockPath}",
        "",
        $"`cider uninstall` does that for you from <data-dir>/{BackupFileName}, which `cider install",
        "--system-socket` writes before it touches anything. If `readlink` printed nothing there was no previous",
        "link to restore — remove cider's link instead (`sudo rm -f` on that path).");

    /// <summary>
    /// Attempts to (re)point <paramref name="dockerSockPath"/> at <paramref name="socketPath"/> using
    /// non-interactive sudo, after recording the current target under <paramref name="dataDir"/>.
    /// Never prompts for a password; if one is required this fails fast and returns
    /// <see cref="Instructions"/> for the caller to show the user. Refuses to clobber a real socket
    /// file (which could not be restored) unless <paramref name="allowReplaceExisting"/> is set.
    /// </summary>
    public static Task<InstallResult> TryLinkAsync(
        string socketPath,
        TextWriter log,
        CancellationToken ct,
        string? dataDir = null,
        string dockerSockPath = DockerSock,
        bool allowReplaceExisting = false) =>
        TryLinkCoreAsync(socketPath, log, dataDir, dockerSockPath, allowReplaceExisting, SudoAsync, ct);

    /// <summary>
    /// Restores the system socket recorded in <c>&lt;dataDir&gt;/system-socket.backup.json</c>: puts the
    /// previous symlink target back (or removes the link when nothing was there before), but only while
    /// the path still points at the cider socket we linked it to.
    /// </summary>
    public static Task<InstallResult> TryRestoreAsync(
        string dataDir,
        TextWriter log,
        CancellationToken ct,
        string dockerSockPath = DockerSock) =>
        TryRestoreCoreAsync(dataDir, log, dockerSockPath, SudoAsync, ct);

    internal static async Task<InstallResult> TryLinkCoreAsync(
        string socketPath,
        TextWriter log,
        string? dataDir,
        string dockerSockPath,
        bool allowReplaceExisting,
        PrivilegedCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var steps = new List<string>();
        var (state, currentTarget) = Inspect(dockerSockPath, socketPath);

        if (state == SystemSocketState.LinkedToUs)
        {
            // Already ours: leave any earlier backup exactly as it is, it still holds the real previous target.
            const string message = "System socket already linked.";
            steps.Add(message);
            log.WriteLine(message);
            return new InstallResult(true, message, steps);
        }

        if (state == SystemSocketState.NotASymlink && !allowReplaceExisting)
        {
            // Deliberately *not* Instructions(): those hand out the manual `ln -sf` recipe we just
            // refused (unrecoverable over a real socket file) and tell the user that if `readlink`
            // printed nothing there was nothing to restore — which is exactly backwards here, since
            // `readlink` prints nothing precisely because a real socket, not a symlink, is at risk.
            var refusal = string.Join(
                '\n',
                $"Refusing to replace {dockerSockPath}: it is a real file/socket, not a symlink, so cider",
                "could not restore it on uninstall — replacing it would destroy it for good.",
                "",
                "Stop the engine that owns it (or move it aside) and re-run, or pass --force-system-socket",
                "to replace it anyway — that is the only supported override, and even then uninstall can",
                "only remove cider's link and warn you, never bring the original socket back.");
            steps.Add(refusal);
            log.WriteLine(refusal);
            return new InstallResult(false, refusal, steps);
        }

        var backup = new SystemSocketBackup(
            System.IO.Path.GetFullPath(dockerSockPath),
            Existed: state != SystemSocketState.Absent,
            WasSymlink: state == SystemSocketState.LinkedElsewhere,
            PreviousTarget: currentTarget,
            LinkedTarget: System.IO.Path.GetFullPath(socketPath),
            SavedAt: DateTimeOffset.UtcNow);

        string? backupPath = null;
        if (!string.IsNullOrEmpty(dataDir))
        {
            backupPath = BackupPath(dataDir);
            var described = Describe(state, dockerSockPath, currentTarget);

            // Second `install --system-socket` from the same data dir with a different --socket: the path
            // points at an cider socket *we* linked, not at the user's engine. Overwriting the record
            // with that would make uninstall "restore" a socket nobody serves and lose the real previous
            // target forever, so keep the original record and only update what we now link to.
            var (existing, _) = await ReadBackupAsync(backupPath, ct).ConfigureAwait(false);
            if (SupersedesOurOwnLink(existing, backup.Path, currentTarget))
            {
                backup = existing! with { LinkedTarget = backup.LinkedTarget };
                described = $"{dockerSockPath} already pointed at an earlier cider socket ({currentTarget}); " +
                    $"keeping the original target on record ({Describe(existing.Existed, existing.WasSymlink, existing.PreviousTarget)})";
            }

            try
            {
                Directory.CreateDirectory(dataDir);
                await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(backup, BackupJsonContext.Default.SystemSocketBackup), ct).ConfigureAwait(false);
                steps.Add(described + $" — saved to {backupPath}");
                log.WriteLine(steps[^1]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                var failure = $"Could not record the current {dockerSockPath} target in {backupPath} ({ex.Message}); not touching the system socket.";
                steps.Add(failure);
                return new InstallResult(false, failure, steps);
            }
        }

        var link = await runner(["ln", "-sf", socketPath, dockerSockPath], ct).ConfigureAwait(false);
        steps.Add($"{link.Command} (exit {link.ExitCode})");
        log.WriteLine(steps[^1]);

        if (link.Succeeded && Inspect(dockerSockPath, socketPath).State == SystemSocketState.LinkedToUs)
        {
            var linkedMessage = $"Linked {dockerSockPath} -> {socketPath}." +
                (backupPath is null ? "" : $" Previous state saved to {backupPath}; `cider uninstall` restores it.");
            steps.Add(linkedMessage);
            return new InstallResult(true, linkedMessage, steps);
        }

        // Nothing was changed, so the record we just wrote would only mislead a later uninstall.
        DeleteBackup(backupPath, steps);
        return new InstallResult(false, Instructions(socketPath, dockerSockPath), steps);
    }

    internal static async Task<InstallResult> TryRestoreCoreAsync(
        string dataDir,
        TextWriter log,
        string dockerSockPath,
        PrivilegedCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var steps = new List<string>();
        var backupPath = BackupPath(dataDir);
        if (!File.Exists(backupPath))
        {
            // `uninstall` without the --data-dir used at install time finds no record here, but the system
            // socket may still be pointing at the cider socket we just stopped serving. Saying
            // "nothing to restore" would leave the user with a dangling docker.sock and no idea why.
            var (linkState, linkTarget) = Inspect(dockerSockPath, dockerSockPath);
            if (linkState == SystemSocketState.LinkedElsewhere && StaleLinkReason(linkTarget) is { } reason)
            {
                var stale = string.Join(
                    '\n',
                    $"No system socket backup at {backupPath}, but {dockerSockPath} is still a symlink to",
                    $"{linkTarget}, {reason}. That is what `install --system-socket` leaves behind, so the",
                    "record of the previous target is probably saved under a different data dir. Re-run",
                    "uninstall with the same --data-dir you installed with:",
                    "",
                    "    cider uninstall --data-dir <the data dir you installed with>",
                    "",
                    "or put the previous target back by hand:",
                    "",
                    $"    sudo ln -sf <the socket {dockerSockPath} pointed at before> {dockerSockPath}");
                steps.Add(stale);
                log.WriteLine(stale);
                return new InstallResult(false, stale, steps);
            }

            var none = $"No system socket backup at {backupPath}; nothing to restore.";
            steps.Add(none);
            log.WriteLine(none);
            return new InstallResult(true, none, steps);
        }

        var (backup, readError) = await ReadBackupAsync(backupPath, ct).ConfigureAwait(false);
        if (readError is not null)
        {
            steps.Add($"Could not read {backupPath}: {readError}");
            log.WriteLine(steps[^1]);
        }

        if (backup is null || string.IsNullOrEmpty(backup.Path))
        {
            var unreadable = $"System socket backup {backupPath} is unreadable; restore it by hand with `sudo ln -sf <previous target> {dockerSockPath}`.";
            steps.Add(unreadable);
            return new InstallResult(false, unreadable, steps);
        }

        var (state, currentTarget) = Inspect(backup.Path, backup.LinkedTarget);
        if (state != SystemSocketState.LinkedToUs)
        {
            var untouched = $"{backup.Path} no longer points at the cider socket ({Describe(state, backup.Path, currentTarget)}); leaving it alone. Backup kept at {backupPath}.";
            steps.Add(untouched);
            log.WriteLine(untouched);
            return new InstallResult(true, untouched, steps);
        }

        if (backup.Existed && !backup.WasSymlink)
        {
            // --force-system-socket replaced a real file/socket; it cannot be recreated, only reported.
            var removeForced = await runner(["rm", "-f", backup.Path], ct).ConfigureAwait(false);
            steps.Add($"{removeForced.Command} (exit {removeForced.ExitCode})");
            log.WriteLine(steps[^1]);
            var warning =
                $"WARNING: {backup.Path} was a real file/socket (not a symlink) before cider replaced it, so it " +
                "cannot be restored. Recreate it by restarting whichever engine owns that path" +
                (removeForced.Succeeded ? "; cider's link has been removed." : $". Remove cider's link with `sudo rm -f {backup.Path}`.");
            steps.Add(warning);
            if (removeForced.Succeeded)
            {
                DeleteBackup(backupPath, steps);
            }

            return new InstallResult(removeForced.Succeeded, warning, steps);
        }

        IReadOnlyList<string> argv;
        string manualCommand;
        string successMessage;
        if (backup is { Existed: true, PreviousTarget: { Length: > 0 } previous })
        {
            argv = ["ln", "-sf", previous, backup.Path];
            manualCommand = $"sudo ln -sf {previous} {backup.Path}";
            successMessage = $"Restored {backup.Path} -> {previous}.";
        }
        else
        {
            argv = ["rm", "-f", backup.Path];
            manualCommand = $"sudo rm -f {backup.Path}";
            successMessage = $"Removed {backup.Path} (nothing was there before cider linked it).";
        }

        var result = await runner(argv, ct).ConfigureAwait(false);
        steps.Add($"{result.Command} (exit {result.ExitCode})");
        log.WriteLine(steps[^1]);

        if (!result.Succeeded)
        {
            var manual = $"Could not restore {backup.Path} without an interactive password. Run this yourself:\n\n    {manualCommand}";
            steps.Add(manual);
            return new InstallResult(false, manual, steps);
        }

        steps.Add(successMessage);
        log.WriteLine(successMessage);
        DeleteBackup(backupPath, steps);
        return new InstallResult(true, successMessage, steps);
    }

    /// <summary>Outcome of one privileged filesystem command, plus the command line as it should be logged.</summary>
    internal readonly record struct PrivilegedCommandResult(string Command, int ExitCode, string StdErr, bool Succeeded);

    /// <summary>
    /// Runs a privileged filesystem command (argv, e.g. <c>["ln", "-sf", src, dst]</c>). Production uses
    /// non-interactive sudo; tests substitute a runner that operates on a temp directory instead.
    /// </summary>
    internal delegate Task<PrivilegedCommandResult> PrivilegedCommandRunner(IReadOnlyList<string> argv, CancellationToken ct);

    internal enum SystemSocketState
    {
        Absent,
        LinkedToUs,
        LinkedElsewhere,
        NotASymlink,
    }

    internal static async Task<PrivilegedCommandResult> SudoAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        List<string> args = ["-n", .. argv];
        var result = await ProcessRunner.RunAsync("sudo", args, TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        return new PrivilegedCommandResult($"sudo {string.Join(' ', args)}", result.ExitCode, result.StdErr, result.Succeeded);
    }

    private static void DeleteBackup(string? backupPath, List<string> steps)
    {
        if (backupPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
                steps.Add($"Removed {backupPath}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            steps.Add($"Could not remove {backupPath}: {ex.Message}");
        }
    }

    /// <summary>Reads the backup record, if any. Returns (null, null) when there is no record and (null, error) when it is unreadable.</summary>
    private static async Task<(SystemSocketBackup? Backup, string? Error)> ReadBackupAsync(string backupPath, CancellationToken ct)
    {
        if (!File.Exists(backupPath))
        {
            return (null, null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(backupPath, ct).ConfigureAwait(false);
            return (JsonSerializer.Deserialize(json, BackupJsonContext.Default.SystemSocketBackup), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// True when <paramref name="currentTarget"/> is the cider socket an earlier install recorded
    /// linking, i.e. the new link supersedes our own rather than another engine's.
    /// </summary>
    private static bool SupersedesOurOwnLink(SystemSocketBackup? existing, string path, string? currentTarget) =>
        existing is not null
        && !string.IsNullOrEmpty(currentTarget)
        && !string.IsNullOrEmpty(existing.LinkedTarget)
        && string.Equals(existing.Path, path, StringComparison.Ordinal)
        && string.Equals(existing.LinkedTarget, currentTarget, StringComparison.Ordinal);

    /// <summary>
    /// Whether the symlink target looks like something cider left behind rather than a live engine:
    /// it no longer exists (what an uninstalled cider leaves), or it is an cider path.
    /// Returns null when the target looks like somebody else's working socket.
    /// </summary>
    private static string? StaleLinkReason(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        bool exists;
        try
        {
            exists = File.Exists(target) || Directory.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = true;
        }

        if (!exists)
        {
            return "which no longer exists";
        }

        return target.Contains("cider", StringComparison.OrdinalIgnoreCase)
            ? "an cider socket"
            : null;
    }

    private static string Describe(bool existed, bool wasSymlink, string? previousTarget) => (existed, wasSymlink) switch
    {
        (false, _) => "nothing was there before",
        (true, true) => $"a symlink to {previousTarget}",
        _ => "a real file/socket, which cannot be restored",
    };

    private static string Describe(SystemSocketState state, string dockerSockPath, string? currentTarget) => state switch
    {
        SystemSocketState.Absent => $"{dockerSockPath} did not exist",
        SystemSocketState.LinkedToUs => $"{dockerSockPath} points at the cider socket",
        SystemSocketState.LinkedElsewhere => $"{dockerSockPath} was a symlink to {currentTarget}",
        _ => $"{dockerSockPath} is a real file/socket, not a symlink",
    };

    /// <summary>Classifies what currently lives at <paramref name="dockerSockPath"/> relative to <paramref name="socketPath"/>.</summary>
    internal static (SystemSocketState State, string? Target) Inspect(string dockerSockPath, string socketPath)
    {
        string? linkTarget;
        try
        {
            linkTarget = new FileInfo(dockerSockPath).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (SystemSocketState.NotASymlink, null);
        }

        if (linkTarget is null)
        {
            bool exists;
            try
            {
                exists = File.Exists(dockerSockPath) || Directory.Exists(dockerSockPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exists = true;
            }

            return exists ? (SystemSocketState.NotASymlink, null) : (SystemSocketState.Absent, null);
        }

        if (!System.IO.Path.IsPathRooted(linkTarget))
        {
            linkTarget = System.IO.Path.GetFullPath(linkTarget, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dockerSockPath))!);
        }

        linkTarget = System.IO.Path.GetFullPath(linkTarget);
        var ours = string.Equals(linkTarget, System.IO.Path.GetFullPath(socketPath), StringComparison.Ordinal);
        return (ours ? SystemSocketState.LinkedToUs : SystemSocketState.LinkedElsewhere, linkTarget);
    }
}
