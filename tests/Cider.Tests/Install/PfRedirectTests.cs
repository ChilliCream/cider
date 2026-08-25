using Cider.Daemon.Install;
using Xunit;

namespace Cider.Tests.Install;

/// <summary>
/// Covers rule generation, pf.conf line insertion/removal, instruction text and the enable/disable
/// flow against a fully fake privileged-command runner. Nothing here shells out to <c>pfctl</c> or
/// <c>sudo</c> — the fake runner below simulates every step in plain C#, and the pf.conf path itself
/// is redirected to a fake file under a temp dir, so these tests never touch the machine's real pf
/// state or its real <c>/etc/pf.conf</c> (see the `PfRedirect.PrivilegedCommandRunner` seam
/// SystemSocketLink already uses the same way for its own privileged calls).
/// </summary>
public class PfRedirectTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cider-pfredirect-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stand-in for /etc/pf.anchors/&lt;anchor&gt;.</summary>
    private string AnchorFile => Path.Combine(_root, "anchor.conf");

    /// <summary>Stand-in for /etc/pf.conf.</summary>
    private string PfConfFile => Path.Combine(_root, "pf.conf");

    /// <summary>The stock content macOS ships in /etc/pf.conf, for the anchor registration tests.</summary>
    private const string StockPfConf =
        "#\n" +
        "# Default PF configuration file.\n" +
        "#\n" +
        "# See pf.conf(5) for syntax.\n" +
        "#\n" +
        "\n" +
        "#\n" +
        "# com.apple anchor point\n" +
        "#\n" +
        "scrub-anchor \"com.apple/*\"\n" +
        "nat-anchor \"com.apple/*\"\n" +
        "rdr-anchor \"com.apple/*\"\n" +
        "dummynet-anchor \"com.apple/*\"\n" +
        "anchor \"com.apple/*\"\n" +
        "load anchor \"com.apple\" from \"/etc/pf.anchors/com.apple\"\n";

    private string DataDir => Path.Combine(_root, "data");

    public PfRedirectTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }

        GC.SuppressFinalize(this);
    }

    // Simulates `sudo -n <argv>` entirely in-process: `cp`/`rm` are performed for real against the
    // fake anchor/pf.conf paths under _root (never /etc), and `pfctl` calls never spawn the real
    // binary — they just report the canned outcome the test asked for.
    private static PfRedirect.PrivilegedCommandRunner FakeRunner(
        bool copySucceeds = true,
        bool validateSucceeds = true,
        bool reloadSucceeds = true,
        bool flushSucceeds = true,
        bool removeSucceeds = true,
        bool pfReportsEnabled = true) =>
        (argv, _) =>
        {
            bool succeeded;
            var stdOut = "";
            switch (argv[0])
            {
                case "cp":
                    succeeded = copySucceeds;
                    if (succeeded)
                    {
                        File.Copy(argv[1], argv[2], overwrite: true);
                    }

                    break;
                case "pfctl" when argv.Contains("-F"):
                    succeeded = flushSucceeds;
                    break;
                case "pfctl" when argv.Contains("-n") && argv.Contains("-f"):
                    // `pfctl -n -f <tmp path>`: validates the rewritten pf.conf before it is ever
                    // copied over the real path.
                    succeeded = validateSucceeds;
                    break;
                case "pfctl" when argv.Contains("-f"):
                    // `pfctl -f <pfConfPath>`: reloads the real ruleset.
                    succeeded = reloadSucceeds;
                    break;
                case "pfctl" when argv.Contains("-s") && argv.Contains("info"):
                    succeeded = true;
                    stdOut = pfReportsEnabled ? "Status: Enabled for 0 days 00:00:00\n" : "Status: Disabled\n";
                    break;
                case "pfctl":
                    // `pfctl -E`/`-X`: harmless either way, neither core method gates on them.
                    succeeded = true;
                    break;
                case "rm":
                    succeeded = removeSucceeds;
                    var path = argv[^1];
                    if (succeeded && File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    break;
                default:
                    succeeded = true;
                    break;
            }

            var result = new PfRedirect.PrivilegedCommandResult(
                "sudo -n " + string.Join(' ', argv),
                succeeded ? 0 : 1,
                stdOut,
                succeeded ? "" : "sudo: a password is required\n",
                succeeded);
            return Task.FromResult(result);
        };

    // ---- BuildRule ----

    [Fact]
    public void BuildRule_FormatsTheAppleStyleRdrRule()
    {
        var rule = PfRedirect.BuildRule("192.168.64.0/24", "192.168.64.1");

        Assert.Equal("rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n", rule);
    }

    [Theory]
    [InlineData("not-a-subnet", "192.168.64.1")]
    [InlineData("192.168.64.0/24", "not-an-ip")]
    [InlineData("", "192.168.64.1")]
    [InlineData("192.168.64.0/24", "")]
    public void BuildRule_RejectsInvalidInput(string subnet, string gateway)
    {
        Assert.ThrowsAny<ArgumentException>(() => PfRedirect.BuildRule(subnet, gateway));
    }

    [Fact]
    public void BuildRule_RejectsAHostAddressWithoutAPrefix()
    {
        // A bare IP is not a CIDR subnet; pf's `from` needs one or the rule would not parse.
        Assert.Throws<ArgumentException>(() => PfRedirect.BuildRule("192.168.64.1", "192.168.64.1"));
    }

    // ---- InsertAnchorLines / RemoveAnchorLines ----

    [Fact]
    public void InsertAnchorLines_AddsAllThreeLinesInPfConfKeywordOrder()
    {
        var updated = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        var lines = updated.Split('\n');
        var rdrIndex = Array.IndexOf(lines, $"rdr-anchor \"{PfRedirect.AnchorName}\"");
        var anchorIndex = Array.IndexOf(lines, $"anchor \"{PfRedirect.AnchorName}\"");
        var loadIndex = Array.IndexOf(lines, $"load anchor \"{PfRedirect.AnchorName}\" from \"{PfRedirect.AnchorFilePath}\"");

        Assert.True(rdrIndex >= 0, "rdr-anchor line missing");
        Assert.True(anchorIndex >= 0, "anchor line missing");
        Assert.True(loadIndex >= 0, "load anchor line missing");

        // Must not disturb Apple's own stanza, and must respect the fixed keyword order relative to
        // it: rdr-anchor after com.apple's rdr-anchor but before its dummynet-anchor; anchor after
        // com.apple's anchor but before any load anchor; load anchor after com.apple's load anchor.
        var appleRdrIndex = Array.IndexOf(lines, "rdr-anchor \"com.apple/*\"");
        var appleDummynetIndex = Array.IndexOf(lines, "dummynet-anchor \"com.apple/*\"");
        var appleAnchorIndex = Array.IndexOf(lines, "anchor \"com.apple/*\"");
        var appleLoadIndex = Array.IndexOf(lines, "load anchor \"com.apple\" from \"/etc/pf.anchors/com.apple\"");

        Assert.True(appleRdrIndex < rdrIndex && rdrIndex < appleDummynetIndex);
        Assert.True(appleAnchorIndex < anchorIndex && anchorIndex < loadIndex);
        Assert.True(appleLoadIndex < loadIndex);
        Assert.True(rdrIndex < anchorIndex);
    }

    [Fact]
    public void InsertAnchorLines_IsIdempotent()
    {
        var once = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);
        var twice = PfRedirect.InsertAnchorLines(once, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void InsertAnchorLines_OnAFileWithNoExistingAnchorStanza_AppendsAtTheEnd()
    {
        // A body with real filter/nat rules but no `*-anchor`/`anchor`/`load anchor` lines at all —
        // the three inserted lines must land after all of it, in order, never ahead of `set skip`/
        // `scrub`/`block` (pf.conf(5) requires anchor points to come after those).
        const string bare = "#\n# minimal pf.conf\n#\nset skip on lo0\nscrub in all\nblock in all\n";

        var updated = PfRedirect.InsertAnchorLines(bare, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        Assert.StartsWith(bare, updated, StringComparison.Ordinal);
        Assert.EndsWith(
            $"rdr-anchor \"{PfRedirect.AnchorName}\"\n" +
            $"anchor \"{PfRedirect.AnchorName}\"\n" +
            $"load anchor \"{PfRedirect.AnchorName}\" from \"{PfRedirect.AnchorFilePath}\"\n",
            updated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAnchorLines_RestoresTheOriginalContent()
    {
        var updated = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);
        var removed = PfRedirect.RemoveAnchorLines(updated, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        Assert.Equal(StockPfConf, removed);
    }

    [Fact]
    public void RemoveAnchorLines_IsIdempotent()
    {
        var once = PfRedirect.RemoveAnchorLines(StockPfConf, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);
        var twice = PfRedirect.RemoveAnchorLines(once, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        Assert.Equal(StockPfConf, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void RemoveAnchorLines_NeverTouchesAppleSOwnAnchorLines()
    {
        var updated = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);
        var removed = PfRedirect.RemoveAnchorLines(updated, PfRedirect.AnchorName, PfRedirect.AnchorFilePath);

        Assert.Contains("rdr-anchor \"com.apple/*\"", removed, StringComparison.Ordinal);
        Assert.Contains("anchor \"com.apple/*\"", removed, StringComparison.Ordinal);
        Assert.Contains("load anchor \"com.apple\" from \"/etc/pf.anchors/com.apple\"", removed, StringComparison.Ordinal);
    }

    // ---- Instructions ----

    [Fact]
    public void Instructions_ContainsTheAnchorFileAndPfConfRegistrationAndReloadCommands()
    {
        var instructions = PfRedirect.Instructions("192.168.64.0/24", "192.168.64.1");

        Assert.Contains("rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo tee {PfRedirect.AnchorFilePath}", instructions, StringComparison.Ordinal);
        Assert.Contains($"rdr-anchor \"%s\"", instructions, StringComparison.Ordinal);
        Assert.Contains($"'{PfRedirect.AnchorName}'", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo tee -a {PfRedirect.PfConfPath}", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo pfctl -n -f {PfRedirect.PfConfPath}", instructions, StringComparison.Ordinal);
        Assert.Contains("sudo pfctl -E", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo pfctl -f {PfRedirect.PfConfPath}", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_WarnsAboutAdminAndPrivateRelayAndReboot()
    {
        var instructions = PfRedirect.Instructions("192.168.64.0/24", "192.168.64.1");

        Assert.Contains("admin", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Private Relay", instructions, StringComparison.Ordinal);
        Assert.Contains("reboot", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisableInstructions_ContainsTheFlushPfConfCleanupReloadAndRemoveCommands()
    {
        var instructions = PfRedirect.DisableInstructions();

        Assert.Contains($"sudo pfctl -a {PfRedirect.AnchorName} -F all", instructions, StringComparison.Ordinal);
        Assert.Contains($"{PfRedirect.PfConfPath}", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo pfctl -f {PfRedirect.PfConfPath}", instructions, StringComparison.Ordinal);
        Assert.Contains("sudo pfctl -X", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo rm -f {PfRedirect.AnchorFilePath}", instructions, StringComparison.Ordinal);
    }

    // ---- TryEnableCoreAsync ----

    [Fact]
    public async Task TryEnableCoreAsync_WritesTheRuleAndRegistersAndReloadsTheAnchor_OnSuccess()
    {
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner()(argv, ct);
        };

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            recording,
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        Assert.Equal("rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n", await File.ReadAllTextAsync(AnchorFile));

        // The anchor must actually be reachable from the main ruleset — not just written to disk.
        var pfConf = await File.ReadAllTextAsync(PfConfFile);
        Assert.Contains($"rdr-anchor \"{PfRedirect.AnchorName}\"", pfConf, StringComparison.Ordinal);
        Assert.Contains($"anchor \"{PfRedirect.AnchorName}\"", pfConf, StringComparison.Ordinal);
        Assert.Contains($"load anchor \"{PfRedirect.AnchorName}\" from \"{AnchorFile}\"", pfConf, StringComparison.Ordinal);

        // Exact argv order: cp the anchor file, validate the rewritten pf.conf at its own (non-real)
        // temp path — never pfConfPath itself — only THEN cp it over pfConfPath, take the `-E`
        // reference, and finally reload the real ruleset.
        Assert.Collection(
            seen,
            argv => Assert.True(argv[0] == "cp" && argv[^1] == AnchorFile),
            argv => Assert.True(argv[0] == "pfctl" && argv.Contains("-n") && argv.Contains("-f") && argv[^1] != PfConfFile),
            argv => Assert.True(argv[0] == "cp" && argv[^1] == PfConfFile),
            argv => Assert.True(argv.SequenceEqual(["pfctl", "-E"])),
            argv => Assert.True(argv.SequenceEqual(["pfctl", "-f", PfConfFile])));

        Assert.Contains(result.Steps, s => s == $"sudo -n pfctl -f {PfConfFile} (exit 0)");
    }

    [Fact]
    public async Task TryEnableCoreAsync_IsIdempotent_DoesNotRewritePfConfWhenAlreadyRegistered()
    {
        var alreadyRegistered = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, AnchorFile);
        await File.WriteAllTextAsync(PfConfFile, alreadyRegistered);

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(),
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        // No `cp` targeting pf.conf itself, since its content did not need to change.
        Assert.DoesNotContain(result.Steps, s => s.Contains($"cp ", StringComparison.Ordinal) && s.Contains(PfConfFile, StringComparison.Ordinal));
        // Still reloads, so a changed anchor rule is always picked up.
        Assert.Contains(result.Steps, s => s == $"sudo -n pfctl -f {PfConfFile} (exit 0)");
    }

    [Fact]
    public async Task TryEnableCoreAsync_ReturnsInstructions_WhenCopyNeedsAPassword()
    {
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(copySucceeds: false),
            CancellationToken.None,
            PfConfFile);

        Assert.False(result.Success);
        Assert.False(File.Exists(AnchorFile));
        Assert.Contains("sudo tee", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryEnableCoreAsync_ReturnsInstructions_WhenThePfConfReloadNeedsAPassword()
    {
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(reloadSucceeds: false),
            CancellationToken.None,
            PfConfFile);

        Assert.False(result.Success);
        Assert.Contains($"sudo pfctl -n -f {PfConfFile}", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryEnableCoreAsync_ReturnsInstructionsAndNeverCopiesPfConf_WhenValidationFails()
    {
        // Validation runs against the unprivileged temp copy BEFORE pf.conf is ever touched, so a
        // syntax error there must never leave a `cp` targeting the real pf.conf path.
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner(validateSucceeds: false)(argv, ct);
        };

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            recording,
            CancellationToken.None,
            PfConfFile);

        Assert.False(result.Success);
        Assert.DoesNotContain(seen, argv => argv[0] == "cp" && argv[^1] == PfConfFile);
        Assert.Equal(StockPfConf, await File.ReadAllTextAsync(PfConfFile));
    }

    [Fact]
    public async Task TryEnableCoreAsync_DoesNotTakeTheEnableReference_WhenPfConfAlreadyRegisteredAndPfReportsEnabled()
    {
        var alreadyRegistered = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, AnchorFile);
        await File.WriteAllTextAsync(PfConfFile, alreadyRegistered);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner(pfReportsEnabled: true)(argv, ct);
        };

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            recording,
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        // pf.conf did not need changing and pf already reports enabled, so a previous enable must
        // already hold the reference disable's `-X` will release — taking a second one here would
        // leak it.
        Assert.DoesNotContain(seen, argv => argv.SequenceEqual(["pfctl", "-E"]));
        Assert.Contains(seen, argv => argv.SequenceEqual(["pfctl", "-s", "info"]));
    }

    [Fact]
    public async Task TryEnableCoreAsync_RetakesTheEnableReference_WhenPfConfAlreadyRegisteredButPfReportsDisabled()
    {
        // Models the post-reboot case: pf.conf still has the lines (nothing removed them), but the
        // reboot itself reset pf's own enable refcount to zero.
        var alreadyRegistered = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, AnchorFile);
        await File.WriteAllTextAsync(PfConfFile, alreadyRegistered);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner(pfReportsEnabled: false)(argv, ct);
        };

        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            recording,
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        Assert.Contains(seen, argv => argv.SequenceEqual(["pfctl", "-E"]));
    }

    [Fact]
    public async Task TryEnableCoreAsync_NeverLeavesTheUnprivilegedTempRuleFileBehind()
    {
        // Captures the unprivileged temp paths the real (net10/net11, run-in-parallel) `cp` steps
        // would read from, rather than diffing a directory listing shared with other concurrently
        // running test processes writing into the same OS temp dir.
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var capturedTmpPaths = new List<string>();
        PfRedirect.PrivilegedCommandRunner capturing = (argv, ct) =>
        {
            if (argv[0] == "cp")
            {
                capturedTmpPaths.Add(argv[1]);
            }

            return FakeRunner()(argv, ct);
        };

        await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            capturing,
            CancellationToken.None,
            PfConfFile);

        Assert.NotEmpty(capturedTmpPaths);
        Assert.All(capturedTmpPaths, p => Assert.False(File.Exists(p)));
    }

    // ---- TryDisableCoreAsync ----

    [Fact]
    public async Task TryDisableCoreAsync_FlushesTheAnchorUnregistersFromPfConfAndRemovesTheFile_OnSuccess()
    {
        await File.WriteAllTextAsync(AnchorFile, "rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n");
        var registered = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, AnchorFile);
        await File.WriteAllTextAsync(PfConfFile, registered);

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(),
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        Assert.False(File.Exists(AnchorFile));

        var pfConf = await File.ReadAllTextAsync(PfConfFile);
        Assert.Equal(StockPfConf, pfConf);
    }

    [Fact]
    public async Task TryDisableCoreAsync_NeverCallsPfctlDisable_OnlyFlushesItsOwnAnchor()
    {
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner()(argv, ct);
        };

        await PfRedirect.TryDisableCoreAsync(new StringWriter(), AnchorFile, PfRedirect.AnchorName, recording, CancellationToken.None, PfConfFile);

        // `pfctl -d` disables pf globally; disabling host-loopback must only ever flush cider's
        // own named anchor (`-a <name> -F all`) and release its own `-E` reference via `-X`, never
        // the global switch.
        Assert.DoesNotContain(seen, argv => argv[0] == "pfctl" && argv.Contains("-d"));
        Assert.Contains(seen, argv => argv.SequenceEqual(["pfctl", "-a", PfRedirect.AnchorName, "-F", "all"]));
        Assert.Contains(seen, argv => argv.SequenceEqual(["pfctl", "-X"]));
    }

    [Fact]
    public async Task TryDisableCoreAsync_Fails_WhenFlushFails_EvenIfRemoveSucceeds()
    {
        await File.WriteAllTextAsync(AnchorFile, "stale");
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(flushSucceeds: false, removeSucceeds: true),
            CancellationToken.None,
            PfConfFile);

        Assert.False(result.Success);
        Assert.Contains("sudo pfctl -a", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryDisableCoreAsync_Succeeds_OnlyWhenFlushSucceeds_RegardlessOfRemove()
    {
        await File.WriteAllTextAsync(AnchorFile, "stale");
        await File.WriteAllTextAsync(PfConfFile, StockPfConf);

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(flushSucceeds: true, removeSucceeds: false),
            CancellationToken.None,
            PfConfFile);

        Assert.True(result.Success);
        // `rm -f` failing on its own does not fail disable — flush is the load-bearing step.
        Assert.True(File.Exists(AnchorFile));
    }

    [Fact]
    public async Task TryDisableCoreAsync_NeverRemovesTheAnchorFile_WhenThePfConfCopyFails()
    {
        // pf.conf still needs the lines removed (it has them), but the privileged `cp` that would
        // rewrite it fails — the anchor file must be kept, since pf.conf's `load anchor ... from`
        // line still points at it, and the call must report failure so callers never mark this
        // disabled (see Program.cs's `if (result.Success) MarkDisabled(...)`).
        await File.WriteAllTextAsync(AnchorFile, "rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n");
        var registered = PfRedirect.InsertAnchorLines(StockPfConf, PfRedirect.AnchorName, AnchorFile);
        await File.WriteAllTextAsync(PfConfFile, registered);

        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner(copySucceeds: false)(argv, ct);
        };

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            recording,
            CancellationToken.None,
            PfConfFile);

        Assert.False(result.Success);
        Assert.DoesNotContain(seen, argv => argv[0] == "rm");
        Assert.True(File.Exists(AnchorFile));
        Assert.Equal(registered, await File.ReadAllTextAsync(PfConfFile));
    }

    // ---- opt-in state marker ----

    [Fact]
    public void IsEnabled_IsFalse_UntilMarkEnabledAsyncRuns()
    {
        Assert.False(PfRedirect.IsEnabled(DataDir));
    }

    [Fact]
    public async Task MarkEnabledAsync_ThenMarkDisabled_RoundTrips()
    {
        await PfRedirect.MarkEnabledAsync(DataDir, CancellationToken.None);
        Assert.True(PfRedirect.IsEnabled(DataDir));

        PfRedirect.MarkDisabled(DataDir);
        Assert.False(PfRedirect.IsEnabled(DataDir));
    }

    [Fact]
    public void MarkDisabled_OnANeverEnabledDataDir_DoesNotThrow()
    {
        PfRedirect.MarkDisabled(DataDir);
        Assert.False(PfRedirect.IsEnabled(DataDir));
    }
}
