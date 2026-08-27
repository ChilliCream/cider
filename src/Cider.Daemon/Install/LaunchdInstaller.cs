using System.Text;

namespace Cider.Daemon.Install;

/// <summary>
/// Installs/uninstalls cider as a per-user launchd agent (gui/&lt;uid&gt;) and reports its status.
/// </summary>
public static class LaunchdInstaller
{
    private const string PathEnvironmentValue = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin";

    /// <summary>
    /// launchd.plist(5): "Interactive jobs run with the same resource limitations as apps, i.e.
    /// none." Anything else (Background/Standard/Adaptive) makes macOS throttle CPU/IO for this
    /// process and every child it spawns -- including every `container` CLI call cider makes.
    /// </summary>
    private const string ProcessTypeValue = "Interactive";

    /// <summary>
    /// Runs an external command (<c>launchctl</c>, <c>id</c>). Production is <see cref="ProcessRunner.RunAsync"/>;
    /// tests substitute a stub so the uninstall wiring can be exercised without touching the machine's launchd.
    /// </summary>
    internal delegate Task<ProcessRunner.Result> ExternalCommandRunner(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Removes the docker context. Production is <see cref="DockerContextInstaller.RemoveAsync"/>; tests
    /// substitute a stub so no `docker context rm` runs against the developer's real Docker CLI.
    /// </summary>
    internal delegate Task<InstallResult> DockerContextRemover(string contextName, TextWriter log, CancellationToken ct);

    /// <summary>~/Library/LaunchAgents/&lt;label&gt;.plist</summary>
    public static string PlistPath(string label) =>
        Path.Combine(GetHomeDirectory(), "Library", "LaunchAgents", $"{label}.plist");

    /// <summary>Renders the launchd agent property list XML for <paramref name="options"/>.</summary>
    public static string GeneratePlist(InstallOptions options)
    {
        var logPath = Path.Combine(options.DataDir, "daemon.log");
        var home = GetHomeDirectory();

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        sb.Append("<dict>\n");

        AppendKey(sb, "Label");
        AppendString(sb, options.Label);

        AppendKey(sb, "ProgramArguments");
        sb.Append("\t<array>\n");
        AppendArrayString(sb, options.ExecutablePath);
        AppendArrayString(sb, "serve");
        AppendArrayString(sb, "--socket");
        AppendArrayString(sb, options.SocketPath);
        AppendArrayString(sb, "--data-dir");
        AppendArrayString(sb, options.DataDir);
        if (!string.IsNullOrEmpty(options.LogLevel))
        {
            AppendArrayString(sb, "--log-level");
            AppendArrayString(sb, options.LogLevel);
        }
        sb.Append("\t</array>\n");

        AppendKey(sb, "RunAtLoad");
        sb.Append("\t<true/>\n");

        AppendKey(sb, "KeepAlive");
        sb.Append("\t<dict>\n");
        sb.Append("\t\t<key>SuccessfulExit</key>\n");
        sb.Append("\t\t<false/>\n");
        sb.Append("\t</dict>\n");

        AppendKey(sb, "StandardOutPath");
        AppendString(sb, logPath);

        AppendKey(sb, "StandardErrorPath");
        AppendString(sb, logPath);

        AppendKey(sb, "EnvironmentVariables");
        sb.Append("\t<dict>\n");
        sb.Append("\t\t<key>PATH</key>\n");
        sb.Append($"\t\t<string>{Escape(PathEnvironmentValue)}</string>\n");
        sb.Append("\t\t<key>HOME</key>\n");
        sb.Append($"\t\t<string>{Escape(home)}</string>\n");
        sb.Append("\t</dict>\n");

        AppendKey(sb, "ProcessType");
        AppendString(sb, ProcessTypeValue);

        AppendKey(sb, "WorkingDirectory");
        AppendString(sb, options.DataDir);

        sb.Append("</dict>\n");
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Maps a Homebrew Cellar executable path to the stable <c>opt</c> symlink brew maintains
    /// across upgrades: <c>&lt;prefix&gt;/Cellar/&lt;name&gt;/&lt;version&gt;/bin/&lt;exe&gt;</c> becomes
    /// <c>&lt;prefix&gt;/opt/&lt;name&gt;/bin/&lt;exe&gt;</c>. The versioned Cellar directory is deleted by
    /// <c>brew cleanup</c> on upgrade, so a plist pointing at it leaves KeepAlive respawning a
    /// deleted binary if the post-upgrade `cider install` never succeeds. Any path that does not
    /// match the Cellar shape (dev builds, .pkg installs) — or whose opt symlink does not exist —
    /// is returned unchanged.
    /// </summary>
    public static string StabilizeHomebrewExecutablePath(string executablePath) =>
        StabilizeHomebrewExecutablePathCore(executablePath, File.Exists);

    /// <summary>
    /// <see cref="StabilizeHomebrewExecutablePath"/> with the single allowed filesystem probe
    /// (does the opt path exist?) injectable so tests run against no real Homebrew prefix.
    /// </summary>
    internal static string StabilizeHomebrewExecutablePathCore(string executablePath, Func<string, bool> fileExists)
    {
        // Pure string mapping over an absolute path shaped <prefix>/Cellar/<name>/<version>/bin/<exe>.
        if (executablePath.Length == 0 || executablePath[0] != '/')
        {
            return executablePath;
        }

        var segments = executablePath.Split('/');
        var n = segments.Length;

        // Need at least "" + <prefix...> + Cellar/<name>/<version>/bin/<exe> — Cellar sits at n-5.
        if (n < 7
            || segments[n - 5] != "Cellar"
            || segments[n - 2] != "bin"
            || segments[n - 4].Length == 0
            || segments[n - 3].Length == 0
            || segments[n - 1].Length == 0)
        {
            return executablePath;
        }

        var prefix = string.Join('/', segments[..(n - 5)]);
        var optPath = $"{prefix}/opt/{segments[n - 4]}/bin/{segments[n - 1]}";
        return fileExists(optPath) ? optPath : executablePath;
    }

    /// <summary>
    /// Writes the plist, (re)loads it under launchd, and waits up to 10s for the daemon's
    /// socket to appear. Optionally wires up a docker context and/or the system socket symlink.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(InstallOptions options, TextWriter log, CancellationToken ct)
    {
        var steps = new List<string>();
        try
        {
            Directory.CreateDirectory(options.DataDir);
            steps.Add($"Ensured data directory: {options.DataDir}");
            Log(log, steps[^1]);

            var plistPath = PlistPath(options.Label);
            var plistDir = Path.GetDirectoryName(plistPath)!;
            Directory.CreateDirectory(plistDir);

            // Read the previous plist's ProcessType (if any) before it's overwritten below, so we
            // can tell the user when a stale Background-QoS install is about to be fixed.
            string? previousProcessType = null;
            if (File.Exists(plistPath))
            {
                previousProcessType = ExtractProcessType(await File.ReadAllTextAsync(plistPath, ct).ConfigureAwait(false));
            }

            var plistXml = GeneratePlist(options);
            await File.WriteAllTextAsync(plistPath, plistXml, ct).ConfigureAwait(false);
            steps.Add($"Wrote plist: {plistPath}");
            Log(log, steps[^1]);

            if (previousProcessType is not null && !string.Equals(previousProcessType, ProcessTypeValue, StringComparison.Ordinal))
            {
                steps.Add($"ProcessType changed: {previousProcessType} -> {ProcessTypeValue} (restarting the daemon so the new resource class takes effect)");
                Log(log, steps[^1]);
            }

            var uid = await GetUidAsync(RunExternalAsync, ct).ConfigureAwait(false);
            var target = $"gui/{uid}/{options.Label}";

            var (_, bootstrapFailure) = await BootoutThenBootstrapAsync(
                RunExternalAsync, uid, options.Label, plistPath, steps, log, ct).ConfigureAwait(false);
            if (bootstrapFailure is not null)
            {
                steps.Add(bootstrapFailure);
                return new InstallResult(false, bootstrapFailure, steps);
            }

            var kickstart = await ProcessRunner.RunAsync("launchctl", ["kickstart", "-k", target], TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
            steps.Add($"launchctl kickstart -k {target} (exit {kickstart.ExitCode})");
            Log(log, steps[^1]);

            var socketReady = await WaitForSocketAsync(options.SocketPath, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            steps.Add(socketReady
                ? $"Socket ready: {options.SocketPath}"
                : $"Timed out waiting for socket: {options.SocketPath}");
            Log(log, steps[^1]);

            var message = socketReady
                ? "cider daemon installed and running."
                : $"Daemon installed but the socket did not appear within 10s at {options.SocketPath}. Check {Path.Combine(options.DataDir, "daemon.log")}.";

            if (options.CreateDockerContext)
            {
                var dockerResult = await DockerContextInstaller.EnsureAsync(options.ContextName, options.SocketPath, setCurrent: false, log, ct).ConfigureAwait(false);
                steps.AddRange(dockerResult.Steps);
                steps.Add(dockerResult.Message);
            }

            if (options.SystemSocketSymlink)
            {
                var linkResult = await LinkSystemSocketAsync(options, log, ct).ConfigureAwait(false);
                steps.AddRange(linkResult.Steps);
                steps.Add(linkResult.Message);
                if (!linkResult.Success)
                {
                    // The refusal/fallback instructions must reach the user, not just the step log.
                    message = message + "\n\n" + linkResult.Message;
                }
            }

            // No `else` here on purpose: when --system-socket was NOT passed, the
            // SystemSocketLink.Instructions block is `cider install`'s (Program.cs) to print, and
            // it does so under the same condition. Folding it into the message here too printed it
            // twice on the first output a new user ever sees (cider-xij).

            return new InstallResult(socketReady, message, steps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            steps.Add($"Error: {ex.Message}");
            return new InstallResult(false, $"Install failed: {ex.Message}", steps);
        }
    }

    /// <summary>Bounded number of <c>launchctl bootstrap</c> attempts before install gives up.</summary>
    internal const int BootstrapAttempts = 3;

    /// <summary>
    /// Settle-poll bound after a successful bootout: <see cref="SettleMaxPolls"/> x
    /// <see cref="SettlePollInterval"/> = ~5s. A poll count (not a wall-clock deadline) so tests
    /// that inject an instant delay still terminate deterministically.
    /// </summary>
    private const int SettleMaxPolls = 20;

    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan BootstrapRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The bootout -&gt; settle-wait -&gt; bootstrap-with-retry sequence of <see cref="InstallAsync"/>,
    /// with the process runner and delays injectable so tests can drive it without launchd.
    ///
    /// launchctl's <c>bootout</c> returns before launchd has finished tearing the service down, and
    /// bootstrapping the same label mid-teardown fails with EIO ("Bootstrap failed: 5: Input/output
    /// error") — which left a real upgrade with the old daemon stopped and no new one installed
    /// (cider-gu1). So: after a bootout that removed a service (exit 0), poll
    /// <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c> until it reports not-found (bounded ~5s),
    /// then bootstrap, retrying up to <see cref="BootstrapAttempts"/> times with a short backoff.
    /// </summary>
    /// <returns>
    /// The last bootstrap result, plus — when every attempt failed — the user-facing failure
    /// message stating the machine state and the remediation; <c>null</c> on success.
    /// </returns>
    internal static async Task<(ProcessRunner.Result Bootstrap, string? FailureMessage)> BootoutThenBootstrapAsync(
        ExternalCommandRunner run,
        string uid,
        string label,
        string plistPath,
        List<string> steps,
        TextWriter log,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var target = $"gui/{uid}/{label}";

        var bootout = await run("launchctl", ["bootout", target], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        steps.Add($"launchctl bootout {target} (exit {bootout.ExitCode})");
        Log(log, steps[^1]);

        if (bootout.ExitCode == 0)
        {
            // A service was actually removed; wait for launchd to finish the teardown before
            // bootstrapping the same label. `launchctl print` failing means the service is gone.
            var settled = false;
            for (var poll = 0; poll < SettleMaxPolls; poll++)
            {
                var print = await run("launchctl", ["print", target], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!print.Succeeded)
                {
                    settled = true;
                    break;
                }

                await delay(SettlePollInterval, ct).ConfigureAwait(false);
            }

            steps.Add(settled
                ? $"Waited for launchd to finish removing {target}"
                : $"launchd still reports {target} after ~{SettleMaxPolls * SettlePollInterval.TotalSeconds:0}s; attempting bootstrap anyway");
            Log(log, steps[^1]);
        }

        ProcessRunner.Result bootstrap = default;
        for (var attempt = 1; attempt <= BootstrapAttempts; attempt++)
        {
            bootstrap = await run("launchctl", ["bootstrap", $"gui/{uid}", plistPath], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            steps.Add($"launchctl bootstrap gui/{uid} {plistPath} (exit {bootstrap.ExitCode}, attempt {attempt}/{BootstrapAttempts})");
            Log(log, steps[^1]);
            if (bootstrap.Succeeded)
            {
                return (bootstrap, null);
            }

            if (attempt < BootstrapAttempts)
            {
                await delay(BootstrapRetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }

        var state = bootout.ExitCode == 0
            ? "The previous daemon was stopped and the new one did not start, so no cider daemon is running right now."
            : "The new daemon did not start.";
        var failureMessage =
            $"launchctl bootstrap failed after {BootstrapAttempts} attempts: {bootstrap.StdErr.Trim()}\n" +
            $"{state} Re-run `cider install` to try again.";
        return (bootstrap, failureMessage);
    }

    /// <summary>
    /// Hands the system-socket half of <see cref="InstallAsync"/> to <see cref="SystemSocketLink"/>:
    /// the socket to link to, the data dir the backup record is written to, and whether
    /// <see cref="InstallOptions.SystemSocketForce"/> allows replacing a non-symlink. Tests override
    /// <paramref name="dockerSockPath"/>/<paramref name="privileged"/> to drive this against a temp path.
    /// </summary>
    internal static Task<InstallResult> LinkSystemSocketAsync(
        InstallOptions options,
        TextWriter log,
        CancellationToken ct,
        string dockerSockPath = SystemSocketLink.DockerSock,
        SystemSocketLink.PrivilegedCommandRunner? privileged = null) =>
        SystemSocketLink.TryLinkCoreAsync(
            options.SocketPath,
            log,
            options.DataDir,
            dockerSockPath,
            options.SystemSocketForce,
            privileged ?? SystemSocketLink.SudoAsync,
            ct);

    /// <summary>
    /// Unloads the launchd agent, deletes its plist, best-effort removes the docker context and —
    /// when <paramref name="dataDir"/> is given — restores the system socket target recorded by
    /// <c>install --system-socket</c>.
    /// </summary>
    public static Task<InstallResult> UninstallAsync(string label, TextWriter log, CancellationToken ct, string? dataDir = null) =>
        UninstallCoreAsync(label, log, ct, dataDir);

    /// <summary>
    /// <see cref="UninstallAsync"/> with every off-process effect injectable, so tests can prove the
    /// wiring (label, data dir, restore) without running launchctl/docker against the real machine.
    /// </summary>
    internal static async Task<InstallResult> UninstallCoreAsync(
        string label,
        TextWriter log,
        CancellationToken ct,
        string? dataDir = null,
        string dockerSockPath = SystemSocketLink.DockerSock,
        SystemSocketLink.PrivilegedCommandRunner? privileged = null,
        ExternalCommandRunner? run = null,
        DockerContextRemover? removeContext = null)
    {
        run ??= RunExternalAsync;
        removeContext ??= DockerContextInstaller.RemoveAsync;

        var steps = new List<string>();
        try
        {
            var uid = await GetUidAsync(run, ct).ConfigureAwait(false);
            var target = $"gui/{uid}/{label}";

            var bootout = await run("launchctl", ["bootout", target], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            steps.Add($"launchctl bootout {target} (exit {bootout.ExitCode})");
            Log(log, steps[^1]);

            var plistPath = PlistPath(label);
            if (File.Exists(plistPath))
            {
                File.Delete(plistPath);
                steps.Add($"Deleted plist: {plistPath}");
            }
            else
            {
                steps.Add($"Plist not present: {plistPath}");
            }
            Log(log, steps[^1]);

            var dockerResult = await removeContext("cider", log, ct).ConfigureAwait(false);
            steps.AddRange(dockerResult.Steps);
            steps.Add(dockerResult.Message);

            if (!string.IsNullOrEmpty(dataDir))
            {
                var restore = await SystemSocketLink.TryRestoreCoreAsync(
                    dataDir,
                    log,
                    dockerSockPath,
                    privileged ?? SystemSocketLink.SudoAsync,
                    ct).ConfigureAwait(false);
                steps.AddRange(restore.Steps);
                steps.Add(restore.Message);
                if (!restore.Success)
                {
                    return new InstallResult(
                        false,
                        "cider daemon uninstalled, but the system socket could not be restored automatically.\n\n" + restore.Message,
                        steps);
                }
            }

            return new InstallResult(true, "cider daemon uninstalled.", steps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            steps.Add($"Error: {ex.Message}");
            return new InstallResult(false, $"Uninstall failed: {ex.Message}", steps);
        }
    }

    /// <summary>Runs <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c> and reports the current state.</summary>
    public static async Task<ServiceStatus> StatusAsync(string label, CancellationToken ct)
    {
        var plistPath = PlistPath(label);
        var uid = await GetUidAsync(RunExternalAsync, ct).ConfigureAwait(false);
        var result = await ProcessRunner.RunAsync("launchctl", ["print", $"gui/{uid}/{label}"], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var installed = File.Exists(plistPath);
            return new ServiceStatus(installed, false, null, installed ? plistPath : null, null);
        }

        var (running, pid, lastExitStatus) = ParseLaunchctlPrint(result.StdOut);
        return new ServiceStatus(true, running, pid, plistPath, lastExitStatus);
    }

    /// <summary>
    /// Parses the subset of <c>launchctl print</c> output cider cares about:
    /// "state = running", "pid = N", "last exit code = N".
    /// </summary>
    internal static (bool Running, int? Pid, string? LastExitStatus) ParseLaunchctlPrint(string output)
    {
        var running = false;
        int? pid = null;
        string? lastExitStatus = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("state = ", StringComparison.Ordinal))
            {
                var value = line["state = ".Length..].Trim();
                running = value.Equals("running", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("pid = ", StringComparison.Ordinal))
            {
                var value = line["pid = ".Length..].Trim();
                if (int.TryParse(value, out var parsedPid))
                {
                    pid = parsedPid;
                }
            }
            else if (line.StartsWith("last exit code = ", StringComparison.Ordinal))
            {
                lastExitStatus = line["last exit code = ".Length..].Trim();
            }
        }

        return (running, pid, lastExitStatus);
    }

    /// <summary>
    /// Pulls the &lt;string&gt; value that follows &lt;key&gt;ProcessType&lt;/key&gt; out of a plist's XML,
    /// or <c>null</c> if the key isn't present. Used to detect and report a stale ProcessType from a
    /// previous install rather than relying on a full XML parser for one field.
    /// </summary>
    internal static string? ExtractProcessType(string plistXml)
    {
        const string keyTag = "<key>ProcessType</key>";
        var keyIndex = plistXml.IndexOf(keyTag, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        var stringStart = plistXml.IndexOf("<string>", keyIndex + keyTag.Length, StringComparison.Ordinal);
        if (stringStart < 0)
        {
            return null;
        }
        stringStart += "<string>".Length;

        var stringEnd = plistXml.IndexOf("</string>", stringStart, StringComparison.Ordinal);
        if (stringEnd < 0)
        {
            return null;
        }

        return plistXml[stringStart..stringEnd];
    }

    private static async Task<bool> WaitForSocketAsync(string socketPath, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (File.Exists(socketPath))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private static Task<ProcessRunner.Result> RunExternalAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct) =>
        ProcessRunner.RunAsync(fileName, arguments, timeout, ct: ct);

    private static async Task<string> GetUidAsync(ExternalCommandRunner run, CancellationToken ct)
    {
        var envUid = Environment.GetEnvironmentVariable("UID");
        if (!string.IsNullOrWhiteSpace(envUid))
        {
            return envUid.Trim();
        }

        var result = await run("id", ["-u"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            var uid = result.StdOut.Trim();
            if (uid.Length > 0)
            {
                return uid;
            }
        }

        throw new InvalidOperationException("Unable to determine the current user id (UID env var not set and `id -u` failed).");
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static void AppendKey(StringBuilder sb, string key) => sb.Append($"\t<key>{key}</key>\n");

    private static void AppendString(StringBuilder sb, string value) => sb.Append($"\t<string>{Escape(value)}</string>\n");

    private static void AppendArrayString(StringBuilder sb, string value) => sb.Append($"\t\t<string>{Escape(value)}</string>\n");

    private static void Log(TextWriter log, string message) => log.WriteLine(message);

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
