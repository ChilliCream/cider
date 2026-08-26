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
        var descriptions = new List<ImageDescription>
        {
            Description("docker.io/library/good:latest", RepeatDigest('1')),
            Description("docker.io/library/bad:latest", RepeatDigest('2')),
        };

        var badDigest = RepeatDigest('2');
        var fakeClient = new FakeImagesServiceClient(descriptions, badDigest);
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

    /// <summary>Overrides only the two routes <see cref="XpcContainerRuntime.ListImagesAsync"/> needs
    /// (the seam <see cref="ImagesServiceClient"/> was made non-<c>sealed</c>/<c>virtual</c> for) —
    /// <see cref="ContentGetAsync"/> throws a genuine, non-<c>notFound</c>, non-<c>Unavailable</c>
    /// <see cref="XpcException"/> for <paramref name="failingDigest"/> only, mirroring the live
    /// dangling-content symptom (cider-ede.24) rather than the "content simply absent" case
    /// <see cref="ImagesServiceClient.ContentGetAsync"/> already handles itself.</summary>
    private sealed class FakeImagesServiceClient(List<ImageDescription> descriptions, string failingDigest)
        : ImagesServiceClient(new XpcClient("com.apple.container.test.images.fake", NullLogger.Instance), TimeSpan.FromSeconds(30))
    {
        public override Task<List<ImageDescription>> ImageListAsync(CancellationToken ct) =>
            Task.FromResult(descriptions);

        public override Task<string?> ContentGetAsync(string digest, CancellationToken ct)
        {
            if (string.Equals(digest, failingDigest, StringComparison.Ordinal))
            {
                throw XpcException.ApiServer("internalError", $"content with digest {digest}: simulated dangling reference");
            }

            // No real local content store in this test; every other digest resolves to "nothing on
            // disk", the same best-effort miss LocalBlobReader.TryReadAsync already tolerates.
            return Task.FromResult<string?>(null);
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
