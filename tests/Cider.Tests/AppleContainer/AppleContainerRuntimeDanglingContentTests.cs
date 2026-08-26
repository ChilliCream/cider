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
/// (<see cref="AppleContainerRuntimeImageTests"/>'s own pattern) to prove the planner's ruling on this
/// task (comment 66) instead: a failed listing that still printed parseable rows is
/// enumerated-with-skips (200 with what is enumerable, one Warning), while a failed listing that
/// printed nothing salvageable is a total failure (throws, after the same Warning) — never a
/// synthesized empty success either way. It also proves that <see
/// cref="AppleContainerRuntime.LoadImagesAsync"/> still identifies a load correctly even while the
/// store is in that state (correction verifier finding 1: the before/after diff it used to lean on
/// can now throw right alongside the poisoned listing, and must be tolerated rather than propagated).
/// </summary>
public sealed class AppleContainerRuntimeDanglingContentTests
{
    private const string DanglingDigest = "sha256:6baf43584bcb78f2e5847d1de515f23499913ac9f12bdf834811a3145eb11ca1";
    private const string DanglingStderr = "Error: content with digest " + DanglingDigest;

    /// <summary>One minimal <c>image ls</c> row, parseable on its own, used to prove a dangling-content
    /// failure that still printed *some* rows on stdout is "enumerated-with-skips", not a total
    /// failure (planner ruling, comment 66).</summary>
    private const string OneParseableRowJson =
        """[{"id":"aaa","configuration":{"name":"docker.io/library/foo:latest"},"variants":[{"platform":{"architecture":"amd64","os":"linux"},"digest":"sha256:bbb","size":10}]}]""";

    /// <summary>The same image as <see cref="OneParseableRowJson"/>, used as the "after" listing in the
    /// normalize-before-dedupe test — same id, so it is the fully-qualified spelling of the same
    /// reference <c>foo:latest</c> resolves to once normalized.</summary>
    private const string FullyQualifiedRowJson = OneParseableRowJson;

