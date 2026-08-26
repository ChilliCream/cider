using Cider.Daemon.Hosting;
using Cider.Daemon.Routes;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// A <see cref="DaemonFixture"/> whose state poller is pushed out to effectively never fire during a
/// test run. <see cref="SyncTests"/> needs this on its own daemon (rather than the shared
/// <see cref="DaemonCollection"/> one): the automatic poller-drop (a separate, already-shipped
/// behaviour — see <c>StatePoller</c>) would otherwise race <c>POST /_cider/sync</c> for the exact
/// same records and make the assertions non-deterministic about which of the two actually did the
/// dropping.
/// </summary>
public sealed class SyncFixture : DaemonFixture
{
    /// <inheritdoc />
    protected override int? PollIntervalOverride => 3600;
}

/// <summary>The collection <see cref="SyncTests"/> uses so it gets its own daemon.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SyncCollection : ICollectionFixture<SyncFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "cider-e2e-sync";
}

/// <summary>
/// <c>POST /_cider/sync</c> — the endpoint behind <c>cider sync</c>: resynchronising cider's
/// persisted state against Apple <c>container</c> after a container/network is removed with the
/// Apple CLI directly, where cider never sees it happen.
/// </summary>
[Collection(SyncCollection.Name)]
[Trait("Category", "E2E")]
public sealed class SyncTests(SyncFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task Sync_drops_a_container_and_a_network_removed_through_the_Apple_CLI()
    {
        var containerName = DaemonFixture.NewName("sync-c");
        var networkName = DaemonFixture.NewName("sync-n");

        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", containerName, Image, "sleep", "300"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        var createNetwork = await daemon.DockerAsync(["network", "create", networkName], timeout: TimeSpan.FromMinutes(2));
        Assert.True(createNetwork.Ok, createNetwork.ToString());

        try
        {
            // Release the held init process cleanly before pulling the container out from under
            // cider — mirrors ExternalRemovalTests: a still-running record's held process makes
            // StateSynchronizer treat the runtime not (yet) listing it as a transient gap, not a
            // removal.
            var stop = await daemon.DockerAsync(["stop", "-t", "1", containerName], timeout: TimeSpan.FromMinutes(2));
            Assert.True(stop.Ok, stop.ToString());

            // The E2E suite multi-targets net10.0/net11.0 and dotnet test runs both concurrently,
            // each against its own throwaway daemon but the one shared, real Apple `container`
            // backend — under that concurrent load the backend's own bookkeeping of a container
            // `docker stop` just returned from can lag visibly behind. Wait for the backend to
            // actually list it before pulling it out from under cider, rather than racing that lag.
            await WaitUntilVisibleAsync(containerName);

            // Both names came from DaemonFixture.NewName, which only ever produces runtime-safe
            // characters, so the Apple-side name is exactly the Docker name (NetworkManager.
            // RuntimeNameFor / ContainerIdentity.ResolveRuntimeId are no-ops for a name this shape).
            await RunAppleCliAsync(["delete", "-f", containerName]);
            await RunAppleCliAsync(["network", "delete", networkName]);

            var report = await PostSyncAsync();

            Assert.Contains(containerName, report.Containers.Removed);
            Assert.Contains(networkName, report.Networks.Removed);

            var ps = await daemon.DockerAsync(["ps", "-a", "--format", "{{.Names}}"], timeout: TimeSpan.FromSeconds(60));
            Assert.True(ps.Ok, ps.ToString());
            Assert.DoesNotContain(containerName, ps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var networkLs = await daemon.DockerAsync(["network", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
            Assert.True(networkLs.Ok, networkLs.ToString());
            Assert.DoesNotContain(networkName, networkLs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            // Idempotent: nothing left to reconcile the second time round.
            var second = await PostSyncAsync();
            Assert.DoesNotContain(containerName, second.Containers.Removed);
            Assert.DoesNotContain(networkName, second.Networks.Removed);
        }
        finally
        {
            // Best-effort: both are already gone on the happy path: these are only for a failed
            // assertion partway through.
            await daemon.DockerAsync(["rm", "-f", containerName], timeout: TimeSpan.FromSeconds(60));
            await daemon.DockerAsync(["network", "rm", networkName], timeout: TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// E2E — cider-ede.37 leg 3: cider-ede.29 (commit 021efa5) fixed <c>AdoptContainerAsync</c> storing
    /// an adopted container's raw engine index digest as its <c>ImageId</c> verbatim, instead of
    /// resolving it through <c>RuntimeImage.IndexDigests</c> to the same content-addressed config id
    /// <c>docker images</c> prints — so <c>docker inspect &lt;adopted&gt; --format '{{.Image}}'</c>
    /// matched nothing in <c>docker images</c> for any container the Apple CLI created directly. Its
    /// own Verification section named this live leg, never run before this task: create a container
    /// with the Apple CLI, adopt it through <c>POST /_cider/sync</c> (the endpoint this class already
    /// drives), and assert the adopted container's <c>.Image</c> equals the image's own
    /// <c>docker images -q</c> id.
    /// </summary>
    [E2EFact]
    public async Task Sync_resolves_an_adopted_containers_image_to_the_docker_visible_id()
    {
        var name = DaemonFixture.NewName("adopt-img");

        // Pulled through cider first so the image is already docker-visible with a known id, then the
        // container itself is created straight through the Apple CLI -- cider never sees this
        // container come into being, exactly the "created outside cider" precondition adoption exists
        // for (see the class doc comment and Logs_on_a_container_started_outside_cider... above).
        var pull = await daemon.DockerAsync(["pull", Image], timeout: TimeSpan.FromMinutes(6));
        Assert.True(pull.Ok, pull.ToString());

        var run = await Cmd.RunAsync(
            "container",
            ["run", "-d", "--name", name, Image, "sleep", "120"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            await WaitUntilVisibleAsync(name);

            var report = await PostSyncAsync();
            Assert.Contains(name, report.Containers.Adopted);

            var inspect = await daemon.DockerAsync("inspect", name, "--format", "{{.Image}}");
            Assert.True(inspect.Ok, inspect.ToString());
            var adoptedImageId = inspect.Stdout.Trim();

            var imagesQ = await daemon.DockerAsync("images", "--no-trunc", "-q", Image);
            Assert.True(imagesQ.Ok, imagesQ.ToString());
            var dockerVisibleId = imagesQ.Stdout.Trim();

            Assert.NotEmpty(dockerVisibleId);
            Assert.Equal(dockerVisibleId, adoptedImageId);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
            await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Runs a <c>container</c> CLI command, retrying a few times on "not found" (see the caller).</summary>
    private static async Task RunAppleCliAsync(IReadOnlyList<string> arguments)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await Cmd.RunAsync("container", arguments, timeout: TimeSpan.FromSeconds(60));
            if (result.Ok || attempt >= 5 || !result.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(result.Ok, result.ToString());
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }

    /// <summary>Waits for the Apple <c>container</c> backend to list <paramref name="name"/> (see the caller).</summary>
    private static async Task WaitUntilVisibleAsync(string name)
    {
        var visible = await DaemonFixture.EventuallyAsync(
            async () =>
            {
                var ls = await Cmd.RunAsync("container", ["ls", "-a", "--format", "json"], timeout: TimeSpan.FromSeconds(30));
                return ls.Ok && ls.Stdout.Contains($"\"{name}\"", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(250));
        Assert.True(visible, $"the Apple container backend never listed {name}");
    }

    /// <summary>POSTs <c>/_cider/sync</c> straight to the fixture's socket, like <c>cider sync</c> does.</summary>
    private async Task<SyncReportDto> PostSyncAsync()
    {
        using var client = DaemonClient.Create(daemon.Options.SocketPath, TimeSpan.FromMinutes(2));
        using var response = await client.PostAsync(new Uri("/_cider/sync", UriKind.Relative), content: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST /_cider/sync -> {(int)response.StatusCode}: {body}");

        // SyncReportDto, not SyncReport — see its doc comment (SyncReport's nested properties are
        // get-only, which System.Text.Json silently fails to deserialize into).
        return System.Text.Json.JsonSerializer.Deserialize(body, CiderJsonContext.Default.SyncReportDto)
            ?? throw new InvalidOperationException("cider: the daemon returned an empty sync report");
    }
}
