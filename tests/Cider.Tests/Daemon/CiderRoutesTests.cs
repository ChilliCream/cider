using System.Text.Json;
using Cider.Core.Runtime;
using Cider.Daemon.Routes;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>Integration tests for cider's own private endpoints (<c>/_cider/...</c>).</summary>
public sealed class CiderRoutesTests
{
    [Fact]
    public async Task Sync_returns_200_and_an_empty_report_when_nothing_changed()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (status, body) = await host.PostJsonAsync("/_cider/sync");

        Assert.Equal(200, status);
        var report = Deserialize(body);
        Assert.True(report.IsEmpty);
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Containers.Removed);
        Assert.Empty(report.Networks.Removed);
        Assert.Empty(report.Volumes.Removed);
    }

    [Fact]
    public async Task Sync_adopts_then_drops_a_container_the_runtime_alone_knows_about()
    {
        // A poll interval far longer than this test can possibly take: any removal below is
        // unambiguously sync's own doing, not the separate (already-shipped) poller-drop path racing it.
        await using var host = await DaemonTestHost.StartAsync(options => options.PollIntervalSeconds = 3600);

        host.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "outside-cider",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            Argv = ["sh"],
        });

        var (firstStatus, firstBody) = await host.PostJsonAsync("/_cider/sync");
        Assert.Equal(200, firstStatus);
        var adopted = Deserialize(firstBody);
        Assert.Contains("outside-cider", adopted.Containers.Adopted);

        host.Runtime.VanishContainer("outside-cider");

        var (secondStatus, secondBody) = await host.PostJsonAsync("/_cider/sync");
        Assert.Equal(200, secondStatus);
        var removed = Deserialize(secondBody);
        Assert.Contains("outside-cider", removed.Containers.Removed);

        // Idempotent: nothing left to reconcile a third time round.
        var (thirdStatus, thirdBody) = await host.PostJsonAsync("/_cider/sync");
        Assert.Equal(200, thirdStatus);
        Assert.True(Deserialize(thirdBody).IsEmpty);
    }

    [Fact]
    public async Task Sync_rejects_GET()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (status, _) = await host.GetAsync("/_cider/sync");

        Assert.True(status is 404 or 405, $"expected 404 or 405, got {status}");
    }

    // SyncReport's Containers/Networks/Volumes are get-only, non-collection-typed properties:
    // System.Text.Json silently leaves a get-only property of that shape at its default on
    // deserialize, so a plain Deserialize<SyncReport> would read back every field empty even though
    // the wire body is correct (CiderRoutes.MapCiderRoutes serializes SyncReport directly). Every
    // reader of the response goes through SyncReportDto instead — see its doc comment.
    private static SyncReportDto Deserialize(string body) =>
        JsonSerializer.Deserialize(body, CiderJsonContext.Default.SyncReportDto)
        ?? throw new InvalidOperationException("the daemon returned an empty sync report");
}
