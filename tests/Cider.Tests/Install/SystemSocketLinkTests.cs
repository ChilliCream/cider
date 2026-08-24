using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cider.Daemon.Install;
using Xunit;

namespace Cider.Tests.Install;

/// <summary>
/// Drives the whole save -> link -> restore cycle against a temporary directory. Nothing here reads,
/// writes or unlinks the real <c>/var/run/docker.sock</c>: every test passes its own
/// <c>dockerSockPath</c> and an unprivileged command runner in place of <c>sudo</c>.
/// </summary>
public class SystemSocketLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cider-syssock-" + Guid.NewGuid().ToString("N"));

    private string DataDir => Path.Combine(_root, "data");

    /// <summary>The stand-in for /var/run/docker.sock.</summary>
    private string SockPath => Path.Combine(_root, "docker.sock");

    /// <summary>The socket cider would link the system path to.</summary>
    private string OurSocket => Path.Combine(_root, "cider", "docker.sock");

    public SystemSocketLinkTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(OurSocket)!);
        File.WriteAllText(OurSocket, "cider");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(SockPath) || new FileInfo(SockPath).LinkTarget is not null)
            {
                File.Delete(SockPath);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }

        GC.SuppressFinalize(this);
    }

    // Runs `ln`/`rm` for real, but without sudo, so the tests exercise the same argv the installer
    // hands to `sudo -n` in production.
    private static async Task<SystemSocketLink.PrivilegedCommandResult> RunUnprivilegedAsync(
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in argv.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new SystemSocketLink.PrivilegedCommandResult(
            string.Join(' ', argv),
            process.ExitCode,
            stderr,
            process.ExitCode == 0);
    }

    private static Task<SystemSocketLink.PrivilegedCommandResult> RefuseAsync(
        IReadOnlyList<string> argv,
        CancellationToken ct) =>
        Task.FromResult(new SystemSocketLink.PrivilegedCommandResult(
            "sudo -n " + string.Join(' ', argv),
            1,
            "sudo: a password is required\n",
            false));

    private Task<InstallResult> LinkAsync(bool force = false, StringWriter? log = null, string? socketPath = null) =>
        SystemSocketLink.TryLinkCoreAsync(
            socketPath ?? OurSocket,
            log ?? new StringWriter(),
            DataDir,
            SockPath,
            force,
            RunUnprivilegedAsync,
            CancellationToken.None);

    private Task<InstallResult> RestoreAsync(SystemSocketLink.PrivilegedCommandRunner? runner = null) =>
        SystemSocketLink.TryRestoreCoreAsync(
            DataDir,
            new StringWriter(),
            SockPath,
            runner ?? RunUnprivilegedAsync,
            CancellationToken.None);

    private static string? LinkTargetOf(string path) => new FileInfo(path).LinkTarget;

    private SystemSocketBackup ReadBackup()
    {
        var json = File.ReadAllText(SystemSocketLink.BackupPath(DataDir));
        return JsonSerializer.Deserialize<SystemSocketBackup>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void DockerSock_IsTheStandardSystemSocketPath()
    {
        Assert.Equal("/var/run/docker.sock", SystemSocketLink.DockerSock);
    }

    [Fact]
    public void Instructions_ContainsNonInteractiveSudoLinkCommand()
    {
        const string socketPath = "/Users/testuser/.cider/docker.sock";

        var instructions = SystemSocketLink.Instructions(socketPath);

        Assert.Contains($"sudo ln -sf {socketPath} {SystemSocketLink.DockerSock}", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_TellsUserToRecordTheCurrentTargetAndRestoreIt_NotToDeleteTheLink()
    {
        var instructions = SystemSocketLink.Instructions("/tmp/cider.sock");

        Assert.Contains($"readlink {SystemSocketLink.DockerSock}", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo ln -sf <the path readlink printed> {SystemSocketLink.DockerSock}", instructions, StringComparison.Ordinal);
        Assert.Contains(SystemSocketLink.BackupFileName, instructions, StringComparison.Ordinal);
        // The old undo destroyed another engine's wiring instead of restoring it.
        Assert.DoesNotContain($"sudo rm -f {SystemSocketLink.DockerSock}", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_UsesTheGivenSystemSocketPath()
    {
        var instructions = SystemSocketLink.Instructions("/tmp/cider.sock", "/tmp/fake/docker.sock");

        Assert.Contains("sudo ln -sf /tmp/cider.sock /tmp/fake/docker.sock", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemSocketLink.DockerSock, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_WhenSocketIsASymlinkToAnotherEngine_SavesTargetAndRestoreReinstatesIt()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);

        var link = await LinkAsync();

        Assert.True(link.Success);
        Assert.Equal(OurSocket, LinkTargetOf(SockPath));

        var backup = ReadBackup();
        Assert.Equal(SockPath, backup.Path);
        Assert.True(backup.Existed);
        Assert.True(backup.WasSymlink);
        Assert.Equal(orbstack, backup.PreviousTarget);
        Assert.Equal(OurSocket, backup.LinkedTarget);
        Assert.NotEqual(default, backup.SavedAt);

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Equal(orbstack, LinkTargetOf(SockPath));
        Assert.Equal("orbstack", File.ReadAllText(SockPath));
        Assert.False(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task BackupJson_UsesCamelCaseFields()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);

        await LinkAsync();

        var json = File.ReadAllText(SystemSocketLink.BackupPath(DataDir));
        Assert.Contains("\"path\":", json, StringComparison.Ordinal);
        Assert.Contains("\"previousTarget\":", json, StringComparison.Ordinal);
        Assert.Contains("\"wasSymlink\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"savedAt\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_WhenNothingIsThere_RecordsAbsentAndRestoreRemovesOurLink()
    {
        var link = await LinkAsync();

        Assert.True(link.Success);
        Assert.Equal(OurSocket, LinkTargetOf(SockPath));

        var backup = ReadBackup();
        Assert.False(backup.Existed);
        Assert.False(backup.WasSymlink);
        Assert.Null(backup.PreviousTarget);

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Null(LinkTargetOf(SockPath));
        Assert.False(File.Exists(SockPath));
        Assert.False(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task Link_WhenSocketIsARealFile_RefusesAndLeavesItAlone()
    {
        File.WriteAllText(SockPath, "a real docker socket");
        var log = new StringWriter();

        var link = await LinkAsync(log: log);

        Assert.False(link.Success);
        Assert.Contains("Refusing to replace", link.Message, StringComparison.Ordinal);
        // --force-system-socket is the only supported override, and the refusal must not hand the user
        // the very clobber it just refused: `ln -sf` over a real socket file is unrecoverable, and the
        // "readlink printed nothing -> rm -f" advice is exactly wrong here (readlink prints nothing for
        // a real file, but there is a real socket to lose).
        Assert.Contains("--force-system-socket", link.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ln -sf", link.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -f", link.Message, StringComparison.Ordinal);
        Assert.Null(LinkTargetOf(SockPath));
        Assert.Equal("a real docker socket", File.ReadAllText(SockPath));
        Assert.False(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task Link_WhenSocketIsARealFile_AndForced_LinksAndRestoreWarnsItCannotBeRecreated()
    {
        File.WriteAllText(SockPath, "a real docker socket");

        var link = await LinkAsync(force: true);

        Assert.True(link.Success);
        Assert.Equal(OurSocket, LinkTargetOf(SockPath));

        var backup = ReadBackup();
        Assert.True(backup.Existed);
        Assert.False(backup.WasSymlink);
        Assert.Null(backup.PreviousTarget);

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Contains("WARNING", restore.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be restored", restore.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SockPath));
    }

    [Fact]
    public async Task Link_WhenAlreadyLinkedToUs_IsANoOpAndKeepsTheExistingBackup()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);
        await LinkAsync();
        var firstBackup = File.ReadAllText(SystemSocketLink.BackupPath(DataDir));

        var again = await LinkAsync();

        Assert.True(again.Success);
        Assert.Equal("System socket already linked.", again.Message);
        Assert.Equal(firstBackup, File.ReadAllText(SystemSocketLink.BackupPath(DataDir)));

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Equal(orbstack, LinkTargetOf(SockPath));
    }

    [Fact]
    public async Task Link_WhenTheSocketAlreadyPointsAtAnEarlierCiderSocket_KeepsTheOriginalPreviousTarget()
    {
        // Install once (the path belonged to another engine), then install again from the same data dir
        // but with a different --socket. The second install must not record *our own* first socket as
        // "the previous target", or uninstall would restore a socket nobody serves and the real engine's
        // target would be lost forever.
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);
        await LinkAsync();

        var otherSocket = Path.Combine(_root, "cider", "other.sock");
        File.WriteAllText(otherSocket, "cider");
        var second = await LinkAsync(socketPath: otherSocket);

        Assert.True(second.Success);
        Assert.Equal(otherSocket, LinkTargetOf(SockPath));

        var backup = ReadBackup();
        Assert.True(backup.Existed);
        Assert.True(backup.WasSymlink);
        Assert.Equal(orbstack, backup.PreviousTarget);
        Assert.Equal(otherSocket, backup.LinkedTarget);

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Equal(orbstack, LinkTargetOf(SockPath));
        Assert.Equal("orbstack", File.ReadAllText(SockPath));
    }

    [Fact]
    public async Task Restore_WhenThereIsNoBackupButOurStaleLinkRemains_PointsAtTheOtherDataDirAndFails()
    {
        // `uninstall` run without the --data-dir used at install time: the record is somewhere else, and
        // the system socket is left dangling at the cider socket uninstall just stopped serving.
        var staleCiderSocket = Path.Combine(_root, "cider", "gone.sock");
        File.CreateSymbolicLink(SockPath, staleCiderSocket);
        Assert.False(File.Exists(SystemSocketLink.BackupPath(DataDir)));

        var restore = await RestoreAsync();

        Assert.False(restore.Success);
        Assert.DoesNotContain("nothing to restore", restore.Message, StringComparison.Ordinal);
        Assert.Contains("different data dir", restore.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cider uninstall --data-dir", restore.Message, StringComparison.Ordinal);
        Assert.Contains($"sudo ln -sf", restore.Message, StringComparison.Ordinal);
        Assert.Contains(SockPath, restore.Message, StringComparison.Ordinal);
        // It reports, it does not act: the link is untouched.
        Assert.Equal(staleCiderSocket, LinkTargetOf(SockPath));
    }

    [Fact]
    public async Task Restore_WhenSocketNoLongerPointsAtUs_LeavesItAloneAndKeepsTheBackup()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        var colima = Path.Combine(_root, "colima.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.WriteAllText(colima, "colima");
        File.CreateSymbolicLink(SockPath, orbstack);
        await LinkAsync();

        // Someone else (another engine's installer) took the path over after us.
        File.Delete(SockPath);
        File.CreateSymbolicLink(SockPath, colima);

        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Contains("no longer points at the cider socket", restore.Message, StringComparison.Ordinal);
        Assert.Equal(colima, LinkTargetOf(SockPath));
        Assert.True(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task Restore_WhenNoBackupWasRecorded_IsANoOp()
    {
        var restore = await RestoreAsync();

        Assert.True(restore.Success);
        Assert.Contains("nothing to restore", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_WhenSudoNeedsAPassword_PrintsTheExactCommandAndFails()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);
        await LinkAsync();

        var restore = await RestoreAsync(RefuseAsync);

        Assert.False(restore.Success);
        Assert.Contains($"sudo ln -sf {orbstack} {SockPath}", restore.Message, StringComparison.Ordinal);
        // Nothing was changed and the record survives, so the user can retry.
        Assert.Equal(OurSocket, LinkTargetOf(SockPath));
        Assert.True(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task Link_WhenSudoNeedsAPassword_DropsTheBackupAndPrintsInstructions()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);

        var link = await SystemSocketLink.TryLinkCoreAsync(
            OurSocket,
            new StringWriter(),
            DataDir,
            SockPath,
            allowReplaceExisting: false,
            RefuseAsync,
            CancellationToken.None);

        Assert.False(link.Success);
        Assert.Contains($"readlink {SockPath}", link.Message, StringComparison.Ordinal);
        Assert.Equal(orbstack, LinkTargetOf(SockPath));
        Assert.False(File.Exists(SystemSocketLink.BackupPath(DataDir)));
    }

    [Fact]
    public async Task Link_WritesTheBackupBeforeTouchingTheSystemSocket()
    {
        var orbstack = Path.Combine(_root, "orbstack.sock");
        File.WriteAllText(orbstack, "orbstack");
        File.CreateSymbolicLink(SockPath, orbstack);

        var order = new StringBuilder();
        Task<SystemSocketLink.PrivilegedCommandResult> Observe(IReadOnlyList<string> argv, CancellationToken ct)
        {
            order.Append(File.Exists(SystemSocketLink.BackupPath(DataDir)) ? "backup-then-link" : "link-without-backup");
            return RunUnprivilegedAsync(argv, ct);
        }

        await SystemSocketLink.TryLinkCoreAsync(
            OurSocket,
            new StringWriter(),
            DataDir,
            SockPath,
            allowReplaceExisting: false,
            Observe,
            CancellationToken.None);

        Assert.Equal("backup-then-link", order.ToString());
    }
}
