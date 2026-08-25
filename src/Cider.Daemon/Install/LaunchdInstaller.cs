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

            var bootout = await ProcessRunner.RunAsync("launchctl", ["bootout", target], TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
            steps.Add($"launchctl bootout {target} (exit {bootout.ExitCode})");
            Log(log, steps[^1]);

            var bootstrap = await ProcessRunner.RunAsync("launchctl", ["bootstrap", $"gui/{uid}", plistPath], TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
            steps.Add($"launchctl bootstrap gui/{uid} {plistPath} (exit {bootstrap.ExitCode})");
            Log(log, steps[^1]);
            if (!bootstrap.Succeeded)
            {
                var failMessage = $"launchctl bootstrap failed: {bootstrap.StdErr.Trim()}";
                steps.Add(failMessage);
                return new InstallResult(false, failMessage, steps);
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
            else
            {
                message = message + "\n" + SystemSocketLink.Instructions(options.SocketPath);
            }

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
