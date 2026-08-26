using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// cider-ede.24 orchestrator scope correction: the default transport is XPC, so the CLI transport's
/// dangling-content tolerance (<see cref="AppleContainerRuntimeDanglingContentTests"/>) is not enough
/// on its own — a single unresolvable <c>contentGet</c> digest must not fail the whole
/// <see cref="XpcContainerRuntime.ListImagesAsync"/> call either, and an operator must see the same
/// Warning either way. Drives <see cref="XpcContainerRuntime"/> through its test-only constructor
/// (<c>ImagesServiceClient</c> injection seam) against a fake whose <c>contentGet</c> throws for one
/// digest, with no live apiserver connection involved at all.
/// </summary>
public sealed class XpcContainerRuntimeListImagesToleranceTests
{
    [Fact]
    public async Task ListImagesAsync_DoesNotThrow_WhenOneDigestFailsToResolve_AndLogsExactlyOneWarningNamingIt()
    {
        using var tempDir = new TempDir();
        var goodIndexPath = tempDir.WriteIndex("good");

        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/good:latest", RepeatDigest('1')),
            Description("docker.io/library/bad:latest", RepeatDigest('2')),
        };

        var goodDigest = RepeatDigest('1');
        var badDigest = RepeatDigest('2');
        var fakeClient = new FakeImagesServiceClient(descriptions, badDigest, resolvable: new() { [goodDigest] = goodIndexPath });
        var logger = new RecordingLogger<XpcContainerRuntime>();
        var runtime = NewRuntime(fakeClient, logger);
        using var _ = runtime;

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        // The good row still comes back even though the bad row's index blob could never be resolved
        // (ToRuntimeImage tolerates an empty variants list the same way a store-miss manifest does).
        Assert.Equal(2, images.Count);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(badDigest, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(goodDigest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("container image prune", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live symptom (cider-ede.24) is not a thrown <see cref="XpcException"/> — it is
    /// <c>contentGet</c> answering <c>null</c> (the <c>notFound</c> path
    /// <see cref="ImagesServiceClient.ContentGetAsync"/> already swallows itself) or a path to a blob
    /// file that no longer exists on disk. Both must still surface exactly one Warning naming the bad
    /// digest, the same as the thrown-exception case above.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListImagesAsync_DoesNotThrow_WhenContentGetReturnsNothingRecovered_AndLogsExactlyOneWarningNamingIt(
        bool pathToMissingFile)
    {
        using var tempDir = new TempDir();
        var goodIndexPath = tempDir.WriteIndex("good");

        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/good:latest", RepeatDigest('1')),
            Description("docker.io/library/bad:latest", RepeatDigest('2')),
        };

        var goodDigest = RepeatDigest('1');
        var badDigest = RepeatDigest('2');

        // notFound: ContentGetAsync answers null. Missing-on-disk: ContentGetAsync answers a path,
        // but nothing lives there anymore — both are the "nothing recovered" shapes finding 1 covers.
        var badPath = pathToMissingFile ? tempDir.MissingFilePath() : null;
        var fakeClient = new FakeImagesServiceClient(
            descriptions,
            failingDigest: null,
            resolvable: new() { [goodDigest] = goodIndexPath, [badDigest] = badPath });
        var logger = new RecordingLogger<XpcContainerRuntime>();
        var runtime = NewRuntime(fakeClient, logger);
        using var _ = runtime;

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        Assert.Equal(2, images.Count);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(badDigest, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(goodDigest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("container image prune", warning.Message, StringComparison.Ordinal);
    }

    private static XpcContainerRuntime NewRuntime(ImagesServiceClient imagesClient, ILogger<XpcContainerRuntime> logger)
    {
        var options = new AppleContainerOptions();
        var apiserver = new XpcClient("com.apple.container.test.apiserver", NullLogger.Instance);
        var images = new XpcClient("com.apple.container.test.images", NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        return new XpcContainerRuntime(
            new FakeContainerRuntime(), apiserver, images, capabilities, options, logger, imagesClient);
    }

    private static string RepeatDigest(char c) => "sha256:" + new string(c, 64);

    private static ImageDescription Description(string reference, string digest) => new()
    {
        Reference = reference,
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = digest, Size = 374 },
    };

    /// <summary>A per-test temp directory holding a minimal, real, resolvable OCI index blob so a
    /// "good" digest can be genuinely read rather than defaulting to the same "nothing recovered"
    /// outcome the bad digest exercises.</summary>
    private sealed class TempDir : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("cider-ede24-tests-").FullName;

        public string WriteIndex(string name)
        {
            var path = Path.Combine(_dir, $"{name}.json");
            File.WriteAllText(
                path,
                """{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}""");
            return path;
        }

        public string MissingFilePath() => Path.Combine(_dir, "does-not-exist.json");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Overrides only the two routes <see cref="XpcContainerRuntime.ListImagesAsync"/> needs
    /// (the seam <see cref="ImagesServiceClient"/> was made non-<c>sealed</c>/<c>virtual</c> for).
    /// <paramref name="failingDigest"/>, when given, makes <see cref="ContentGetAsync"/> throw a
    /// genuine, non-<c>notFound</c>, non-<c>Unavailable</c> <see cref="XpcException"/> for that digest
    /// only (the thrown-exception shape). Every other digest resolves through
    /// <paramref name="resolvable"/> — a path with real content on disk, a path to a file that no
    /// longer exists, or <c>null</c> (the <c>notFound</c>-swallowed shape
    /// <see cref="ImagesServiceClient.ContentGetAsync"/> already handles itself) — falling back to
    /// <c>null</c> for any digest not listed there at all.</summary>
    private sealed class FakeImagesServiceClient(
        List<ImageDescription> descriptions, string? failingDigest, Dictionary<string, string?> resolvable)
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        public override Task<List<ImageDescription>> ImageListAsync(CancellationToken ct) =>
            Task.FromResult(descriptions);

        public override Task<string?> ContentGetAsync(string digest, CancellationToken ct)
        {
            if (failingDigest is not null && string.Equals(digest, failingDigest, StringComparison.Ordinal))
            {
                throw XpcException.ApiServer("internalError", $"content with digest {digest}: simulated dangling reference");
            }

            // No real local content store in this test beyond what `resolvable` stages; anything not
            // listed there resolves to "nothing on disk", the same best-effort miss
            // LocalBlobReader.TryReadAsync already tolerates.
            return Task.FromResult(resolvable.GetValueOrDefault(digest));
        }
    }

    /// <summary>Captures every log entry made against it — the seam
    /// <see cref="AppleContainerRuntimeDanglingContentTests"/> also uses to assert the CLI transport's
    /// matching Warning.</summary>
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
