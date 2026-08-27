using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>
/// <see cref="StateSynchronizer"/>: the one-shot resync engine behind the (not yet wired) <c>cider
/// sync</c> verb. Exercised directly against a <see cref="ContainerTestHarness"/> — no HTTP route
/// exists yet (cider-eh2).
/// </summary>
public sealed class StateSynchronizerTests
{
    private static StateSynchronizer NewSynchronizer(ContainerTestHarness harness) => new(
        harness.Runtime, harness.Containers, harness.Networks, harness.Volumes, harness.Dns,
        NullLogger<StateSynchronizer>.Instance);

    [Fact]
    public async Task SyncAsync_DropsVanishedRecords_Immediately_NoConsecutiveMissGuard()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        var container = await harness.CreateAsync("alpine", "web");
        container.State.Status = "running";
        harness.Store.Upsert(container.Id, container);
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "vanishing-net" }, default);
        harness.Runtime.VanishNetwork("vanishing-net");

        await harness.Volumes.CreateAsync(new VolumeCreateRequest { Name = "vanishing-vol" }, default);
        harness.Runtime.VanishVolume("vanishing-vol");

        var report = await sync.SyncAsync(default);

        // A single miss is enough (unlike the state poller's two-consecutive-miss guard): this is an
        // explicit, user-requested resync.
        Assert.Contains("web", report.Containers.Removed);
        Assert.Null(harness.Store.Get(container.Id));
        await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.ResolveAsync("web", default));

        Assert.Contains("vanishing-net", report.Networks.Removed);
        await Assert.ThrowsAsync<DockerApiException>(() => harness.Networks.ResolveAsync("vanishing-net", default));

        Assert.Contains("vanishing-vol", report.Volumes.Removed);
        await Assert.ThrowsAsync<DockerApiException>(() => harness.Volumes.InspectAsync("vanishing-vol", default));
    }

    [Fact]
    public async Task SyncAsync_DroppingAVanishedRecord_CompletesAPendingDockerWait()
    {
        // cider-1ki: the third instance of the cider-ede.33 class. `cider sync`'s "drop vanished
        // records" step (StateSynchronizer.SyncContainersAsync step 1 -> ForgetVanishedAsync) used
        // to delete the record, complete the `removed` waiter and the attachments, and leave the
        // record's NextExit pending -- so a `docker wait` from before the resync waited forever on
        // a container whose record no longer exists. Adopted here on purpose (no held process): with
        // a held process the drop is skipped entirely, and HandleExitAsync would have completed the
        // waiter anyway.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        var container = await harness.CreateAsync("alpine", "web");
        container.State.Status = "running";
        harness.Store.Upsert(container.Id, container);
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        var nextExit = harness.Containers.WaitAsync(container.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(container.Id, "not-running", default);
        var removed = harness.Containers.WaitAsync(container.Id, "removed", default);

        var report = await sync.SyncAsync(default);

        Assert.Contains("web", report.Containers.Removed);
        Assert.Null(harness.Store.Get(container.Id));

        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));
        var removedResponse = await removed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(container.State.ExitCode, nextExitResponse.StatusCode);
        Assert.Equal(container.State.ExitCode, notRunningResponse.StatusCode);
        Assert.Equal(container.State.ExitCode, removedResponse.StatusCode);
    }

    [Fact]
    public async Task SyncAsync_AdoptsUnknownRuntimeResources_AsReadOnlyRecords()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "outside-cider",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            Argv = ["sh"],
        });

        await harness.Runtime.CreateNetworkAsync(
            new NetworkSpec { Name = "outside-net", Subnet = "192.168.70.0/24" }, default);

        await harness.Runtime.CreateVolumeAsync(new VolumeSpec { Name = "outside-vol" }, default);

        var report = await sync.SyncAsync(default);

        Assert.Contains("outside-cider", report.Containers.Adopted);
        var adoptedContainer = await harness.Containers.ResolveAsync("outside-cider", default);
        Assert.False(adoptedContainer.Managed);

        Assert.Contains("outside-net", report.Networks.Adopted);
        var adoptedNetwork = await harness.Networks.ResolveAsync("outside-net", default);
        Assert.False(adoptedNetwork.Managed);
        Assert.Equal("192.168.70.0/24", adoptedNetwork.Request.IPAM?.Config?[0].Subnet);

        Assert.Contains("outside-vol", report.Volumes.Adopted);
        var adoptedVolume = await harness.Volumes.InspectAsync("outside-vol", default);
        Assert.Equal("local", adoptedVolume.Driver);
    }

    [Fact]
    public async Task SyncAsync_CorrectsStatusDrift_ThenReportsNothingOnASecondRun()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        // Created but started directly through the engine, bypassing cider: the record is stale.
        var record = await harness.CreateShellAsync("sleep 30", "web");
        Assert.Equal("created", record.State.Status);
        await harness.Runtime.StartContainerAsync("web", new StartOptions(), default);

        var first = await sync.SyncAsync(default);

        Assert.Equal("running", record.State.Status);
        Assert.Contains("web", first.Containers.Updated);

        var second = await sync.SyncAsync(default);

        Assert.True(second.Containers.IsEmpty, "a second pass over unchanged state must report nothing");
        Assert.True(second.Networks.IsEmpty, "a second pass over unchanged state must report nothing");
        Assert.True(second.Volumes.IsEmpty, "a second pass over unchanged state must report nothing");

        await harness.Runtime.KillContainerAsync("web", "SIGKILL", default);
    }

    [Fact]
    public async Task SyncAsync_NeverDropsAContainerTheDaemonHolds_EvenAfterItVanishes()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        var record = await harness.RunShellAsync("sleep 30", "web");

        // Apple's services restart and lose track of it (ARCHITECTURE §6/§9), but this daemon still
        // holds the init process directly — it never went through RemoveContainerAsync.
        harness.Runtime.VanishContainer("web");

        var first = await sync.SyncAsync(default);

        // Never dropped: the process is still held, so the runtime's missing listing is a transient
        // gap, not a removal (mirrors StatePoller.IsHeldByUs).
        Assert.DoesNotContain("web", first.Containers.Removed);
        Assert.NotNull(harness.Store.Get(record.Id));
        var stillThere = await harness.Containers.ResolveAsync("web", default);
        Assert.Equal(record.Id, stillThere.Id);

        var second = await sync.SyncAsync(default);

        Assert.True(second.Containers.IsEmpty, "a second pass over unchanged state must report nothing");
    }

    [Fact]
    public async Task SyncAsync_DnsForwarders_ReportsStartedAndStoppedNetworks()
    {
        // cider-ede.39: cider-eh2 shipped the forwarder resync itself but the report had no counter
        // for it, so a `cider sync` that started or stopped a forwarder looked identical to one that
        // never touched DNS at all. This pins the fix: both halves show up in report.Dns.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        // A running container on "bridge" — its forwarder gets ensured every pass, which counts as
        // "started" here (SyncReport.Dns can't tell "just created" from "already running"; see its
        // doc comment).
        var running = await harness.RunShellAsync("sleep 30", "web-with-dns");
        Assert.True(running.Networks.ContainsKey("bridge"), "the default create attaches bridge");

        // A network record that vanished from the engine has its forwarder released as part of
        // dropping the record (NetworkManager.ReconcileAsync).
        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "vanishing-net" }, default);
        harness.Runtime.VanishNetwork("vanishing-net");

        var report = await sync.SyncAsync(default);

        Assert.Contains("bridge", report.Dns.Adopted);
        Assert.Contains("bridge", harness.Dns.Requested);

        Assert.Contains("vanishing-net", report.Dns.Removed);
        Assert.Contains("vanishing-net", harness.Dns.Released);

        await harness.Runtime.KillContainerAsync("web-with-dns", "SIGKILL", default);
    }

    [Fact]
    public async Task SyncAsync_DnsForwarders_ReportsZerosRatherThanOmittingTheLine_WhenNothingTouchesDns()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        // No running containers and no dropped networks: nothing for SyncDnsForwardersAsync or the
        // network reconciler to touch.
        var report = await sync.SyncAsync(default);

        Assert.Empty(report.Dns.Adopted);
        Assert.Empty(report.Dns.Removed);
        Assert.True(report.Dns.IsEmpty);
        Assert.Empty(harness.Dns.Requested);
        Assert.Empty(harness.Dns.Released);
    }

    [Fact]
    public async Task SyncAsync_DnsForwarders_DroppingANetworkWithNoForwarderPresent_ReportsNothingStopped()
    {
        // cider-ede.39 correction: report.Dns.Removed must not credit a network for a forwarder that
        // was never actually torn down (DNS disabled, or none was ever running for this network) —
        // NetworkManager.ReleaseDnsForwarderAsync now only adds the network when ReleaseAsync itself
        // reports it removed something. Simulated here with NullDnsForwarderService, the same object a
        // DNS-disabled daemon wires up (DaemonLifecycle.cs), whose ReleaseAsync always returns false.
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Networks.SetDnsForwarders(NullDnsForwarderService.Instance);
        var sync = NewSynchronizer(harness);

        await harness.Networks.CreateAsync(new NetworkCreateRequest { Name = "vanishing-net" }, default);
        harness.Runtime.VanishNetwork("vanishing-net");

        var report = await sync.SyncAsync(default);

        Assert.Contains("vanishing-net", report.Networks.Removed);
        Assert.Empty(report.Dns.Removed);
    }

    [Fact]
    public async Task SyncAsync_EngineListFailure_ThrowsAndChangesNothing()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var sync = NewSynchronizer(harness);

        // Would be dropped by a successful pass; must survive an aborted one untouched.
        var container = await harness.CreateAsync("alpine", "web");
        container.State.Status = "running";
        harness.Store.Upsert(container.Id, container);
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        harness.Runtime.ListContainersFailure = RuntimeException.Unavailable("engine unreachable");

        await Assert.ThrowsAsync<RuntimeException>(() => sync.SyncAsync(default));

        // Nothing was touched: the vanished record is still exactly as it was before the pass.
        var survivor = harness.Store.Get(container.Id);
        Assert.NotNull(survivor);
        Assert.Equal("running", survivor!.State.Status);
    }
}
