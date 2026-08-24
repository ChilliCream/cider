using Cider.Daemon.Install;
using Xunit;

namespace Cider.Tests.Install;

public class LaunchdInstallerTests
{
    // GeneratePlist/PlistPath read HOME (falling back to the OS user profile folder) purely to
    // report it back in the plist; mirror that same resolution here instead of mutating the
    // process-wide HOME env var, which would race with other tests running in parallel.
    private static string ResolveHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cider-launchd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    private static string? LinkTargetOf(string path) => new FileInfo(path).LinkTarget;

    /// <summary>
    /// Stands in for `sudo -n ln|rm`: applies the same argv the installer builds, in-process and
    /// unprivileged, so these tests never go anywhere near the real /var/run/docker.sock.
    /// </summary>
    private static Task<SystemSocketLink.PrivilegedCommandResult> ApplyUnprivilegedAsync(
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        var command = "sudo -n " + string.Join(' ', argv);
        try
        {
            switch (argv[0])
            {
                case "ln": // ln -sf <target> <path>
                    var linkPath = argv[3];
                    if (File.Exists(linkPath) || LinkTargetOf(linkPath) is not null)
                    {
                        File.Delete(linkPath);
                    }

                    File.CreateSymbolicLink(linkPath, argv[2]);
                    break;
                case "rm": // rm -f <path>
                    File.Delete(argv[2]);
                    break;
                default:
                    return Task.FromResult(new SystemSocketLink.PrivilegedCommandResult(command, 127, $"unsupported: {argv[0]}", false));
            }

            return Task.FromResult(new SystemSocketLink.PrivilegedCommandResult(command, 0, string.Empty, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new SystemSocketLink.PrivilegedCommandResult(command, 1, ex.Message, false));
        }
    }

    [Fact]
    public void GeneratePlist_ProducesExpectedXml_AndEscapesAmpersandInPaths()
    {
        var home = ResolveHome();
        var options = new InstallOptions(
            ExecutablePath: "/usr/local/bin/cider",
            SocketPath: "/Users/testuser/.cider/docker.sock",
            DataDir: "/Users/testuser/.cider & data",
            LogLevel: "Information");

        var expected =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "\t<key>Label</key>\n" +
            "\t<string>com.chillicream.cider.daemon</string>\n" +
            "\t<key>ProgramArguments</key>\n" +
            "\t<array>\n" +
            "\t\t<string>/usr/local/bin/cider</string>\n" +
            "\t\t<string>serve</string>\n" +
            "\t\t<string>--socket</string>\n" +
            "\t\t<string>/Users/testuser/.cider/docker.sock</string>\n" +
            "\t\t<string>--data-dir</string>\n" +
            "\t\t<string>/Users/testuser/.cider &amp; data</string>\n" +
            "\t\t<string>--log-level</string>\n" +
            "\t\t<string>Information</string>\n" +
            "\t</array>\n" +
            "\t<key>RunAtLoad</key>\n" +
            "\t<true/>\n" +
            "\t<key>KeepAlive</key>\n" +
            "\t<dict>\n" +
            "\t\t<key>SuccessfulExit</key>\n" +
            "\t\t<false/>\n" +
            "\t</dict>\n" +
            "\t<key>StandardOutPath</key>\n" +
            "\t<string>/Users/testuser/.cider &amp; data/daemon.log</string>\n" +
            "\t<key>StandardErrorPath</key>\n" +
            "\t<string>/Users/testuser/.cider &amp; data/daemon.log</string>\n" +
            "\t<key>EnvironmentVariables</key>\n" +
            "\t<dict>\n" +
            "\t\t<key>PATH</key>\n" +
            "\t\t<string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>\n" +
            "\t\t<key>HOME</key>\n" +
            $"\t\t<string>{XmlEscape(home)}</string>\n" +
            "\t</dict>\n" +
            "\t<key>ProcessType</key>\n" +
            "\t<string>Background</string>\n" +
            "\t<key>WorkingDirectory</key>\n" +
            "\t<string>/Users/testuser/.cider &amp; data</string>\n" +
            "</dict>\n" +
            "</plist>\n";

        var actual = LaunchdInstaller.GeneratePlist(options);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GeneratePlist_OmitsLogLevelArguments_WhenLogLevelIsNull()
    {
        var options = new InstallOptions(
            ExecutablePath: "/usr/local/bin/cider",
            SocketPath: "/Users/testuser/.cider/docker.sock",
            DataDir: "/Users/testuser/.cider",
            LogLevel: null);

        var xml = LaunchdInstaller.GeneratePlist(options);

        Assert.DoesNotContain("--log-level", xml, StringComparison.Ordinal);
        Assert.Contains("<string>--data-dir</string>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void PlistPath_ReturnsLaunchAgentsPathUnderHome()
    {
        var home = ResolveHome();
        var expected = Path.Combine(home, "Library", "LaunchAgents", "com.chillicream.cider.daemon.plist");

        Assert.Equal(expected, LaunchdInstaller.PlistPath("com.chillicream.cider.daemon"));
    }

    [Fact]
    public void ParseLaunchctlPrint_ExtractsStatePidAndLastExitCode_FromRealisticFixture()
    {
        const string fixture = "gui/501/com.chillicream.cider.daemon = {\n" +
            "\tactive count = 1\n" +
            "\tpath = /Users/testuser/Library/LaunchAgents/com.chillicream.cider.daemon.plist\n" +
            "\ttype = LaunchAgent\n" +
            "\tstate = running\n" +
            "\n" +
            "\tprogram = /usr/local/bin/cider\n" +
            "\targuments = {\n" +
            "\t\t/usr/local/bin/cider\n" +
            "\t\tserve\n" +
            "\t\t--socket\n" +
            "\t\t/Users/testuser/.cider/docker.sock\n" +
            "\t}\n" +
            "\n" +
            "\tpid = 4242\n" +
            "\timmediate reason = process completed\n" +
            "\tlast exit code = 0\n" +
            "\n" +
            "\tspawn type = daemon (via launchd)\n" +
            "\truntime info = {\n" +
            "\t\twakeups = 0\n" +
            "\t}\n" +
            "}\n";

        var (running, pid, lastExitStatus) = LaunchdInstaller.ParseLaunchctlPrint(fixture);

        Assert.True(running);
        Assert.Equal(4242, pid);
        Assert.Equal("0", lastExitStatus);
    }

    [Fact]
    public void ParseLaunchctlPrint_ReturnsNotRunning_WhenStateLineSaysNotRunning()
    {
        const string fixture = "gui/501/com.chillicream.cider.daemon = {\n" +
            "\tactive count = 0\n" +
            "\tpath = /Users/testuser/Library/LaunchAgents/com.chillicream.cider.daemon.plist\n" +
            "\ttype = LaunchAgent\n" +
            "\tstate = not running\n" +
            "\tlast exit code = 1\n" +
            "}\n";

        var (running, pid, lastExitStatus) = LaunchdInstaller.ParseLaunchctlPrint(fixture);

        Assert.False(running);
        Assert.Null(pid);
        Assert.Equal("1", lastExitStatus);
    }

    [Fact]
    public void ParseLaunchctlPrint_ReturnsDefaults_ForEmptyOutput()
    {
        var (running, pid, lastExitStatus) = LaunchdInstaller.ParseLaunchctlPrint(string.Empty);

        Assert.False(running);
        Assert.Null(pid);
        Assert.Null(lastExitStatus);
    }

    [Fact]
    public async Task InstallWiring_PassesSystemSocketForceAndDataDir_ThroughToSystemSocketLink()
    {
        var root = NewTempRoot();
        try
        {
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var ourSocket = Path.Combine(root, "cider.sock");
            File.WriteAllText(ourSocket, "cider");
            var systemSock = Path.Combine(root, "docker.sock");
            File.WriteAllText(systemSock, "a real docker socket"); // a real file, not a symlink

            var options = new InstallOptions(
                ExecutablePath: "/usr/local/bin/cider",
                SocketPath: ourSocket,
                DataDir: dataDir,
                LogLevel: null,
                CreateDockerContext: false,
                SystemSocketSymlink: true);

            var refused = await LaunchdInstaller.LinkSystemSocketAsync(
                options, new StringWriter(), CancellationToken.None, systemSock, ApplyUnprivilegedAsync);

            Assert.False(refused.Success);
            Assert.Contains("Refusing to replace", refused.Message, StringComparison.Ordinal);
            Assert.Equal("a real docker socket", File.ReadAllText(systemSock));
            Assert.False(File.Exists(SystemSocketLink.BackupPath(dataDir)));

            var forced = await LaunchdInstaller.LinkSystemSocketAsync(
                options with { SystemSocketForce = true }, new StringWriter(), CancellationToken.None, systemSock, ApplyUnprivilegedAsync);

            Assert.True(forced.Success);
            Assert.Equal(ourSocket, LinkTargetOf(systemSock));
            // The backup landed in InstallOptions.DataDir, i.e. the data dir was wired through too.
            Assert.True(File.Exists(SystemSocketLink.BackupPath(dataDir)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task UninstallAsync_RestoresTheSystemSocket_UsingTheDataDirItWasGiven()
    {
        var root = NewTempRoot();
        try
        {
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var ourSocket = Path.Combine(root, "cider.sock");
            File.WriteAllText(ourSocket, "cider");
            var orbstack = Path.Combine(root, "orbstack.sock");
            File.WriteAllText(orbstack, "orbstack");
            var systemSock = Path.Combine(root, "docker.sock");
            File.CreateSymbolicLink(systemSock, orbstack);

            var options = new InstallOptions(
                ExecutablePath: "/usr/local/bin/cider",
                SocketPath: ourSocket,
                DataDir: dataDir,
                LogLevel: null,
                CreateDockerContext: false,
                SystemSocketSymlink: true);
            await LaunchdInstaller.LinkSystemSocketAsync(
                options, new StringWriter(), CancellationToken.None, systemSock, ApplyUnprivilegedAsync);
            Assert.Equal(ourSocket, LinkTargetOf(systemSock));

            // A label that exists nowhere, plus stubs for launchd and docker: nothing here touches the
            // machine's real services.
            var label = "com.chillicream.cider.test-" + Guid.NewGuid().ToString("N");
            var commands = new List<string>();
            Task<ProcessRunner.Result> FakeRun(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            {
                commands.Add($"{file} {string.Join(' ', args)}");
                return Task.FromResult(new ProcessRunner.Result(0, file == "id" ? "501\n" : string.Empty, string.Empty, TimedOut: false));
            }

            var removedContexts = new List<string>();
            Task<InstallResult> FakeRemoveContext(string contextName, TextWriter log, CancellationToken ct)
            {
                removedContexts.Add(contextName);
                return Task.FromResult(new InstallResult(true, $"Docker context '{contextName}' removed (stub).", []));
            }

            var result = await LaunchdInstaller.UninstallCoreAsync(
                label,
                new StringWriter(),
                CancellationToken.None,
                dataDir: dataDir,
                dockerSockPath: systemSock,
                privileged: ApplyUnprivilegedAsync,
                run: FakeRun,
                removeContext: FakeRemoveContext);

            Assert.True(result.Success);
            // Uninstall found the record under the data dir it was handed and put OrbStack's target back.
            Assert.Equal(orbstack, LinkTargetOf(systemSock));
            Assert.Equal("orbstack", File.ReadAllText(systemSock));
            Assert.False(File.Exists(SystemSocketLink.BackupPath(dataDir)));
            Assert.Contains(result.Steps, s => s.Contains($"Restored {systemSock} -> {orbstack}", StringComparison.Ordinal));
            Assert.Contains(commands, c => c.Contains("bootout", StringComparison.Ordinal) && c.EndsWith('/' + label));
            Assert.Equal(["cider"], removedContexts);
            Assert.False(File.Exists(LaunchdInstaller.PlistPath(label)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task UninstallAsync_WithoutADataDir_LeavesTheSystemSocketAlone()
    {
        var root = NewTempRoot();
        try
        {
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var ourSocket = Path.Combine(root, "cider.sock");
            File.WriteAllText(ourSocket, "cider");
            var systemSock = Path.Combine(root, "docker.sock");
            File.CreateSymbolicLink(systemSock, ourSocket);

            static Task<ProcessRunner.Result> FakeRun(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(new ProcessRunner.Result(0, file == "id" ? "501\n" : string.Empty, string.Empty, TimedOut: false));

            static Task<InstallResult> FakeRemoveContext(string contextName, TextWriter log, CancellationToken ct) =>
                Task.FromResult(new InstallResult(true, "stub", []));

            var result = await LaunchdInstaller.UninstallCoreAsync(
                "com.chillicream.cider.test-" + Guid.NewGuid().ToString("N"),
                new StringWriter(),
                CancellationToken.None,
                dataDir: null,
                dockerSockPath: systemSock,
                privileged: ApplyUnprivilegedAsync,
                run: FakeRun,
                removeContext: FakeRemoveContext);

            Assert.True(result.Success);
            Assert.Equal(ourSocket, LinkTargetOf(systemSock));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }
}
