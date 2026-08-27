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
            "\t<string>Interactive</string>\n" +
            "\t<key>WorkingDirectory</key>\n" +
            "\t<string>/Users/testuser/.cider &amp; data</string>\n" +
            "</dict>\n" +
            "</plist>\n";

        var actual = LaunchdInstaller.GeneratePlist(options);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GeneratePlist_UsesInteractiveProcessType_NeverBackground()
    {
        var options = new InstallOptions(
            ExecutablePath: "/usr/local/bin/cider",
            SocketPath: "/Users/testuser/.cider/docker.sock",
            DataDir: "/Users/testuser/.cider",
            LogLevel: null);

        var xml = LaunchdInstaller.GeneratePlist(options);

        // Background QoS throttles CPU/IO for this process and every `container` CLI child it
        // spawns (cider-8ok); Interactive is the only ProcessType launchd applies no throttling to.
        Assert.Contains("<key>ProcessType</key>\n\t<string>Interactive</string>\n", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractProcessType_ReadsTheStringFollowingTheProcessTypeKey()
    {
        const string plist =
            "<plist version=\"1.0\">\n<dict>\n\t<key>Label</key>\n\t<string>x</string>\n" +
            "\t<key>ProcessType</key>\n\t<string>Background</string>\n</dict>\n</plist>\n";

        Assert.Equal("Background", LaunchdInstaller.ExtractProcessType(plist));
    }

    [Fact]
    public void ExtractProcessType_ReturnsNull_WhenKeyIsAbsent()
    {
        const string plist = "<plist version=\"1.0\">\n<dict>\n\t<key>Label</key>\n\t<string>x</string>\n</dict>\n</plist>\n";

        Assert.Null(LaunchdInstaller.ExtractProcessType(plist));
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

    private const string Uid = "501";
    private const string TestPlistPath = "/Users/testuser/Library/LaunchAgents/com.chillicream.cider.daemon.plist";
    private const string EioStdErr = "Bootstrap failed: 5: Input/output error\n";

    private static ProcessRunner.Result Ok(string stdout = "") => new(0, stdout, string.Empty, TimedOut: false);

    private static ProcessRunner.Result Fail(int exitCode, string stderr = "") => new(exitCode, string.Empty, stderr, TimedOut: false);

    /// <summary>No-op delay so settle-poll and backoff tests run instantly.</summary>
    private static Task InstantDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>
    /// Scripted launchctl stub: records every command; answers `print` from a queue of results
    /// (last entry repeats) and `bootstrap` from its own queue (last entry repeats). `bootout`
    /// always exits with <paramref name="bootoutExit"/>.
    /// </summary>
    private static LaunchdInstaller.ExternalCommandRunner ScriptedLaunchctl(
        List<string> commands,
        int bootoutExit,
        Queue<ProcessRunner.Result> printResults,
        Queue<ProcessRunner.Result> bootstrapResults)
    {
        ProcessRunner.Result lastPrint = Fail(113, "Could not find service");
        ProcessRunner.Result lastBootstrap = Ok();
        return (file, args, timeout, ct) =>
        {
            commands.Add($"{file} {string.Join(' ', args)}");
            Assert.Equal("launchctl", file);
            switch (args[0])
            {
                case "bootout":
                    return Task.FromResult(bootoutExit == 0 ? Ok() : Fail(bootoutExit, "Boot-out failed: 3: No such process"));
                case "print":
                    if (printResults.Count > 0)
                    {
                        lastPrint = printResults.Dequeue();
                    }

                    return Task.FromResult(lastPrint);
                case "bootstrap":
                    if (bootstrapResults.Count > 0)
                    {
                        lastBootstrap = bootstrapResults.Dequeue();
                    }

                    return Task.FromResult(lastBootstrap);
                default:
                    throw new InvalidOperationException($"unexpected launchctl subcommand: {args[0]}");
            }
        };
    }

    [Fact]
    public async Task BootoutThenBootstrap_RetriesEioBootstrap_AndSucceeds()
    {
        var commands = new List<string>();
        var steps = new List<string>();
        var run = ScriptedLaunchctl(
            commands,
            bootoutExit: 0,
            printResults: new Queue<ProcessRunner.Result>([Fail(113, "Could not find service")]),
            bootstrapResults: new Queue<ProcessRunner.Result>([Fail(5, EioStdErr), Ok()]));

        var (bootstrap, failureMessage) = await LaunchdInstaller.BootoutThenBootstrapAsync(
            run, Uid, "com.chillicream.cider.daemon", TestPlistPath, steps, new StringWriter(), CancellationToken.None, InstantDelay);

        Assert.True(bootstrap.Succeeded);
        Assert.Null(failureMessage);
        // The step log shows the failed first attempt and the successful retry.
        Assert.Contains(steps, s => s.Contains("(exit 5, attempt 1/3)", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("(exit 0, attempt 2/3)", StringComparison.Ordinal));
        Assert.Equal(2, commands.Count(c => c.Contains(" bootstrap ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BootoutThenBootstrap_PollsPrintUntilNotFound_BeforeBootstrapping()
    {
        var commands = new List<string>();
        var steps = new List<string>();
        // launchd still reports the service twice mid-teardown, then it is gone.
        var run = ScriptedLaunchctl(
            commands,
            bootoutExit: 0,
            printResults: new Queue<ProcessRunner.Result>([Ok("state = running"), Ok("state = running"), Fail(113, "Could not find service")]),
            bootstrapResults: new Queue<ProcessRunner.Result>([Ok()]));

        var (bootstrap, failureMessage) = await LaunchdInstaller.BootoutThenBootstrapAsync(
            run, Uid, "com.chillicream.cider.daemon", TestPlistPath, steps, new StringWriter(), CancellationToken.None, InstantDelay);

        Assert.True(bootstrap.Succeeded);
        Assert.Null(failureMessage);
        // The poll consumed the not-found transition: exactly three prints, all before the bootstrap.
        Assert.Equal(3, commands.Count(c => c.Contains(" print ", StringComparison.Ordinal)));
        var lastPrint = commands.FindLastIndex(c => c.Contains(" print ", StringComparison.Ordinal));
        var firstBootstrap = commands.FindIndex(c => c.Contains(" bootstrap ", StringComparison.Ordinal));
        Assert.True(lastPrint < firstBootstrap, $"expected all prints before bootstrap; commands: {string.Join(" | ", commands)}");
        Assert.Contains(steps, s => s.Contains("Waited for launchd to finish removing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootoutThenBootstrap_SkipsSettlePoll_WhenBootoutRemovedNothing()
    {
        var commands = new List<string>();
        var steps = new List<string>();
        // Fresh install: bootout exits 3 ("No such process"); no service existed, nothing to wait for.
        var run = ScriptedLaunchctl(
            commands,
            bootoutExit: 3,
            printResults: new Queue<ProcessRunner.Result>(),
            bootstrapResults: new Queue<ProcessRunner.Result>([Ok()]));

        var (bootstrap, failureMessage) = await LaunchdInstaller.BootoutThenBootstrapAsync(
            run, Uid, "com.chillicream.cider.daemon", TestPlistPath, steps, new StringWriter(), CancellationToken.None, InstantDelay);

        Assert.True(bootstrap.Succeeded);
        Assert.Null(failureMessage);
        Assert.DoesNotContain(commands, c => c.Contains(" print ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootoutThenBootstrap_PersistentEio_FailsWithMachineStateAndRemediation()
    {
        var commands = new List<string>();
        var steps = new List<string>();
        var run = ScriptedLaunchctl(
            commands,
            bootoutExit: 0,
            printResults: new Queue<ProcessRunner.Result>([Fail(113, "Could not find service")]),
            bootstrapResults: new Queue<ProcessRunner.Result>([Fail(5, EioStdErr)])); // repeats forever

        var (bootstrap, failureMessage) = await LaunchdInstaller.BootoutThenBootstrapAsync(
            run, Uid, "com.chillicream.cider.daemon", TestPlistPath, steps, new StringWriter(), CancellationToken.None, InstantDelay);

        Assert.False(bootstrap.Succeeded);
        Assert.Equal(3, commands.Count(c => c.Contains(" bootstrap ", StringComparison.Ordinal))); // bounded, not infinite
        Assert.NotNull(failureMessage);
        // The message states the machine state and the remediation.
        Assert.Contains("The previous daemon was stopped and the new one did not start", failureMessage, StringComparison.Ordinal);
        Assert.Contains("Re-run `cider install`", failureMessage, StringComparison.Ordinal);
        Assert.Contains("Input/output error", failureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootoutThenBootstrap_GivesUpBoundedly_WhenServiceNeverDisappears()
    {
        var commands = new List<string>();
        var steps = new List<string>();
        // Pathological launchd: print keeps finding the service forever. The settle poll must be
        // bounded and install must still attempt the bootstrap.
        var run = ScriptedLaunchctl(
            commands,
            bootoutExit: 0,
            printResults: new Queue<ProcessRunner.Result>([Ok("state = running")]), // repeats forever
            bootstrapResults: new Queue<ProcessRunner.Result>([Ok()]));

        var (bootstrap, failureMessage) = await LaunchdInstaller.BootoutThenBootstrapAsync(
            run, Uid, "com.chillicream.cider.daemon", TestPlistPath, steps, new StringWriter(), CancellationToken.None, InstantDelay);

        Assert.True(bootstrap.Succeeded);
        Assert.Null(failureMessage);
        Assert.Equal(20, commands.Count(c => c.Contains(" print ", StringComparison.Ordinal)));
        Assert.Contains(steps, s => s.Contains("attempting bootstrap anyway", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/opt/homebrew/Cellar/cider/0.2.0/bin/cider", "/opt/homebrew/opt/cider/bin/cider")]
    [InlineData("/usr/local/Cellar/cider/0.3.0-rc.1/bin/cider", "/usr/local/opt/cider/bin/cider")]
    [InlineData("/home/linuxbrew/.linuxbrew/Cellar/cider/1.0.0/bin/cider", "/home/linuxbrew/.linuxbrew/opt/cider/bin/cider")]
    public void StabilizeHomebrewExecutablePath_MapsVersionedCellarPath_ToStableOptSymlink(string cellarPath, string expectedOptPath)
    {
        string? probed = null;
        var result = LaunchdInstaller.StabilizeHomebrewExecutablePathCore(cellarPath, p =>
        {
            probed = p;
            return true;
        });

        Assert.Equal(expectedOptPath, result);
        Assert.Equal(expectedOptPath, probed);
    }

    [Theory]
    [InlineData("/Users/dev/local/cider/src/Cider.Daemon/bin/Debug/net10.0/cider")] // dev build
    [InlineData("/usr/local/bin/cider")] // .pkg install
    [InlineData("/opt/homebrew/opt/cider/bin/cider")] // already the stable opt path
    [InlineData("/opt/homebrew/Cellar/cider/0.2.0/libexec/cider")] // not under bin/
    [InlineData("/opt/homebrew/Cellar/cider/bin/cider")] // no version segment (bin where <version> goes)
    [InlineData("/data/Cellar-archive/cider/0.2.0/bin/cider")] // "Cellar" only as a substring
    [InlineData("Cellar/cider/0.2.0/bin/cider")] // relative path
    [InlineData("")]
    public void StabilizeHomebrewExecutablePath_LeavesNonCellarPathsUntouched_WithoutTouchingTheFilesystem(string path)
    {
        var result = LaunchdInstaller.StabilizeHomebrewExecutablePathCore(
            path,
            _ => throw new InvalidOperationException("non-Cellar paths must not probe the filesystem"));

        Assert.Equal(path, result);
    }

    [Fact]
    public void StabilizeHomebrewExecutablePath_FallsBackToTheCellarPath_WhenTheOptSymlinkIsMissing()
    {
        const string cellarPath = "/opt/homebrew/Cellar/cider/0.2.0/bin/cider";

        var result = LaunchdInstaller.StabilizeHomebrewExecutablePathCore(cellarPath, _ => false);

        Assert.Equal(cellarPath, result);
    }
}
