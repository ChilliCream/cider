using System.Runtime.CompilerServices;
using Xunit;

namespace Cider.Tests.Install;

/// <summary>
/// Pins the fix for cider-xij: the <c>SystemSocketLink.Instructions</c> block must reach the user
/// exactly once per `cider install` run.
///
/// This is deliberately a source-level guard rather than a behavioural test. The only way to observe
/// the real emission is to run `cider install`, which writes the user's LaunchAgents plist, bootstraps
/// a launchd job and repoints their docker context — a user-owned action tests must not take (cider-fpt).
/// <c>LaunchdInstaller.InstallAsync</c> has no seam that lets the message be built without doing all of
/// that, so what is pinned instead is the single fact the duplication reduced to: how many call sites
/// emit the block, and which one owns it.
///
/// Program.cs is the owner — it prints the block gated on `--system-socket` not being passed. If a
/// second emission is ever reintroduced (in LaunchdInstaller or anywhere else that feeds
/// `InstallResult.Message`), these tests fail.
/// </summary>
public class SystemSocketInstructionsEmissionTests
{
    private const string EmissionCall = "SystemSocketLink.Instructions(";

    [Fact]
    public void LaunchdInstaller_DoesNotEmitTheSystemSocketInstructions()
    {
        var source = File.ReadAllText(DaemonSourcePath("Install/LaunchdInstaller.cs"));

        Assert.Equal(0, Occurrences(source, EmissionCall));
    }

    [Fact]
    public void Program_EmitsTheSystemSocketInstructionsExactlyOnce()
    {
        var source = File.ReadAllText(DaemonSourcePath("Program.cs"));

        Assert.Equal(1, Occurrences(source, EmissionCall));
    }

    [Fact]
    public void Program_GatesTheOnlyEmissionOnSystemSocketNotHavingBeenRequested()
    {
        var source = File.ReadAllText(DaemonSourcePath("Program.cs"));

        var guard = source.IndexOf("if (!installOptions.SystemSocketSymlink)", StringComparison.Ordinal);
        var emission = source.IndexOf(EmissionCall, StringComparison.Ordinal);

        Assert.True(guard >= 0, "the `--system-socket` guard around the instructions block is gone");
        Assert.True(emission > guard, "the instructions are no longer printed under the `--system-socket` guard");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // Resolves against the repo checkout rather than the build output: the sources under test are not
    // copied next to the test assembly, and CallerFilePath survives any output-directory layout.
    private static string DaemonSourcePath(string relative)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ThisFile(), "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "src", "Cider.Daemon", relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"expected daemon source at {path}");
        return path;
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
