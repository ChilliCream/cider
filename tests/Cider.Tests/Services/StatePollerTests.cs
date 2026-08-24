using Cider.Core.Runtime;
using Cider.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class StatePollerTests
{
    [Fact]
    public async Task A_container_that_vanished_from_the_engine_is_marked_exited()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        // The engine never learned about a start, so listing does not report it as running.
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        await poller.PollOnceAsync(default);

        Assert.Equal("exited", record.State.Status);
        Assert.Equal("exit code unknown (daemon restarted)", record.State.Error);
        await events.WaitForAsync("die");
    }

    [Fact]
    public async Task A_container_started_outside_the_daemon_is_marked_running()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateShellAsync("sleep 30", "web");
        await harness.Runtime.StartContainerAsync("web", new StartOptions(), default);

        await poller.PollOnceAsync(default);

        Assert.Equal("running", record.State.Status);
        await events.WaitForAsync("start");

        await harness.Runtime.KillContainerAsync("web", "SIGKILL", default);
    }

    [Fact]
    public async Task A_container_the_daemon_holds_is_left_alone()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.RunShellAsync("sleep 30", "web");
        await poller.PollOnceAsync(default);

        Assert.Equal("running", record.State.Status);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task PollOnce_fills_in_an_address_start_gave_up_waiting_for()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        // Force Start to give up before the address is known, exactly like the real Apple container
        // race (docs/apple-container-notes.md §12): the record ends up "running" with no IP at all.
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(5);
        harness.Containers.StartReturnBudget = TimeSpan.FromMilliseconds(15);
        harness.Runtime.DelayNetworkAttachment("web", 1000);

        var record = await harness.CreateShellAsync("sleep 30", "web");
        await harness.Containers.StartAsync(record.Id, default);

        Assert.True(string.IsNullOrEmpty(record.Networks.GetValueOrDefault("bridge")?.IPAddress));
        Assert.False(harness.NameRegistry.TryResolve("bridge", "web", out _));

        // The race is over now: a later inspect reports the real attachment, and the poller's
        // belt-and-braces refresh (StatePoller.PollOnceAsync -> ContainerManager.RefreshNetworkInfoAsync)
        // must pick it up without anyone calling StartAsync again.
        harness.Runtime.DelayNetworkAttachment("web", 0);
        await poller.PollOnceAsync(default);

        Assert.True(harness.NameRegistry.TryResolve("bridge", "web", out var ip));
        Assert.Equal(ip.ToString(), record.Networks["bridge"].IPAddress);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Reconcile_marks_records_whose_engine_container_disappeared()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        await harness.Containers.ReconcileAsync(default);

        Assert.Equal("exited", record.State.Status);
    }

    [Fact]
    public async Task Reconcile_surfaces_containers_created_outside_the_daemon()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "handmade",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            Argv = ["sh"],
        });

        await harness.Containers.ReconcileAsync(default);

        var record = await harness.Containers.ResolveAsync("handmade", default);
        Assert.False(record.Managed);
        Assert.Equal("running", record.State.Status);
        Assert.Equal("sh", record.Path);
    }

    [Fact]
    public async Task The_daemons_own_system_containers_stay_hidden()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "cider-dns-bridge",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/coredns/coredns:1.14.7",
            Labels = new Dictionary<string, string> { ["com.chillicream.cider.system"] = "dns" },
        });

        await harness.Containers.ReconcileAsync(default);
        await poller.PollOnceAsync(default);

        Assert.Empty(await harness.Containers.ListAsync(all: true, null, false, Core.DockerApi.Filters.Empty, default));
    }

    private static StatePoller NewPoller(ContainerTestHarness harness) =>
        new(harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(20),
            FastInterval = TimeSpan.FromMilliseconds(10),
        };
}
