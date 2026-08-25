using System.Net;

namespace Cider.Daemon.Install;

/// <summary>
/// Optionally installs a macOS <c>pf</c> anchor rule so that <c>host.docker.internal</c> also
/// reaches host services bound to <c>127.0.0.1</c> only, not just <c>0.0.0.0</c>-bound ones.
/// Apple solves the same problem for its own DNS with an <c>rdr</c> rule redirecting the vmnet
/// gateway address to <c>127.0.0.1</c> in an anchor named <c>com.apple.container</c>
/// (<c>PacketFilter.swift:36-64</c>, cited in <c>docs/spikes/xpc/03-limitations-audit-1.3.md</c>);
/// this copies that recipe under cider's own anchor name rather than calling Apple at all.
///
/// Needs root, so this never runs unprompted: it is only ever invoked from an explicit opt-in
/// (<c>cider install --host-loopback</c> or <c>cider host-loopback enable</c>), always through
/// non-interactive <c>sudo -n</c>, and it prints the exact commands to run by hand whenever that
/// would need a password. The rule does not survive a reboot (pf state resets), so a daemon that
/// was left enabled reinstalls it — best effort, silently on failure — each time it starts.
/// </summary>
public static partial class PfRedirect
{
    /// <summary>
    /// The pf anchor cider's rule lives under. Distinct from Apple's own <c>com.apple.container</c>
    /// anchor so enabling this never touches, races or gets torn down by Apple's DNS pf rule.
    /// </summary>
    public const string AnchorName = "com.chillicream.cider.hostloopback";

    /// <summary>Where the anchor's rule file is written; mirrors where Apple writes its own.</summary>
    public const string AnchorFilePath = "/etc/pf.anchors/" + AnchorName;

    /// <summary>Marker file recording that the feature is opted in, so a daemon restart reinstalls it.</summary>
    public const string StateFileName = "host-loopback.enabled";

    /// <summary>&lt;dataDir&gt;/host-loopback.enabled</summary>
    public static string StatePath(string dataDir) => Path.Combine(dataDir, StateFileName);

