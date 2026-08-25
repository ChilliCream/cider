using Cider.Daemon.Install;
using Xunit;

namespace Cider.Tests.Install;

/// <summary>
/// Covers rule generation, instruction text and the enable/disable flow against a fully fake
/// privileged-command runner. Nothing here shells out to <c>pfctl</c> or <c>sudo</c> — the fake
/// runner below simulates every step in plain C#, so these tests never touch the machine's real pf
/// state (see the `PfRedirect.PrivilegedCommandRunner` seam SystemSocketLink already uses the same
/// way for its own privileged calls).
/// </summary>
public class PfRedirectTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cider-pfredirect-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stand-in for /etc/pf.anchors/&lt;anchor&gt;.</summary>
    private string AnchorFile => Path.Combine(_root, "anchor.conf");

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
    // fake anchor path under _root (never /etc), and `pfctl` calls never spawn the real binary —
    // they just report the canned outcome the test asked for.
    private static PfRedirect.PrivilegedCommandRunner FakeRunner(
        bool copySucceeds = true,
        bool loadSucceeds = true,
        bool flushSucceeds = true,
        bool removeSucceeds = true) =>
        (argv, _) =>
        {
            bool succeeded;
            switch (argv[0])
            {
                case "cp":
                    succeeded = copySucceeds;
                    if (succeeded)
                    {
                        File.Copy(argv[1], argv[2], overwrite: true);
                    }

                    break;
                case "pfctl" when argv.Contains("-f"):
                    succeeded = loadSucceeds;
                    break;
                case "pfctl" when argv.Contains("-F"):
                    succeeded = flushSucceeds;
                    break;
                case "pfctl":
                    // `pfctl -e`: harmless either way, TryEnableCoreAsync does not gate on it.
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

    // ---- Instructions ----

    [Fact]
    public void Instructions_ContainsTheAnchorFileAndNonInteractiveSudoCommands()
    {
        var instructions = PfRedirect.Instructions("192.168.64.0/24", "192.168.64.1");

        Assert.Contains("rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo tee {PfRedirect.AnchorFilePath}", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo pfctl -a {PfRedirect.AnchorName} -f {PfRedirect.AnchorFilePath}", instructions, StringComparison.Ordinal);
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
    public void DisableInstructions_ContainsTheFlushAndRemoveCommands()
    {
        var instructions = PfRedirect.DisableInstructions();

        Assert.Contains($"sudo pfctl -a {PfRedirect.AnchorName} -F all", instructions, StringComparison.Ordinal);
        Assert.Contains($"sudo rm -f {PfRedirect.AnchorFilePath}", instructions, StringComparison.Ordinal);
    }

    // ---- TryEnableCoreAsync ----

    [Fact]
    public async Task TryEnableCoreAsync_WritesTheRuleAndLoadsTheAnchor_OnSuccess()
    {
        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n", await File.ReadAllTextAsync(AnchorFile));
        Assert.Contains(result.Steps, s => s.StartsWith("sudo -n cp ", StringComparison.Ordinal));
        Assert.Contains(result.Steps, s => s.StartsWith($"sudo -n pfctl -a {PfRedirect.AnchorName} -f {AnchorFile}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryEnableCoreAsync_ReturnsInstructions_WhenCopyNeedsAPassword()
    {
        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(copySucceeds: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(AnchorFile));
        Assert.Contains("sudo tee", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryEnableCoreAsync_ReturnsInstructions_WhenTheAnchorLoadNeedsAPassword()
    {
        var result = await PfRedirect.TryEnableCoreAsync(
            "192.168.64.0/24",
            "192.168.64.1",
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(loadSucceeds: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains($"sudo pfctl -a {PfRedirect.AnchorName} -f {AnchorFile}", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryEnableCoreAsync_NeverLeavesTheUnprivilegedTempRuleFileBehind()
    {
        // Captures the unprivileged temp path the real (net10/net11, run-in-parallel) `cp` step
        // would read from, rather than diffing a directory listing shared with other concurrently
        // running test processes writing into the same OS temp dir.
        string? capturedTmpPath = null;
        PfRedirect.PrivilegedCommandRunner capturing = (argv, ct) =>
        {
            if (argv[0] == "cp")
            {
                capturedTmpPath = argv[1];
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
            CancellationToken.None);

        Assert.NotNull(capturedTmpPath);
        Assert.False(File.Exists(capturedTmpPath));
    }

    // ---- TryDisableCoreAsync ----

    [Fact]
    public async Task TryDisableCoreAsync_FlushesTheAnchorAndRemovesTheFile_OnSuccess()
    {
        await File.WriteAllTextAsync(AnchorFile, "rdr inet from 192.168.64.0/24 to 192.168.64.1 -> 127.0.0.1\n");

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(AnchorFile));
    }

    [Fact]
    public async Task TryDisableCoreAsync_NeverCallsPfctlDisable_OnlyFlushesItsOwnAnchor()
    {
        var seen = new List<IReadOnlyList<string>>();
        PfRedirect.PrivilegedCommandRunner recording = (argv, ct) =>
        {
            seen.Add(argv);
            return FakeRunner()(argv, ct);
        };

        await PfRedirect.TryDisableCoreAsync(new StringWriter(), AnchorFile, PfRedirect.AnchorName, recording, CancellationToken.None);

        // `pfctl -d` disables pf globally; disabling host-loopback must only ever flush cider's
        // own named anchor (`-a <name> -F all`), never the global switch.
        Assert.DoesNotContain(seen, argv => argv[0] == "pfctl" && argv.Contains("-d"));
        Assert.Contains(seen, argv => argv.SequenceEqual(["pfctl", "-a", PfRedirect.AnchorName, "-F", "all"]));
    }

    [Fact]
    public async Task TryDisableCoreAsync_ReturnsInstructions_WhenNeitherStepSucceeds()
    {
        await File.WriteAllTextAsync(AnchorFile, "stale");

        var result = await PfRedirect.TryDisableCoreAsync(
            new StringWriter(),
            AnchorFile,
            PfRedirect.AnchorName,
            FakeRunner(flushSucceeds: false, removeSucceeds: false),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(File.Exists(AnchorFile));
        Assert.Contains("sudo rm -f", result.Message, StringComparison.Ordinal);
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