    [Fact]
    public async Task ListImagesAsync_ReturnsEnumeratedRows_WhenTheFailedCallStillPrintedParseableOutput()
    {
        // Planner ruling (cider-ede.24, comment 66): a dangling-content failure that still leaves one
        // or more parseable rows on stdout is "enumerated-with-skips" — a 200 with what is enumerable,
        // not a synthesized empty success and not a throw.
        var cli = new ScriptedCli(new CliResult(1, OneParseableRowJson, DanglingStderr));
        var logger = new RecordingLogger<AppleContainerRuntime>();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), logger, cli);

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        var image = Assert.Single(images);
        Assert.Contains("docker.io/library/foo:latest", image.References);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(DanglingDigest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("container image prune", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListImagesAsync_Throws_WhenTheFailedCallPrintedNothingParseable()
    {
        // Planner ruling (cider-ede.24, comment 66): "never synthesize an empty success out of a
        // failure" — a dangling-content failure that leaves nothing to salvage is a TOTAL failure and
        // must throw (after logging exactly one Warning), not return an empty 200 that a caller cannot
        // tell apart from a genuinely empty store.
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr));
        var logger = new RecordingLogger<AppleContainerRuntime>();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), logger, cli);

        var ex = await Assert.ThrowsAsync<Cider.Core.Runtime.RuntimeException>(
            () => runtime.ListImagesAsync(CancellationToken.None));

        // "content with digest ..." matches none of CliErrorMapper's other markers, so it classifies
        // as Internal (falls through Classify's marker chain) — asserting the kind, not just that some
        // exception was thrown, pins that the thrown exception is the same classified error a genuine
        // failure would produce.
        Assert.Equal(Cider.Core.Runtime.RuntimeErrorKind.Internal, ex.Kind);

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
        // — a TOTAL failure (dangling stderr, nothing parseable on stdout) on *both* the pre-load and
        // post-load listing, so ListImagesAsync now genuinely throws each time. Fix item 2 pins that
        // LoadImagesAsync must tolerate that throw (catch, log Debug, treat as an empty secondary
        // source) rather than let it propagate and fail an `image load` that Apple itself reported as
        // successful. Apple's own stdout echo of what it loaded must be enough on its own.
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr))
        {
            ImageLoadResult = new CliResult(0, "Loaded image: foo:latest\n", ""),
        };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Equal(["foo:latest"], loaded);
    }

    [Fact]
    public async Task LoadImagesAsync_DropsAPrefixedLine_WhenTheRemainderDoesNotParseAsAReference()
    {
        // Closes the untested half of the restored validation gate: a line carrying Apple's
        // `Loaded image:` prefix still must not be trusted blindly — the text after the prefix has to
        // parse as a real ImageReference. A malformed remainder (here, just ':') must be dropped, not
        // trusted, exactly like an unprefixed noise line already is.
        var cli = new ScriptedCli(new CliResult(0, "[]", ""))
        {
            ImageLoadResult = new CliResult(0, "Loaded image: :\n", ""),
        };
        var logger = new RecordingLogger<AppleContainerRuntime>();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), logger, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Empty(loaded);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning &&
                 e.Message.Contains("no loaded reference could be identified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadImagesAsync_DoesNotDuplicate_WhenStdoutAndTheAfterListingNameTheSameImageDifferently()
    {
        // Fix item 5: the stdout-primary reference and the before/after listing diff must be deduped
        // on the *normalized* reference, not the raw string, or the same image comes back twice under
        // two spellings ('foo:latest' from stdout, 'docker.io/library/foo:latest' from the listing) —
        // exactly what a caller like BuildKit's ExportLoader or `docker load` must not see.
        var cli = new ScriptedCli(new CliResult(0, "[]", ""))
        {
            ImageLoadResult = new CliResult(0, "Loaded image: foo:latest\n", ""),
            ImageLsSequence = new Queue<CliResult>(
            [
                new CliResult(0, "[]", ""), // before: nothing yet
                new CliResult(0, FullyQualifiedRowJson, ""), // after: the same image, fully qualified
            ]),
        };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Equal(["foo:latest"], loaded);
    }

    [Fact]
    public async Task LoadImagesAsync_ReportsOnlyTheLoadedReference_WhenOnlyTheBeforeListingTotallyFails()
    {
        // Correction plan item 3 (asymmetric ordering at the fake-CLI seam): the *before* `image ls`
        // is a TOTAL failure (dangling stderr, nothing parseable on stdout) so
        // ListReferencesToleratingFailureAsync must come back null, not an empty set — an empty "before"
        // would make every entry in a healthy "after" listing look newly loaded (before.Contains would
        // be false for all of them), so the diff must be skipped entirely rather than run against a
        // synthesized empty snapshot. The *after* `image ls` then succeeds and lists several unrelated
        // images already present in the store. The result must be exactly the one reference Apple's own
        // stdout named as loaded — never the whole store.
        const string severalUnrelatedImagesJson =
            """
            [
              {"id":"aaa","configuration":{"name":"docker.io/library/foo:latest"},"variants":[{"platform":{"architecture":"amd64","os":"linux"},"digest":"sha256:bbb","size":10}]},
              {"id":"ccc","configuration":{"name":"docker.io/library/bar:latest"},"variants":[{"platform":{"architecture":"amd64","os":"linux"},"digest":"sha256:ddd","size":10}]},
              {"id":"eee","configuration":{"name":"docker.io/library/baz:latest"},"variants":[{"platform":{"architecture":"amd64","os":"linux"},"digest":"sha256:fff","size":10}]}
            ]
            """;
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr))
        {
            ImageLoadResult = new CliResult(0, "Loaded image: foo:latest\n", ""),
            ImageLsSequence = new Queue<CliResult>(
            [
                new CliResult(1, "", DanglingStderr), // before: total failure, nothing parseable
                new CliResult(0, severalUnrelatedImagesJson, ""), // after: several unrelated images
            ]),
        };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Equal(["foo:latest"], loaded);
    }

    [Fact]
    public async Task LoadImagesAsync_IgnoresStdoutNoiseThatDoesNotCarryTheLoadedImagePrefix()
    {
        // Review correction (cider-ede.24, MAJOR 1): a prior pass treated ANY non-empty stdout line as
        // a loaded reference, so unrelated CLI chatter (progress/copy lines) leaked in as phantom
        // "loaded" references. Only lines that actually carry Apple's `Loaded image:` prefix — and
        // parse as a real reference once it is stripped — may count.
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr))
        {
            ImageLoadResult = new CliResult(
                0,
                "Copying blob sha256:1234567890abcdef\nunpacking...\nLoaded image: foo:latest\ndone\n",
                ""),
        };
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Equal(["foo:latest"], loaded);
    }

    [Fact]
    public async Task LoadImagesAsync_DoesNotThrow_WhenASuccessfulLoadNamesNothing()
    {
        // Review correction (cider-ede.24, MAJOR 2 / planner ruling): a successful `image load` that
        // still can't be pinned to a reference must not be reported as a failed load — that turned
        // `docker load`/`commit`/`import` into a failure path for a call that genuinely succeeded on
        // Apple's side. It logs a Warning and returns an empty list instead of throwing.
        var cli = new ScriptedCli(new CliResult(0, "[]", ""))
        {
            ImageLoadResult = new CliResult(0, "unpacking...\ndone\n", ""),
        };
        var logger = new RecordingLogger<AppleContainerRuntime>();
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), logger, cli);

        await using var tar = new MemoryStream([1, 2, 3]);
        var loaded = await runtime.LoadImagesAsync(tar, CancellationToken.None);

        Assert.Empty(loaded);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning &&
                 e.Message.Contains("no loaded reference could be identified", StringComparison.Ordinal));
    }

    private sealed class ScriptedCli(CliResult imageLsResult) : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
        public CliResult? ImageLoadResult { get; set; }

        /// <summary>When set, each successive <c>image ls</c> call dequeues the next result instead of
        /// always returning the constructor's single <c>imageLsResult</c> — for a test where the
        /// before- and after-load listings need to differ (fix item 5's normalize-before-dedupe case).
        /// Once drained, the most recently dequeued result repeats for any further call.</summary>
        public Queue<CliResult>? ImageLsSequence { get; set; }

        private CliResult? _lastSequenced;

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            if (args.Count >= 2 && args[0] == "image" && (args[1] == "ls" || args[1] == "list"))
            {
                if (ImageLsSequence is { Count: > 0 } sequence)
                {
                    _lastSequenced = sequence.Dequeue();
                    return Task.FromResult(_lastSequenced);
                }

                return Task.FromResult(_lastSequenced ?? imageLsResult);
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
