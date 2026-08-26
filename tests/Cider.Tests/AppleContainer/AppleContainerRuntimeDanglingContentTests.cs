using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// cider-ede.24: <c>container image ls</c> fails hard when Apple's store holds even one dangling
/// content reference, even though every other entry is fine (verified live: <c>Error: content with
/// digest sha256:…</c>, the blob gone but <c>state.json</c> still naming it). Before this fix every
/// <see cref="AppleContainerRuntime.ListImagesAsync"/> call — and so <c>docker images</c> — 500'd
/// outright on a store in that state; this drives the fake-CLI seam
/// (<see cref="AppleContainerRuntimeImageTests"/>'s own pattern) to prove it now degrades instead, and
/// that <see cref="AppleContainerRuntime.LoadImagesAsync"/> still identifies a load correctly even
/// while the store is in that state (correction verifier finding 1: the before/after diff it used to
/// lean on collapses to empty right alongside the poisoned listing).
/// </summary>
public sealed class AppleContainerRuntimeDanglingContentTests
{
    private const string DanglingDigest = "sha256:6baf43584bcb78f2e5847d1de515f23499913ac9f12bdf834811a3145eb11ca1";
    private const string DanglingStderr = "Error: content with digest " + DanglingDigest;

    [Fact]
    public async Task ListImagesAsync_DoesNotThrow_WhenTheStoreReportsOneDanglingContentReference()
    {
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr));
        var logger = new RecordingLogger<AppleContainerRuntime>();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), logger, cli);

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        Assert.Empty(images);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(DanglingDigest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("container image prune", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListImagesAsync_StillThrows_ForAGenuineFailure()
    {
        var cli = new ScriptedCli(new CliResult(1, "", "Error: apiserver is not running"));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var ex = await Assert.ThrowsAsync<Cider.Core.Runtime.RuntimeException>(
            () => runtime.ListImagesAsync(CancellationToken.None));

        Assert.Equal(Cider.Core.Runtime.RuntimeErrorKind.Unavailable, ex.Kind);
    }

    [Fact]
    public async Task LoadImagesAsync_IdentifiesTheLoadFromStdout_WhenTheStoreIsPoisoned()
    {
        // `image ls` (the before/after diff LoadImagesAsync used to depend on) is poisoned throughout
        // — both the pre-load and post-load listing collapse to empty, exactly the "both sets empty"
        // case the correction plan calls out. Apple's own `image load` still succeeds and names what
        // it loaded on stdout, so that must be enough on its own.
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr))
        {
            ImageLoadResult = new CliResult(0, "Loaded image: foo:latest\n", ""),
        };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Equal(["foo:latest"], loaded);
    }

    private sealed class ScriptedCli(CliResult imageLsResult) : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
        public CliResult? ImageLoadResult { get; set; }

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            if (args.Count >= 2 && args[0] == "image" && (args[1] == "ls" || args[1] == "list"))
            {
                return Task.FromResult(imageLsResult);
            }

            if (args.Count >= 2 && args[0] == "image" && args[1] == "load" && ImageLoadResult is { } loadResult)
            {
                return Task.FromResult(loadResult);
            }

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }

    /// <summary>Captures every log entry made against it, so a test can assert exactly one Warning was
    /// logged (and what it said) instead of only that the call did not throw.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