    /// <summary>
    /// Whether <c>host-loopback enable</c> was last run for <paramref name="dataDir"/> (and not
    /// disabled since). Read at daemon start to decide whether to reinstall the anchor.
    /// </summary>
    public static bool IsEnabled(string dataDir)
    {
        try
        {
            return File.Exists(StatePath(dataDir));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Records that <c>host-loopback</c> is opted in for <paramref name="dataDir"/>.</summary>
    public static async Task MarkEnabledAsync(string dataDir, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(
            StatePath(dataDir),
            $"enabled at {DateTimeOffset.UtcNow:O}\n",
            ct).ConfigureAwait(false);
    }

    /// <summary>Clears the opt-in marker so a future daemon start stops reinstalling the anchor.</summary>
    public static void MarkDisabled(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        try
        {
            File.Delete(StatePath(dataDir));
        }
        catch (IOException)
        {
            // best effort; disable still proceeds with tearing down the anchor itself.
        }
    }

    /// <summary>
    /// Builds the one-line anchor rule redirecting traffic to <paramref name="gatewayIp"/> from
    /// <paramref name="subnetCidr"/> to loopback, on every port. Throws
    /// <see cref="ArgumentException"/> when either is not a syntactically valid CIDR / IPv4 address —
    /// this never writes a rule pf itself would reject.
    /// </summary>
    public static string BuildRule(string subnetCidr, string gatewayIp)
    {
        ArgumentException.ThrowIfNullOrEmpty(subnetCidr);
        ArgumentException.ThrowIfNullOrEmpty(gatewayIp);

        if (!IPNetwork.TryParse(subnetCidr, out _))
        {
            throw new ArgumentException($"'{subnetCidr}' is not a valid CIDR subnet.", nameof(subnetCidr));
        }

        if (!IPAddress.TryParse(gatewayIp, out _))
        {
            throw new ArgumentException($"'{gatewayIp}' is not a valid IP address.", nameof(gatewayIp));
        }

        return $"rdr inet from {subnetCidr} to {gatewayIp} -> 127.0.0.1\n";
    }

    /// <summary>The caveats every caller printing instructions or a result should show alongside them.</summary>
    public static string Caveats =>
        "This needs admin (root) and, per Apple's own docs for the identical trick, disables\n" +
        "Private Relay while the rule is loaded. pf state does not survive a reboot, so `cider serve`\n" +
        "reinstalls the rule itself (best effort, silently) on every start while enabled — reboot still\n" +
        "leaves a brief gap until the daemon comes back up. Only the default bridge network's subnet is\n" +
        "covered; a container on a different user-created network is unaffected.";

    /// <summary>
    /// Human-readable commands that install the anchor by hand, for when non-interactive
    /// <c>sudo</c> is not available.
    /// </summary>
    public static string Instructions(
        string subnetCidr,
        string gatewayIp,
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName) => string.Join(
        '\n',
        $"To make host.docker.internal reach 127.0.0.1-bound host services, run:",
        "",
        $"    echo 'rdr inet from {subnetCidr} to {gatewayIp} -> 127.0.0.1' | sudo tee {anchorFilePath} >/dev/null",
        $"    sudo pfctl -e 2>/dev/null; sudo pfctl -a {anchorName} -f {anchorFilePath}",
        "",
        Caveats,
        "",
        $"To undo: sudo pfctl -a {anchorName} -F all && sudo rm -f {anchorFilePath}");

    /// <summary>Human-readable commands that remove the anchor by hand.</summary>
    public static string DisableInstructions(
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName) => string.Join(
        '\n',
        "To remove the host-loopback pf redirect, run:",
        "",
        $"    sudo pfctl -a {anchorName} -F all",
        $"    sudo rm -f {anchorFilePath}");

    /// <summary>
    /// Writes and loads the anchor rule via non-interactive <c>sudo</c>. Never prompts for a
    /// password; on failure this leaves pf untouched and returns <see cref="Instructions"/> for the
    /// caller to show. The rule is idempotent to reapply, so this is also what a daemon start calls
    /// to reinstall a rule pf may have dropped since (pf state does not survive a reboot).
    /// </summary>
    public static Task<InstallResult> TryEnableAsync(
        string subnetCidr,
        string gatewayIp,
        TextWriter log,
        CancellationToken ct,
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName) =>
        TryEnableCoreAsync(subnetCidr, gatewayIp, log, anchorFilePath, anchorName, SudoAsync, ct);

    /// <summary>
    /// Flushes and removes the anchor via non-interactive <c>sudo</c>. Never prompts for a
    /// password; on failure this returns <see cref="DisableInstructions"/> for the caller to show.
    /// </summary>
    public static Task<InstallResult> TryDisableAsync(
        TextWriter log,
        CancellationToken ct,
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName) =>
        TryDisableCoreAsync(log, anchorFilePath, anchorName, SudoAsync, ct);

    internal static async Task<InstallResult> TryEnableCoreAsync(
        string subnetCidr,
        string gatewayIp,
        TextWriter log,
        string anchorFilePath,
        string anchorName,
        PrivilegedCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var rule = BuildRule(subnetCidr, gatewayIp);
        var steps = new List<string>();

        // sudo needs a source file it can read; write the rule to an unprivileged temp file first
        // and have the privileged side only ever `cp`/`pfctl` it, never receive rule text on argv.
        var tmp = Path.Combine(Path.GetTempPath(), $"cider-pf-{Guid.NewGuid():N}.conf");
        try
        {
            await File.WriteAllTextAsync(tmp, rule, ct).ConfigureAwait(false);

            var copy = await runner(["cp", tmp, anchorFilePath], ct).ConfigureAwait(false);
            steps.Add($"{copy.Command} (exit {copy.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!copy.Succeeded)
            {
                var failure = $"Could not write {anchorFilePath} without an interactive password.";
                steps.Add(failure);
                return new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName), steps);
            }

            // Harmless if pf is already enabled (`pfctl -e` then just reports that and exits 1);
            // only the anchor load below decides success.
            var enable = await runner(["pfctl", "-e"], ct).ConfigureAwait(false);
            steps.Add($"{enable.Command} (exit {enable.ExitCode})");
            log.WriteLine(steps[^1]);

            var load = await runner(["pfctl", "-a", anchorName, "-f", anchorFilePath], ct).ConfigureAwait(false);
            steps.Add($"{load.Command} (exit {load.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!load.Succeeded)
            {
                var failure = $"Could not load the {anchorName} pf anchor without an interactive password.";
                steps.Add(failure);
                return new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName), steps);
            }

            var success = $"Loaded pf anchor {anchorName}: {subnetCidr} -> {gatewayIp} now redirects to 127.0.0.1.";
            steps.Add(success);
            return new InstallResult(true, success, steps);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // best-effort temp file cleanup.
            }
        }
    }

    internal static async Task<InstallResult> TryDisableCoreAsync(
        TextWriter log,
        string anchorFilePath,
        string anchorName,
        PrivilegedCommandRunner runner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var steps = new List<string>();

        // Flush only cider's own anchor — never `pfctl -d`, which would disable pf globally and
        // could drop rules that have nothing to do with cider.
        var flush = await runner(["pfctl", "-a", anchorName, "-F", "all"], ct).ConfigureAwait(false);
        steps.Add($"{flush.Command} (exit {flush.ExitCode})");
        log.WriteLine(steps[^1]);

        var remove = await runner(["rm", "-f", anchorFilePath], ct).ConfigureAwait(false);
        steps.Add($"{remove.Command} (exit {remove.ExitCode})");
        log.WriteLine(steps[^1]);

        if (!flush.Succeeded && !remove.Succeeded)
        {
            var failure = "Could not remove the pf anchor without an interactive password.";
            steps.Add(failure);
            return new InstallResult(false, DisableInstructions(anchorFilePath, anchorName), steps);
        }

        var success = $"Removed pf anchor {anchorName}.";
        steps.Add(success);
        return new InstallResult(true, success, steps);
    }

    /// <summary>Outcome of one privileged command, plus the command line as it should be logged.</summary>
    internal readonly record struct PrivilegedCommandResult(string Command, int ExitCode, string StdErr, bool Succeeded);

    /// <summary>
    /// Runs a privileged command (argv, e.g. <c>["pfctl", "-a", name, "-F", "all"]</c>). Production
    /// uses non-interactive sudo; tests substitute a runner that never touches the real pf state.
    /// </summary>
    internal delegate Task<PrivilegedCommandResult> PrivilegedCommandRunner(IReadOnlyList<string> argv, CancellationToken ct);

    internal static async Task<PrivilegedCommandResult> SudoAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        List<string> args = ["-n", .. argv];
        var result = await ProcessRunner.RunAsync("sudo", args, TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        return new PrivilegedCommandResult($"sudo {string.Join(' ', args)}", result.ExitCode, result.StdErr, result.Succeeded);
    }
}
