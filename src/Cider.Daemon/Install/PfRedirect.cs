using System.Net;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// The main pf ruleset. Writing the anchor file alone is not enough for pf to ever evaluate it —
    /// the anchor also has to be declared and loaded from here, the same way Apple's own
    /// <c>addAnchorToConfig()</c> registers <c>com.apple.container</c> (<c>PacketFilter.swift:102-142</c>).
    /// </summary>
    public const string PfConfPath = "/etc/pf.conf";

    /// <summary>Marker file recording that the feature is opted in, so a daemon restart reinstalls it.</summary>
    public const string StateFileName = "host-loopback.enabled";

    /// <summary>&lt;dataDir&gt;/host-loopback.enabled</summary>
    public static string StatePath(string dataDir) => Path.Combine(dataDir, StateFileName);

    /// <summary>
    /// File recording the token <c>pfctl -E</c> printed for the enable reference this process is
    /// holding, so a later <c>disable</c> (possibly a different process invocation) can release the
    /// exact same reference with <c>pfctl -X &lt;token&gt;</c> rather than guessing.
    /// </summary>
    public const string PfTokenFileName = "host-loopback.pftoken";

    /// <summary>&lt;dataDir&gt;/host-loopback.pftoken</summary>
    public static string PfTokenPath(string dataDir) => Path.Combine(dataDir, PfTokenFileName);

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

    /// <summary>
    /// Matches the token line <c>pfctl -E</c> prints on macOS, e.g. <c>Token : 1234567890</c> — the
    /// value <c>pfctl -X &lt;token&gt;</c> needs later to release exactly this enable reference
    /// (<c>man pfctl</c>: <c>-X</c> takes the token <c>-E</c> reported, it is not optional).
    /// </summary>
    private static readonly Regex EnableTokenPattern = new(@"Token\s*:\s*(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the reference token from <c>pfctl -E</c>'s stdout, or <see langword="null"/> when
    /// none is present (empty output, unexpected format). Kept separate from the runner call so it
    /// is unit-testable against real macOS output without shelling out.
    /// </summary>
    internal static string? ParseEnableToken(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return null;
        }

        var match = EnableTokenPattern.Match(stdout);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Parses the token from a successful <c>pfctl -E</c> result and logs a step when <c>-E</c>
    /// succeeded but no token could be parsed — best effort: enable still succeeds either way, but a
    /// later disable will have nothing on record to release with <c>-X</c> and will skip it (never
    /// fabricates a token to send instead). Returns <see langword="null"/> when <c>-E</c> itself did
    /// not succeed.
    /// </summary>
    private static string? RecordEnableToken(PrivilegedCommandResult enable, List<string> steps, TextWriter log)
    {
        if (!enable.Succeeded)
        {
            return null;
        }

        var token = ParseEnableToken(enable.StdOut);
        if (token is null)
        {
            var noToken = "pfctl -E succeeded but no token could be parsed from its output; " +
                "a future disable will not be able to release this pf reference.";
            steps.Add(noToken);
            log.WriteLine(noToken);
        }

        return token;
    }

    internal static string RdrAnchorLine(string anchorName) => $"rdr-anchor \"{anchorName}\"";

    internal static string AnchorLine(string anchorName) => $"anchor \"{anchorName}\"";

    internal static string LoadAnchorLine(string anchorName, string anchorFilePath) =>
        $"load anchor \"{anchorName}\" from \"{anchorFilePath}\"";

    /// <summary>
    /// Rank of the pf.conf anchor-declaration keyword <paramref name="line"/> starts with, in the
    /// fixed relative order pf.conf(5) requires them in (<c>scrub-anchor</c>, <c>nat-anchor</c>,
    /// <c>rdr-anchor</c>, <c>dummynet-anchor</c>, <c>anchor</c>, <c>load anchor</c>) — or -1 when the
    /// line is none of these. <c>load anchor</c> is checked first since it would otherwise also match
    /// the plain <c>anchor</c> prefix.
    /// </summary>
    private static int AnchorLineRank(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("load anchor", StringComparison.Ordinal))
        {
            return 5;
        }

        if (trimmed.StartsWith("scrub-anchor", StringComparison.Ordinal))
        {
            return 0;
        }

        if (trimmed.StartsWith("nat-anchor", StringComparison.Ordinal))
        {
            return 1;
        }

        if (trimmed.StartsWith("rdr-anchor", StringComparison.Ordinal))
        {
            return 2;
        }

        if (trimmed.StartsWith("dummynet-anchor", StringComparison.Ordinal))
        {
            return 3;
        }

        if (trimmed.StartsWith("anchor", StringComparison.Ordinal))
        {
            return 4;
        }

        return -1;
    }

    /// <summary>The pf.conf(5) rule classes, in the fixed relative order they must appear in.</summary>
    private enum PfRuleClass
    {
        Options,
        Normalization,
        Queueing,
        Translation,
        Filtering,
    }

    /// <summary>
    /// Classifies an existing pf.conf line by the pf.conf(5) rule class its first keyword belongs
    /// to — used as a fallback for placing the anchor stanza when the file has no existing ranked
    /// anchor-declaration line to anchor off of (see <see cref="AnchorLineRank"/> and
    /// <see cref="FallbackInsertIndex"/>). Returns <see langword="null"/> for a line that is none
    /// of these (a comment, a blank line, or a keyword pf.conf(5) does not order relative to the
    /// anchor stanza).
    /// </summary>
    private static PfRuleClass? ClassifyRuleLine(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("set", StringComparison.Ordinal) || trimmed.StartsWith("include", StringComparison.Ordinal))
        {
            return PfRuleClass.Options;
        }

        if (trimmed.StartsWith("scrub", StringComparison.Ordinal))
        {
            return PfRuleClass.Normalization;
        }

        if (trimmed.StartsWith("altq", StringComparison.Ordinal) || trimmed.StartsWith("queue", StringComparison.Ordinal))
        {
            return PfRuleClass.Queueing;
        }

        if (trimmed.StartsWith("nat", StringComparison.Ordinal)
            || trimmed.StartsWith("rdr", StringComparison.Ordinal)
            || trimmed.StartsWith("binat", StringComparison.Ordinal))
        {
            return PfRuleClass.Translation;
        }

        if (trimmed.StartsWith("pass", StringComparison.Ordinal)
            || trimmed.StartsWith("block", StringComparison.Ordinal)
            || trimmed.StartsWith("match", StringComparison.Ordinal)
            || trimmed.StartsWith("antispoof", StringComparison.Ordinal)
            || trimmed.StartsWith("anchor", StringComparison.Ordinal))
        {
            return PfRuleClass.Filtering;
        }

        return null;
    }

    /// <summary>
    /// Where to place an anchor-stanza <paramref name="line"/> (of the given <paramref name="rank"/>,
    /// see <see cref="AnchorLineRank"/>) when <paramref name="lines"/> has no existing ranked
    /// anchor-declaration line to insert relative to — e.g. a pf.conf with real filter/nat rules but
    /// no <c>*-anchor</c>/<c>anchor</c>/<c>load anchor</c> lines at all. Appending at the end
    /// unconditionally (the previous behavior) can land <c>rdr-anchor</c>/<c>anchor</c> after
    /// filtering-class lines, which <c>pfctl -n -f</c> rejects ("Rules must be in order: options,
    /// normalization, queueing, translation, filtering") — so instead this places each line by
    /// pf.conf(5) rule class: <c>rdr-anchor</c> (translation-class) immediately before the first
    /// filtering-class line, <c>anchor</c> (itself filtering-class) after the last one, and
    /// <c>load anchor</c> always at the very end, since it is what actually loads the anchor file's
    /// rules and so must follow every other class.
    /// </summary>
    private static int FallbackInsertIndex(List<string> lines, int rank)
    {
        if (rank == 5)
        {
            return lines.Count;
        }

        var filteringIndexes = Enumerable.Range(0, lines.Count)
            .Where(i => ClassifyRuleLine(lines[i]) == PfRuleClass.Filtering)
            .ToList();

        if (filteringIndexes.Count == 0)
        {
            return lines.Count;
        }

        // rank 2 is rdr-anchor (translation-class): before the first filtering-class line.
        // rank 4 is anchor (filtering-class itself): after the last filtering-class line.
        return rank <= 2 ? filteringIndexes[0] : filteringIndexes[^1] + 1;
    }

    /// <summary>
    /// Returns <paramref name="pfConf"/> with the three lines that register <paramref name="anchorName"/>
    /// with the main ruleset (<c>rdr-anchor</c> / <c>anchor</c> / <c>load anchor</c>) inserted in the
    /// relative order pf.conf(5) requires, mirroring Apple's own <c>addAnchorToConfig()</c>
    /// (<c>PacketFilter.swift:102-142</c>). Idempotent: content that already has all three lines is
    /// returned unchanged.
    /// </summary>
    internal static string InsertAnchorLines(string pfConf, string anchorName, string anchorFilePath)
    {
        ArgumentNullException.ThrowIfNull(pfConf);
        ArgumentException.ThrowIfNullOrEmpty(anchorName);
        ArgumentException.ThrowIfNullOrEmpty(anchorFilePath);

        var (lines, trailingNewline) = SplitPfConfLines(pfConf);

        var toInsert = new[]
        {
            (Line: RdrAnchorLine(anchorName), Rank: 2),
            (Line: AnchorLine(anchorName), Rank: 4),
            (Line: LoadAnchorLine(anchorName, anchorFilePath), Rank: 5),
        };

        if (toInsert.All(t => lines.Contains(t.Line)))
        {
            return pfConf;
        }

        // Decided once, up front, from the file as it originally stood: whether there is any
        // existing ranked anchor-declaration line to insert our own three relative to at all. Once
        // decided this must not flip mid-loop — our own rdr-anchor line, inserted below, is itself a
        // ranked line (AnchorLineRank matches it), and if later insertions re-scanned for ranked
        // lines they would anchor off of *that* instead of falling through to the rule-class
        // fallback, landing `anchor` right after `rdr-anchor` instead of after the last
        // filtering-class line.
        var hasExistingRankedLine = lines.Any(l => AnchorLineRank(l) >= 0);

        foreach (var (line, rank) in toInsert)
        {
            if (lines.Contains(line))
            {
                continue;
            }

            int insertAt;
            if (hasExistingRankedLine)
            {
                // Insert right after the last existing line whose keyword rank is <= this one's, so
                // the relative keyword order pf.conf(5) requires is preserved regardless of what is
                // already in the file.
                int? lastMatch = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    var r = AnchorLineRank(lines[i]);
                    if (r >= 0 && r <= rank)
                    {
                        lastMatch = i;
                    }
                }

                insertAt = lastMatch is not null ? lastMatch.Value + 1 : lines.Count;
            }
            else
            {
                // No anchor-declaration line to anchor off of at all — place by pf.conf(5) rule
                // class instead (see FallbackInsertIndex), so any real filter/nat/etc. rules that
                // are present still end up in the order pfctl requires.
                insertAt = FallbackInsertIndex(lines, rank);
            }

            lines.Insert(insertAt, line);
        }

        return JoinPfConfLines(lines, trailingNewline);
    }

    /// <summary>
    /// Returns <paramref name="pfConf"/> with the three <paramref name="anchorName"/> lines (as added
    /// by <see cref="InsertAnchorLines"/>) removed, restoring what the file looked like before.
    /// Idempotent: content without them is returned unchanged.
    /// </summary>
    internal static string RemoveAnchorLines(string pfConf, string anchorName, string anchorFilePath)
    {
        ArgumentNullException.ThrowIfNull(pfConf);
        ArgumentException.ThrowIfNullOrEmpty(anchorName);
        ArgumentException.ThrowIfNullOrEmpty(anchorFilePath);

        var (lines, trailingNewline) = SplitPfConfLines(pfConf);

        var toRemove = new[]
        {
            RdrAnchorLine(anchorName),
            AnchorLine(anchorName),
            LoadAnchorLine(anchorName, anchorFilePath),
        };

        lines.RemoveAll(l => toRemove.Contains(l));

        return JoinPfConfLines(lines, trailingNewline);
    }

    private static (List<string> Lines, bool TrailingNewline) SplitPfConfLines(string pfConf)
    {
        var normalized = pfConf.Replace("\r\n", "\n", StringComparison.Ordinal);
        var trailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return (lines, trailingNewline);
    }

    private static string JoinPfConfLines(List<string> lines, bool trailingNewline)
    {
        var result = string.Join('\n', lines);
        return trailingNewline ? result + "\n" : result;
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
        string anchorName = AnchorName,
        string pfConfPath = PfConfPath) => string.Join(
        '\n',
        $"To make host.docker.internal reach 127.0.0.1-bound host services, run:",
        "",
        $"    echo 'rdr inet from {subnetCidr} to {gatewayIp} -> 127.0.0.1' | sudo tee {anchorFilePath} >/dev/null",
        $"    printf 'rdr-anchor \"%s\"\\nanchor \"%s\"\\nload anchor \"%s\" from \"%s\"\\n' '{anchorName}' '{anchorName}' '{anchorName}' '{anchorFilePath}' | sudo tee -a {pfConfPath} >/dev/null",
        $"    sudo pfctl -n -f {pfConfPath} && sudo pfctl -E && sudo pfctl -f {pfConfPath}",
        "",
        Caveats,
        "",
        "To undo:",
        $"    sudo pfctl -a {anchorName} -F all",
        $"    sudo sed -i '' -e '\\|^{RdrAnchorLine(anchorName)}$|d' -e '\\|^{AnchorLine(anchorName)}$|d' -e '\\|^{LoadAnchorLine(anchorName, anchorFilePath)}$|d' {pfConfPath}",
        $"    sudo pfctl -f {pfConfPath} && sudo pfctl -X <token from pfctl -E> && sudo rm -f {anchorFilePath}");

    /// <summary>Human-readable commands that remove the anchor by hand.</summary>
    public static string DisableInstructions(
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName,
        string pfConfPath = PfConfPath) => string.Join(
        '\n',
        "To remove the host-loopback pf redirect, run:",
        "",
        $"    sudo pfctl -a {anchorName} -F all",
        $"    sudo sed -i '' -e '\\|^{RdrAnchorLine(anchorName)}$|d' -e '\\|^{AnchorLine(anchorName)}$|d' -e '\\|^{LoadAnchorLine(anchorName, anchorFilePath)}$|d' {pfConfPath}",
        $"    sudo pfctl -f {pfConfPath}",
        $"    sudo pfctl -X <token from pfctl -E>",
        $"    sudo rm -f {anchorFilePath}");

    /// <summary>
    /// Writes and loads the anchor rule via non-interactive <c>sudo</c>. Never prompts for a
    /// password; on failure this leaves pf untouched and returns <see cref="Instructions"/> for the
    /// caller to show. The rule is idempotent to reapply, so this is also what a daemon start calls
    /// to reinstall a rule pf may have dropped since (pf state does not survive a reboot).
    ///
    /// When this call takes a fresh <c>pfctl -E</c> reference, the token it printed is recorded at
    /// <see cref="PfTokenPath"/> under <paramref name="dataDir"/>, so a later <see cref="TryDisableAsync"/>
    /// — possibly from a different process invocation — can release that exact reference with
    /// <c>pfctl -X &lt;token&gt;</c> (<c>-X</c> requires the token; see <see cref="ParseEnableToken"/>).
    /// </summary>
    public static async Task<InstallResult> TryEnableAsync(
        string subnetCidr,
        string gatewayIp,
        TextWriter log,
        string dataDir,
        CancellationToken ct,
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);

        var (result, enableToken) = await TryEnableCoreAsync(
            subnetCidr, gatewayIp, log, anchorFilePath, anchorName, SudoAsync, ct, PfConfPath).ConfigureAwait(false);

        if (result.Success && enableToken is not null)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                await File.WriteAllTextAsync(PfTokenPath(dataDir), enableToken, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Best effort: a missing token file just means a future disable will not find one to
                // release and will skip `-X` entirely rather than send it bare (see TryDisableAsync).
            }
        }

        return result;
    }

    /// <summary>
    /// Flushes and removes the anchor via non-interactive <c>sudo</c>. Never prompts for a
    /// password; on failure this returns <see cref="DisableInstructions"/> for the caller to show.
    /// Reads the token a prior <see cref="TryEnableAsync"/> recorded at <see cref="PfTokenPath"/>
    /// under <paramref name="dataDir"/> (if any) and releases exactly that reference with
    /// <c>pfctl -X &lt;token&gt;</c>; when there is none on record this never sends a bare <c>-X</c>.
    /// </summary>
    public static async Task<InstallResult> TryDisableAsync(
        TextWriter log,
        string dataDir,
        CancellationToken ct,
        string anchorFilePath = AnchorFilePath,
        string anchorName = AnchorName)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);

        string? enableToken = null;
        try
        {
            var tokenPath = PfTokenPath(dataDir);
            if (File.Exists(tokenPath))
            {
                var stored = (await File.ReadAllTextAsync(tokenPath, ct).ConfigureAwait(false)).Trim();
                enableToken = stored.Length > 0 ? stored : null;
            }
        }
        catch (IOException)
        {
            enableToken = null;
        }
        catch (UnauthorizedAccessException)
        {
            enableToken = null;
        }

        var result = await TryDisableCoreAsync(
            log, anchorFilePath, anchorName, SudoAsync, ct, PfConfPath, enableToken).ConfigureAwait(false);

        if (result.Success)
        {
            try
            {
                File.Delete(PfTokenPath(dataDir));
            }
            catch (IOException)
            {
                // best effort; matches MarkDisabled's own best-effort delete of the enabled marker.
            }
        }

        return result;
    }

    internal static async Task<(InstallResult Result, string? EnableToken)> TryEnableCoreAsync(
        string subnetCidr,
        string gatewayIp,
        TextWriter log,
        string anchorFilePath,
        string anchorName,
        PrivilegedCommandRunner runner,
        CancellationToken ct,
        string pfConfPath = PfConfPath)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var rule = BuildRule(subnetCidr, gatewayIp);
        var steps = new List<string>();
        string? enableToken = null;

        // sudo needs a source file it can read; write the rule (and, if it needs changing, pf.conf)
        // to unprivileged temp files first and have the privileged side only ever `cp`/`pfctl` them,
        // never receive their content on argv.
        var tmpAnchor = Path.Combine(Path.GetTempPath(), $"cider-pf-{Guid.NewGuid():N}.conf");
        var tmpPfConf = Path.Combine(Path.GetTempPath(), $"cider-pfconf-{Guid.NewGuid():N}.conf");
        try
        {
            await File.WriteAllTextAsync(tmpAnchor, rule, ct).ConfigureAwait(false);

            var copyAnchor = await runner(["cp", tmpAnchor, anchorFilePath], ct).ConfigureAwait(false);
            steps.Add($"{copyAnchor.Command} (exit {copyAnchor.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!copyAnchor.Succeeded)
            {
                var failure = $"Could not write {anchorFilePath} without an interactive password.";
                steps.Add(failure);
                return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
            }

            // Writing the anchor file is not enough: pf only ever evaluates it once it is declared
            // and loaded from the main ruleset (see the type's remarks and PfConfPath's doc comment).
            string currentPfConf;
            try
            {
                currentPfConf = await File.ReadAllTextAsync(pfConfPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failure = $"Could not read {pfConfPath} to register the {anchorName} anchor: {ex.Message}";
                steps.Add(failure);
                return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
            }

            var updatedPfConf = InsertAnchorLines(currentPfConf, anchorName, anchorFilePath);
            var pfConfChanged = !string.Equals(updatedPfConf, currentPfConf, StringComparison.Ordinal);
            if (pfConfChanged)
            {
                await File.WriteAllTextAsync(tmpPfConf, updatedPfConf, ct).ConfigureAwait(false);

                // Validate against the unprivileged temp copy BEFORE ever touching pfConfPath: the
                // anchor file is already copied in place at this point, so `-n -f` on the tmp file
                // still resolves the `load anchor ... from anchorFilePath` line, and a syntax error
                // is caught without ever leaving the real pf.conf in an invalid state.
                var validate = await runner(["pfctl", "-n", "-f", tmpPfConf], ct).ConfigureAwait(false);
                steps.Add($"{validate.Command} (exit {validate.ExitCode})");
                log.WriteLine(steps[^1]);
                if (!validate.Succeeded)
                {
                    var failure = $"{pfConfPath} would fail pfctl validation after registering the {anchorName} anchor; left {pfConfPath} untouched.";
                    steps.Add(failure);
                    return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
                }

                var copyPfConf = await runner(["cp", tmpPfConf, pfConfPath], ct).ConfigureAwait(false);
                steps.Add($"{copyPfConf.Command} (exit {copyPfConf.ExitCode})");
                log.WriteLine(steps[^1]);
                if (!copyPfConf.Succeeded)
                {
                    var failure = $"Could not register the {anchorName} anchor in {pfConfPath} without an interactive password.";
                    steps.Add(failure);
                    return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
                }
            }

            // Reference-counted per the /etc/pf.conf header ("each component ... responsible for
            // enabling and disabling PF via -E and -X ... PF is disabled only when the last enable
            // reference is released"). Only take the reference when this call actually changed
            // pf.conf: an unchanged file means a previous enable already holds the reference, and
            // disable releases exactly one with `-X` in the same call that removes the lines, so
            // taking a second one here would leak a reference nothing ever releases. The exception is
            // a reboot: pf.conf still has the lines (nothing removed them), but the reboot itself
            // reset pf's enable refcount to zero (Apple's own launchd job reloads the ruleset, not the
            // refcount) — detect that by asking pf directly and re-take the reference if it reports
            // disabled.
            if (pfConfChanged)
            {
                var enable = await runner(["pfctl", "-E"], ct).ConfigureAwait(false);
                steps.Add($"{enable.Command} (exit {enable.ExitCode})");
                log.WriteLine(steps[^1]);
                enableToken = RecordEnableToken(enable, steps, log);
            }
            else
            {
                var status = await runner(["pfctl", "-s", "info"], ct).ConfigureAwait(false);
                steps.Add($"{status.Command} (exit {status.ExitCode})");
                log.WriteLine(steps[^1]);
                if (!status.Succeeded)
                {
                    // Can't tell whether pf is actually enabled without this, and sudo is evidently
                    // unavailable anyway (the following `pfctl -f` would fail the same way) — fail
                    // now rather than guessing and possibly re-taking a reference that was never
                    // released, or skipping one that was needed.
                    var failure = $"Could not query pf status ({status.Command}) without an interactive password.";
                    steps.Add(failure);
                    return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
                }

                if (!status.StdOut.Contains("Status: Enabled", StringComparison.Ordinal))
                {
                    var enable = await runner(["pfctl", "-E"], ct).ConfigureAwait(false);
                    steps.Add($"{enable.Command} (exit {enable.ExitCode})");
                    log.WriteLine(steps[^1]);
                    enableToken = RecordEnableToken(enable, steps, log);
                }
            }

            // Reloading the main ruleset is what actually (re)loads the anchor file's rule content,
            // now that it is declared via the `load anchor` line above.
            var reload = await runner(["pfctl", "-f", pfConfPath], ct).ConfigureAwait(false);
            steps.Add($"{reload.Command} (exit {reload.ExitCode})");
            log.WriteLine(steps[^1]);
            if (!reload.Succeeded)
            {
                var failure = $"Could not reload {pfConfPath} (which loads the {anchorName} anchor) without an interactive password.";
                steps.Add(failure);

                if (pfConfChanged)
                {
                    // Belt-and-braces: validation at the temp path passed, but the reload against the
                    // real path still failed (e.g. lost the password prompt mid-flow) — restore
                    // pf.conf to what it was before this call rather than leaving the new lines in
                    // place with no anchor actually loaded. Best effort; a failure here is only logged,
                    // never turned into a second error the caller has to parse.
                    var rollbackTmp = Path.Combine(Path.GetTempPath(), $"cider-pfconf-rollback-{Guid.NewGuid():N}.conf");
                    try
                    {
                        await File.WriteAllTextAsync(rollbackTmp, currentPfConf, ct).ConfigureAwait(false);
                        var rollback = await runner(["cp", rollbackTmp, pfConfPath], ct).ConfigureAwait(false);
                        steps.Add($"{rollback.Command} (exit {rollback.ExitCode})");
                        log.WriteLine(steps[^1]);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        steps.Add($"Could not roll back {pfConfPath}: {ex.Message}");
                        log.WriteLine(steps[^1]);
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(rollbackTmp);
                        }
                        catch (IOException)
                        {
                            // best-effort temp file cleanup.
                        }
                    }
                }

                return (new InstallResult(false, Instructions(subnetCidr, gatewayIp, anchorFilePath, anchorName, pfConfPath), steps), enableToken);
            }

            var success = $"Loaded pf anchor {anchorName}: {subnetCidr} -> {gatewayIp} now redirects to 127.0.0.1.";
            steps.Add(success);
            return (new InstallResult(true, success, steps), enableToken);
        }
        finally
        {
            try
            {
                File.Delete(tmpAnchor);
            }
            catch (IOException)
            {
                // best-effort temp file cleanup.
            }

            try
            {
                File.Delete(tmpPfConf);
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
        CancellationToken ct,
        string pfConfPath = PfConfPath,
        string? enableToken = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(runner);

        var steps = new List<string>();

        // Flush only cider's own anchor — never `pfctl -d`, which would disable pf globally and
        // could drop rules that have nothing to do with cider. This is the load-bearing step: it is
        // what actually stops pf from evaluating the redirect, so failure here fails the whole call.
        var flush = await runner(["pfctl", "-a", anchorName, "-F", "all"], ct).ConfigureAwait(false);
        steps.Add($"{flush.Command} (exit {flush.ExitCode})");
        log.WriteLine(steps[^1]);

        var tmpPfConf = Path.Combine(Path.GetTempPath(), $"cider-pfconf-{Guid.NewGuid():N}.conf");
        try
        {
            string? currentPfConf = null;
            try
            {
                currentPfConf = await File.ReadAllTextAsync(pfConfPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                steps.Add($"Could not read {pfConfPath} to unregister the {anchorName} anchor: {ex.Message}");
                log.WriteLine(steps[^1]);
            }

            // Only true once pf.conf is confirmed to no longer reference the anchor file — either it
            // never did, or the rewritten copy actually landed at pfConfPath. Removing the anchor file
            // while pf.conf still has a `load anchor ... from anchorFilePath` line pointing at it would
            // leave pf.conf referencing a file that no longer exists.
            var pfConfLinesConfirmedAbsent = false;
            if (currentPfConf is not null)
            {
                var updatedPfConf = RemoveAnchorLines(currentPfConf, anchorName, anchorFilePath);
                if (string.Equals(updatedPfConf, currentPfConf, StringComparison.Ordinal))
                {
                    pfConfLinesConfirmedAbsent = true;
                }
                else
                {
                    await File.WriteAllTextAsync(tmpPfConf, updatedPfConf, ct).ConfigureAwait(false);

                    var copyPfConf = await runner(["cp", tmpPfConf, pfConfPath], ct).ConfigureAwait(false);
                    steps.Add($"{copyPfConf.Command} (exit {copyPfConf.ExitCode})");
                    log.WriteLine(steps[^1]);

                    if (copyPfConf.Succeeded)
                    {
                        pfConfLinesConfirmedAbsent = true;

                        var reload = await runner(["pfctl", "-f", pfConfPath], ct).ConfigureAwait(false);
                        steps.Add($"{reload.Command} (exit {reload.ExitCode})");
                        log.WriteLine(steps[^1]);
                    }
                }
            }

            // Symmetric with the `-E` reference taken on enable: releases only cider's own reference,
            // never forces pf off for other components relying on it (see /etc/pf.conf's own header).
            // `-X` takes the token `-E` printed (man pfctl: "-X token" is mandatory, not optional) —
            // never run it bare, and never release a reference this process has no record of ever
            // taking.
            bool disableRefSucceeded;
            if (enableToken is not null)
            {
                var disableRef = await runner(["pfctl", "-X", enableToken], ct).ConfigureAwait(false);
                steps.Add($"{disableRef.Command} (exit {disableRef.ExitCode})");
                log.WriteLine(steps[^1]);
                disableRefSucceeded = disableRef.Succeeded;
            }
            else
            {
                var skippedRef = "no enable token recorded; not releasing a pf reference";
                steps.Add(skippedRef);
                log.WriteLine(skippedRef);
                disableRefSucceeded = true;
            }

            if (pfConfLinesConfirmedAbsent)
            {
                var remove = await runner(["rm", "-f", anchorFilePath], ct).ConfigureAwait(false);
                steps.Add($"{remove.Command} (exit {remove.ExitCode})");
                log.WriteLine(steps[^1]);
            }
            else
            {
                var skipped = $"Kept {anchorFilePath}: could not confirm {pfConfPath} no longer references it.";
                steps.Add(skipped);
                log.WriteLine(skipped);
            }

            if (!flush.Succeeded || !pfConfLinesConfirmedAbsent || !disableRefSucceeded)
            {
                var failure = !flush.Succeeded
                    ? "Could not remove the pf anchor without an interactive password."
                    : !pfConfLinesConfirmedAbsent
                        ? $"Could not confirm {pfConfPath} no longer references the {anchorName} anchor without an interactive password; left {anchorFilePath} in place."
                        : "Could not release the pf enable reference (pfctl -X) without an interactive password.";
                steps.Add(failure);
                return new InstallResult(false, DisableInstructions(anchorFilePath, anchorName, pfConfPath), steps);
            }

            var success = $"Removed pf anchor {anchorName}.";
            steps.Add(success);
            return new InstallResult(true, success, steps);
        }
        finally
        {
            try
            {
                File.Delete(tmpPfConf);
            }
            catch (IOException)
            {
                // best-effort temp file cleanup.
            }
        }
    }

    /// <summary>Outcome of one privileged command, plus the command line as it should be logged.</summary>
    internal readonly record struct PrivilegedCommandResult(string Command, int ExitCode, string StdOut, string StdErr, bool Succeeded);

    /// <summary>
    /// Runs a privileged command (argv, e.g. <c>["pfctl", "-a", name, "-F", "all"]</c>). Production
    /// uses non-interactive sudo; tests substitute a runner that never touches the real pf state.
    /// </summary>
    internal delegate Task<PrivilegedCommandResult> PrivilegedCommandRunner(IReadOnlyList<string> argv, CancellationToken ct);

    internal static async Task<PrivilegedCommandResult> SudoAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        List<string> args = ["-n", .. argv];
        var result = await ProcessRunner.RunAsync("sudo", args, TimeSpan.FromSeconds(10), ct: ct).ConfigureAwait(false);
        return new PrivilegedCommandResult($"sudo {string.Join(' ', args)}", result.ExitCode, result.StdOut, result.StdErr, result.Succeeded);
    }
}
