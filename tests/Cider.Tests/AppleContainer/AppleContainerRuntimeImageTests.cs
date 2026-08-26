using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// The adapter's own image logic, driven through the <see cref="ContainerCli"/> seam instead of a
/// real <c>container</c> binary: the first-phase pull buffering that keeps a missing manifest an
/// HTTP 404, and the rule that the adapter reports raw CLI output only — never
/// the Docker-shaped terminal lines <c>ImageManager</c> owns (ARCHITECTURE §9).
/// </summary>
public sealed class AppleContainerRuntimeImageTests
{
    private const string ManifestLine =
        "#6 exporting manifest list sha256:611305aa6efdbc4c0bbd5a5e0451b715cf7fd0e342b635a2ed1ed3758e0eb3b5 done";

    /// <summary>A pull whose manifest never resolves prints only first-step lines, then fails.</summary>
    private static readonly string[] FirstPhaseOnly =
    [
        "[1/2] Fetching image [0s]",
        "[1/2] Fetching image [1s]",
    ];

    private static readonly string[] FullPull =
    [
        "[1/2] Fetching image [0s]",
        "[1/2] Fetching image 12% (20 of 56 blobs, 3.6/28.3 MB, 4 KB/s) [10s]",
        "[2/2] Unpacking image [12s]",
    ];

    [Fact]
    public async Task PullImageAsync_DiscardsFirstPhaseProgress_WhenTheManifestLookupFails()
    {
        var (runtime, cli) = CreateRuntime(FirstPhaseOnly, new CliResult(1, "", "failed to resolve reference: not found"));
        var events = new List<ProgressEvent>();

        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => runtime.PullImageAsync("docker.io/library/alpine:nope", null, null, new CollectingProgress(events), CancellationToken.None));

        Assert.Equal(RuntimeErrorKind.NotFound, ex.Kind);

        // Nothing may reach the caller: the very first reported event starts the NDJSON response and
        // costs the client the 404 it should have got.
        Assert.Empty(events);
        Assert.Contains("pull", string.Join(' ', cli.LastArgs!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullImageAsync_FlushesTheBufferedFirstPhaseInOrder_OnceThePullIsUnderWay()
    {
        var (runtime, _) = CreateRuntime(FullPull, new CliResult(0, "", ""));
        var events = new List<ProgressEvent>();

        await runtime.PullImageAsync("docker.io/library/alpine:3.22", null, null, new CollectingProgress(events), CancellationToken.None);

        Assert.Equal(3, events.Count);
        Assert.Equal("Fetching image", events[0].Status);
        Assert.Equal("1/2", events[0].Id);
        Assert.Equal(20, events[1].Current);
        Assert.Equal(56, events[1].Total);
        Assert.Equal("Unpacking image", events[2].Status);
    }

    [Fact]
    public async Task PullImageAsync_FlushesHeldBackProgress_WhenTheCliSucceedsWithoutEverGoingUnderWay()
    {
        var (runtime, _) = CreateRuntime(FirstPhaseOnly, new CliResult(0, "", ""));
        var events = new List<ProgressEvent>();

        await runtime.PullImageAsync("docker.io/library/alpine:3.22", null, null, new CollectingProgress(events), CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("Fetching image", e.Status));
    }

    [Fact]
    public async Task PullImageAsync_ReportsNoSyntheticStatusLine()
    {
        // ImageManager emits "Status: Downloaded newer image for …"/"Status: Image is up to date
        // for …"; an adapter line of the same shape made a successful pull print both.
        var (runtime, _) = CreateRuntime(FullPull, new CliResult(0, "", ""));
        var events = new List<ProgressEvent>();

        await runtime.PullImageAsync("docker.io/library/alpine:3.22", null, null, new CollectingProgress(events), CancellationToken.None);

        Assert.DoesNotContain(events, e => e.Status is not null && e.Status.StartsWith("Status:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Status is not null && e.Status.StartsWith("Pulling from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildImageAsync_ReportsRawOutputOnly_NeverTheDockerShapedTerminalLines()
    {
        // "Successfully built"/"Successfully tagged" are ImageManager's; the adapter emitting them
        // too produced them twice — and leaked the synthetic tag of an untagged build.
        var (runtime, _) = CreateRuntime(["#1 [internal] load build definition", ManifestLine], new CliResult(0, "", ""));
        var events = new List<ProgressEvent>();

        var id = await runtime.BuildImageAsync(
            new BuildSpec { ContextDir = Path.GetTempPath() },
            new CollectingProgress(events),
            CancellationToken.None);

        // ScriptedContainerCli.RunAsync above is deliberately left unscripted (always "not scripted"),
        // so InspectImageAsync inside BuildImageAsync can never resolve the content-addressed id here
        // -- this asserts the documented best-effort fallback to the scraped manifest digest, not the
        // inspect-preferred id a real build reports. That path is covered by
        // AppleContainerRuntimeContentAddressedIdTests.BuildImageAsync_ReportsTheContentAddressedConfigId_NotTheScrapedManifestDigest.
        Assert.Equal("sha256:611305aa6efdbc4c0bbd5a5e0451b715cf7fd0e342b635a2ed1ed3758e0eb3b5", id);
        Assert.DoesNotContain(events, e => e.Stream is not null && e.Stream.StartsWith("Successfully", StringComparison.Ordinal));
        Assert.Equal(2, events.Count);
    }

    private static (AppleContainerRuntime Runtime, ScriptedContainerCli Cli) CreateRuntime(
        IReadOnlyList<string> lines,
        CliResult result)
    {
        var options = new AppleContainerOptions();
        var cli = new ScriptedContainerCli(options, lines, result);
        return (new AppleContainerRuntime(options, NullLogger<AppleContainerRuntime>.Instance, cli), cli);
    }

    /// <summary>A <see cref="ContainerCli"/> that replays canned CLI output instead of spawning one.</summary>
    private sealed class ScriptedContainerCli(AppleContainerOptions options, IReadOnlyList<string> lines, CliResult result)
        : ContainerCli(options, NullLogger.Instance)
    {
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public override Task<CliResult> RunStreamingAsync(
            IReadOnlyList<string> args,
            Action<string, bool> onLine,
            CancellationToken ct,
            TimeSpan? timeout = null)
        {
            LastArgs = args;
            foreach (var line in lines)
            {
                onLine(line, false);
            }

            return Task.FromResult(result);
        }

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            LastArgs = args;
            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }

    private sealed class CollectingProgress(List<ProgressEvent> sink) : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value) => sink.Add(value);
    }
}
