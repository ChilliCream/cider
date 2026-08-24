using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class NetworkManagerTests
{
    private static (NetworkManager Manager, FakeContainerRuntime Runtime, InMemoryRecordStore<NetworkRecord> Store) CreateManager()
    {
        var runtime = new FakeContainerRuntime();
        var store = new InMemoryRecordStore<NetworkRecord>();
        var events = new EventBus();
        var manager = new NetworkManager(runtime, store, events, NullLogger<NetworkManager>.Instance);
        return (manager, runtime, store);
    }

    /// <summary>The name Aspire's DCP asks for; Apple refuses it for the trailing hyphen alone.</summary>
    private const string DcpNetworkName = "aspire-session-network-Ab12cdef-e2e-aspire-";

    [Fact]
    public void RuntimeNameFor_MapsBridgeToDefault_AndPassesOthersThrough()
    {
        var (manager, _, _) = CreateManager();

        Assert.Equal("default", manager.RuntimeNameFor("bridge"));
        Assert.Equal("mynet", manager.RuntimeNameFor("mynet"));
        Assert.Equal("with_underscore-and-dash", manager.RuntimeNameFor("with_underscore-and-dash"));
    }

    [Theory]
    // Apple `container network create` takes [a-z0-9_-] only and refuses a leading or trailing '-';
    // Docker accepts all of these, so they are folded and given a hash of the original name.
    [InlineData("MyNet", "mynet-")]
    [InlineData("trailing-", "trailing-")]
    [InlineData("-leading", "leading-")]
    [InlineData("dots.and spaces", "dots-and-spaces-")]
    [InlineData("---", "net-")]
    public void RuntimeNameFor_FoldsNamesAppleCannotRepresent(string dockerName, string expectedPrefix)
    {
        var (manager, _, _) = CreateManager();

        var runtimeName = manager.RuntimeNameFor(dockerName);

        Assert.StartsWith(expectedPrefix, runtimeName, StringComparison.Ordinal);
        Assert.Matches("^[a-z0-9_-]+$", runtimeName);
        Assert.NotEqual('-', runtimeName[^1]);
        Assert.NotEqual(runtimeName, manager.RuntimeNameFor(dockerName + "x"));
    }

    [Fact]
    public void RuntimeNameFor_IsStableAndBounded()
    {
        var (manager, _, _) = CreateManager();
        var name = manager.RuntimeNameFor(DcpNetworkName);

        Assert.Equal(name, manager.RuntimeNameFor(DcpNetworkName));
        Assert.True(name.Length <= 33, "the folded runtime name is " + name);
    }

    [Fact]
    public async Task CreateAsync_NameAppleRefuses_IsCreatedUnderAFoldedRuntimeName()
    {
        var (manager, runtime, _) = CreateManager();

        // Without the folding this throws: the fake engine applies Apple 1.2.2's own rule.
        var created = await manager.CreateAsync(
            new NetworkCreateRequest { Name = DcpNetworkName },
            CancellationToken.None);

        // Nothing observable changed: Docker still sees the name the client asked for.
        var inspected = await manager.InspectAsync(DcpNetworkName, verbose: false, scope: null, CancellationToken.None);
        Assert.Equal(DcpNetworkName, inspected.Name);
        Assert.Equal(created.Id, inspected.Id);

        // ... while the engine holds it under the folded name, which is what every other call uses.
        var runtimeName = manager.RuntimeNameFor(DcpNetworkName);
        Assert.NotEqual(DcpNetworkName, runtimeName);
        var networks = await runtime.ListNetworksAsync(CancellationToken.None);
        Assert.Contains(networks, network => string.Equals(network.Name, runtimeName, StringComparison.Ordinal));

        await manager.RemoveAsync(DcpNetworkName, CancellationToken.None);
        Assert.DoesNotContain(
            await runtime.ListNetworksAsync(CancellationToken.None),
            network => string.Equals(network.Name, runtimeName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_DropsLabelKeysAppleCannotStore_AndKeepsThemOnTheDockerSide()
    {
        var (manager, runtime, _) = CreateManager();
        var request = new NetworkCreateRequest
        {
            Name = "op3labels",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Exactly what Aspire's DCP sends; the first two keys carry uppercase, which Apple's
                // `network create --label` refuses with invalid_label_key_content.
                ["com.microsoft.developer.usvc-dev.creatorProcessId"] = "25372",
                ["com.microsoft.developer.usvc-dev.creatorProcessStartTime"] = "2026-08-22T11:27:53.872+05:30",
                ["com.microsoft.developer.usvc-dev.persistent"] = "false",
            },
        };

        // Without the filtering this throws, which is the 500 that stops an Aspire app dead.
        await manager.CreateAsync(request, CancellationToken.None);

        var inspected = await manager.InspectAsync("op3labels", verbose: false, scope: null, CancellationToken.None);
        Assert.Equal("25372", inspected.Labels["com.microsoft.developer.usvc-dev.creatorProcessId"]);
        Assert.Equal("false", inspected.Labels["com.microsoft.developer.usvc-dev.persistent"]);

        var engineLabels = (await runtime.InspectNetworkAsync("op3labels", CancellationToken.None))!.Labels;
        Assert.DoesNotContain("com.microsoft.developer.usvc-dev.creatorProcessId", engineLabels.Keys, StringComparer.Ordinal);
        Assert.Contains("com.microsoft.developer.usvc-dev.persistent", engineLabels.Keys, StringComparer.Ordinal);
        Assert.Contains("com.chillicream.cider.id", engineLabels.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolveDockerName_MapsAnIdOntoItsName_AndLeavesEverythingElseAlone()
    {
        var (manager, _, _) = CreateManager();
        var created = await manager.CreateAsync(new NetworkCreateRequest { Name = "op3byid" }, CancellationToken.None);

        Assert.Equal("op3byid", manager.ResolveDockerName(created.Id));
        Assert.Equal("op3byid", manager.ResolveDockerName(created.Id[..12]));
        Assert.Equal("op3byid", manager.ResolveDockerName("op3byid"));
        Assert.Equal("nosuchnetwork", manager.ResolveDockerName("nosuchnetwork"));
    }

    [Fact]
    public async Task ListAsync_IncludesBridgeHostAndNonePseudoNetworks()
    {
        var (manager, _, _) = CreateManager();

        var networks = await manager.ListAsync(Filters.Empty, CancellationToken.None);

        Assert.Contains(networks, n => n.Name == "bridge" && n.Driver == "bridge");
        Assert.Contains(networks, n => n.Name == "host" && n.Driver == "host");
        Assert.Contains(networks, n => n.Name == "none" && n.Driver == "null");
    }

    [Fact]
    public async Task CreateAsync_ThenListAndInspect_RoundTrips()
    {
        var (manager, _, _) = CreateManager();
        var request = new NetworkCreateRequest { Name = "mynet", Driver = "bridge" };

        var created = await manager.CreateAsync(request, CancellationToken.None);
        Assert.Equal(64, created.Id.Length);

        var inspected = await manager.InspectAsync("mynet", verbose: false, scope: null, CancellationToken.None);
        Assert.Equal("mynet", inspected.Name);
        Assert.Equal(created.Id, inspected.Id);

        var listed = await manager.ListAsync(Filters.Empty, CancellationToken.None);
        Assert.Contains(listed, n => n.Name == "mynet");
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws409()
    {
        var (manager, _, _) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "dup" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.CreateAsync(new NetworkCreateRequest { Name = "dup" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.Status);
    }

    /// <summary>
    /// When the runtime takes the create and never answers, the client gets a
    /// Docker-shaped 500 that names the runtime — not a 503 inviting a retry into the same stall,
    /// and not a five-minute silence that every client reads as a dead daemon. The bound itself is
    /// covered at the CLI seam in <c>ResourceTimeoutTests</c>.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenTheRuntimeNeverAnswers_Is500NamingTheRuntime()
    {
        var (manager, runtime, store) = CreateManager();
        runtime.CreateNetworkFailure = RuntimeException.Timeout(
            "cider: the Apple container runtime did not answer 'network create' within 30s. " +
            "The runtime is most likely wedged; see Troubleshooting in the cider README to recover it.");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.CreateAsync(new NetworkCreateRequest { Name = "wedged" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, ex.Status);
        Assert.Contains("Apple container runtime did not answer", ex.Message, StringComparison.Ordinal);
        Assert.Null(store.Get("wedged"));
    }

    [Fact]
    public async Task CreateAsync_UnsupportedDriver_Throws501()
    {
        var (manager, _, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.CreateAsync(new NetworkCreateRequest { Name = "overlaynet", Driver = "overlay" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    public async Task RemoveAsync_PredefinedNetwork_Throws403(string name)
    {
        var (manager, _, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.RemoveAsync(name, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.Status);
    }

    [Fact]
    public async Task RemoveAsync_UserNetwork_RemovesFromRuntimeAndStore()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "removable" }, CancellationToken.None);

        await manager.RemoveAsync("removable", CancellationToken.None);

        Assert.Null(store.Get("removable"));
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync("removable", verbose: false, scope: null, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task ConnectAsync_WithoutAContainerManager_Throws501()
    {
        var (manager, _, _) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "lonely" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.ConnectAsync("lonely", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
    }

    [Fact]
    public async Task ConnectAsync_UnknownNetwork_Throws404()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.CreateAsync(name: "c1");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("ghost", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("network ghost not found", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_UnknownContainer_Throws404()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "ghost" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("No such container: ghost", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_NoContainerInTheBody_Throws400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("extra", new NetworkConnectRequest(), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task ConnectAsync_CreatedContainer_UpdatesTheRecordAndRecreatesTheEngineContainer()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateAsync(name: "c1");
        await using var events = await harness.CollectEventsAsync();

        await harness.Networks.ConnectAsync(
            "extra",
            new NetworkConnectRequest { Container = "c1", EndpointConfig = new EndpointSettings { Aliases = ["www"] } },
            CancellationToken.None);

        var record = await harness.Containers.ResolveAsync(created.Id, CancellationToken.None);
        Assert.Equal(["bridge", "extra"], record.Networks.Keys.ToArray());

        var endpoint = record.Networks["extra"];
        Assert.Equal(["www"], endpoint.Aliases);
        Assert.Equal(["c1", record.Request.Hostname], endpoint.DNSNames);

        // Apple cannot change a container's networks, so the engine container is re-created with
        // the extended list (`bridge` is Apple's `default`).
        var spec = harness.Runtime.GetSpec(record.RuntimeId);
        Assert.NotNull(spec);
        Assert.Equal(["default", "extra"], spec.Networks.ToArray());
        Assert.Contains($"RemoveContainerAsync:{record.RuntimeId}:False", harness.Runtime.Calls);
        Assert.Equal(2, harness.Runtime.Calls.Count(call => call == $"CreateContainerAsync:{record.RuntimeId}"));

        await events.WaitForAsync("connect");
        var published = events.First("connect");
        Assert.Equal("network", published.Type);
        Assert.Equal("extra", published.Actor.Attributes["name"]);
        Assert.Equal(record.Id, published.Actor.Attributes["container"]);
    }

    [Fact]
    public async Task ConnectAsync_CancelledAfterTheRemove_StillRecreatesTheEngineContainer()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateAsync(name: "c1");

        // Once the remove has gone through, the engine container is gone: a cancellation landing in
        // that window must not stop the re-create, or the record would outlive its container and the
        // container could never be started again.
        using var cts = new CancellationTokenSource();
        harness.Runtime.AfterRemove = cts.Cancel;

        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, cts.Token);
        harness.Runtime.AfterRemove = null;

        var spec = harness.Runtime.GetSpec(created.RuntimeId);
        Assert.NotNull(spec);
        Assert.Equal(["default", "extra"], spec.Networks.ToArray());

        var record = await harness.Containers.ResolveAsync(created.Id, CancellationToken.None);
        Assert.Equal(["bridge", "extra"], record.Networks.Keys.ToArray());
    }

    [Fact]
    public async Task ConnectAsync_DoesNotAddAPlatformTheCreateNeverAskedFor()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // An image that resolves to a platform, so the record's resolved platform and the platform
        // the client asked for (none) differ.
        harness.Runtime.SeedImage(new RuntimeImageDetail
        {
            Id = "sha256:" + new string('e', 64),
            References = ["docker.io/library/alpine:latest"],
            Platforms = ["linux/arm64"],
            Config = new ImageConfig { Cmd = ["/bin/sh"] },
            Architecture = "arm64",
            Os = "linux",
        });
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateAsync(name: "c1");

        Assert.Equal("linux/arm64", created.Platform);
        Assert.Null(harness.Runtime.GetSpec(created.RuntimeId)!.Platform);

        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);

        // `docker create alpine` passed no ?platform=, so neither may the re-create.
        Assert.Null(harness.Runtime.GetSpec(created.RuntimeId)!.Platform);
    }

    [Fact]
    public async Task ConnectAsync_RepeatsThePlatformTheCreateAskedFor()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var response = await harness.Containers.CreateAsync(
            new ContainerCreateRequest { Image = "alpine" }, "c1", "linux/amd64", CancellationToken.None);
        var created = await harness.Containers.ResolveAsync(response.Id, CancellationToken.None);
        Assert.Equal("linux/amd64", harness.Runtime.GetSpec(created.RuntimeId)!.Platform);

        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);

        Assert.Equal("linux/amd64", harness.Runtime.GetSpec(created.RuntimeId)!.Platform);
    }

    [Fact]
    public async Task ConnectAsync_ThenStart_ReportsAnAddressOnBothNetworks()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateShellAsync("sleep 30", "c1");

        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);
        await harness.Containers.StartAsync(created.Id, CancellationToken.None);

        var record = await harness.Containers.ResolveAsync(created.Id, CancellationToken.None);
        Assert.All(
            record.Networks.Values,
            endpoint => Assert.False(string.IsNullOrEmpty(endpoint.IPAddress), "every endpoint has an address"));
    }

    [Fact]
    public async Task ConnectAsync_TwiceToTheSameNetwork_Throws403()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        await harness.CreateAsync(name: "c1");
        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.Status);
        Assert.Equal("endpoint with name c1 already exists in network extra", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_RunningContainer_Throws501()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var record = await harness.RunShellAsync("sleep 30", "c1");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
        Assert.Contains("connecting a running container", ex.Message, StringComparison.Ordinal);

        var reloaded = await harness.Containers.ResolveAsync(record.Id, CancellationToken.None);
        Assert.DoesNotContain("extra", reloaded.Networks.Keys);
    }

    [Fact]
    public async Task ConnectAsync_ExitedContainer_Throws501()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var record = await harness.RunShellAsync("exit 0", "c1");
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.Containers.ResolveAsync(record.Id, CancellationToken.None).Result.State.Status == "exited",
            "the container to exit");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
        Assert.Contains("connecting a stopped container", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectAsync_CreatedContainer_RemovesTheNetworkAndRecreatesTheEngineContainer()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateAsync(name: "c1");
        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);
        await using var events = await harness.CollectEventsAsync();

        await harness.Networks.DisconnectAsync("extra", new NetworkDisconnectRequest { Container = "c1" }, CancellationToken.None);

        var record = await harness.Containers.ResolveAsync(created.Id, CancellationToken.None);
        Assert.Equal(["bridge"], record.Networks.Keys.ToArray());
        Assert.Equal(["default"], harness.Runtime.GetSpec(record.RuntimeId)!.Networks.ToArray());

        await events.WaitForAsync("disconnect");
    }

    [Fact]
    public async Task DisconnectAsync_NetworkTheContainerIsNotOn_Throws403()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        var created = await harness.CreateAsync(name: "c1");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.DisconnectAsync("extra", new NetworkDisconnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.Status);
        Assert.Equal($"container {created.Id} is not connected to network extra", ex.Message);
    }

    [Fact]
    public async Task DisconnectAsync_TheOnlyNetwork_Throws501()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.CreateAsync(name: "c1");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.DisconnectAsync("bridge", new NetworkDisconnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
        Assert.Contains("at least one network", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectAsync_RunningContainer_Throws501()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "extra" }, CancellationToken.None);
        await harness.CreateShellAsync("sleep 30", "c1");
        await harness.Networks.ConnectAsync("extra", new NetworkConnectRequest { Container = "c1" }, CancellationToken.None);
        await harness.Containers.StartAsync("c1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.DisconnectAsync("extra", new NetworkDisconnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, ex.Status);
        Assert.Contains("disconnecting a running container", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("none")]
    public async Task ConnectAsync_PseudoNetwork_Throws400(string network)
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.CreateAsync(name: "c1");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Networks.ConnectAsync(network, new NetworkConnectRequest { Container = "c1" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
    }

    [Fact]
    public async Task PruneAsync_UnknownFilterKey_Is400_AndNothingIsRemoved()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "empty-net" }, CancellationToken.None);
        manager.SetContainerEndpoints(_ => []);

        // Ignoring an unknown key means a mistyped guard prunes what it was written to protect;
        // dockerd validates the key set per endpoint.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"bogus":["x"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'bogus'", ex.Message);
        Assert.NotNull(store.Get("empty-net"));
    }

    [Fact]
    public async Task PruneAsync_RemovesNetworksWithNoContainers()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "empty-net" }, CancellationToken.None);
        await manager.CreateAsync(new NetworkCreateRequest { Name = "used-net" }, CancellationToken.None);
        manager.SetContainerEndpoints(name => name == "used-net"
            ? [("c1", "web", new EndpointSettings())]
            : []);

        var result = await manager.PruneAsync(Filters.Empty, CancellationToken.None);

        Assert.Contains("empty-net", result.NetworksDeleted);
        Assert.DoesNotContain("used-net", result.NetworksDeleted);
        Assert.Null(store.Get("empty-net"));
        Assert.NotNull(store.Get("used-net"));
    }

    [Fact]
    public async Task PruneAsync_UnparseableUntil_Is400_AndNothingIsRemoved()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new NetworkCreateRequest { Name = "empty-net" }, CancellationToken.None);
        manager.SetContainerEndpoints(_ => []);

        // `until` was accepted by Validate here but never read at all — any value, garbage included,
        // was silently ignored and every unused network was pruned regardless.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"until":["not-a-time"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal(
            "parsing time \"not-a-time\" as \"2006-01-02\": cannot parse \"not-a-time\" as \"2006\"",
            ex.Message);
        Assert.NotNull(store.Get("empty-net"));
    }
}
