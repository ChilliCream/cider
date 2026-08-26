using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// cider-ede.24: <c>container image ls</c> fails hard when Apple's store holds even one dangling
/// content reference, even though every other entry is fine (verified live: <c>Error: content with
/// digest sha256:…</c>, the blob gone but <c>state.json</c> still naming it). Before this fix every
/// <see cref="AppleContainerRuntime.ListImagesAsync"/> call — and so <c>docker images</c> — 500'd
/// outright on a store in that state; this drives the fake-CLI seam
/// (<see cref="AppleContainerRuntimeImageTests"/>'s own pattern) to prove it now degrades instead.
/// </summary>
public sealed class AppleContainerRuntimeDanglingContentTests
{
    private const string DanglingStderr =
        "Error: content with digest sha256:6baf43584bcb78f2e5847d1de515f23499913ac9f12bdf834811a3145eb11ca1";

    [Fact]
    public async Task ListImagesAsync_DoesNotThrow_WhenTheStoreReportsOneDanglingContentReference()
    {
        var cli = new ScriptedCli(new CliResult(1, "", DanglingStderr));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        Assert.Empty(images);
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

    private sealed class ScriptedCli(CliResult imageLsResult) : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
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

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }
}
