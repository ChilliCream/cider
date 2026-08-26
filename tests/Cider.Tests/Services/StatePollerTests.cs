using Cider.Core.DockerApi;
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
    public async Task A_single_miss_completes_a_pending_docker_wait_for_an_adopted_container()
    {
        // cider-ede.33: a container cider only adopted has no held process, so HandleExitAsync
        // never runs for it -- this poller-observed transition (record entirely missing from the
        // engine's listing) is the only place that can complete a pending `docker wait`.
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        await harness.Runtime.RemoveContainerAsync("web", force: true, default);
        await poller.PollOnceAsync(default);

        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(record.State.ExitCode, nextExitResponse.StatusCode);
        Assert.Equal(record.State.ExitCode, notRunningResponse.StatusCode);
        Assert.Equal("exit code unknown (daemon restarted)", nextExitResponse.Error?.Message);
    }

    [Fact]
    public async Task Still_listed_but_no_longer_running_completes_a_pending_docker_wait_for_an_adopted_container()
    {
        // Same gap, the other die path: the engine still lists the container (so this is the
        // `!running && record.State.Running` branch, not the miss branch above) but reports it
        // stopped -- e.g. a container started with the Apple CLI directly and exited on its own.
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        var container = harness.Runtime.GetContainer(record.RuntimeId);
        Assert.NotNull(container);
        container!.State = RuntimeContainerState.Stopped;

        await poller.PollOnceAsync(default);

        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(record.State.ExitCode, nextExitResponse.StatusCode);
        Assert.Equal(record.State.ExitCode, notRunningResponse.StatusCode);
        Assert.Equal("exited", record.State.Status);
    }

    [Fact]
    public async Task A_container_missing_twice_in_a_row_is_dropped_and_its_name_freed()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        // Removed outside cider: gone from the engine's listing entirely.
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        // First miss: today's behaviour — marked exited, record stays.
        await poller.PollOnceAsync(default);
        Assert.Equal("exited", record.State.Status);
        Assert.NotNull(harness.Store.Get(record.Id));

        // Second consecutive miss: dropped for good.
        await poller.PollOnceAsync(default);

        Assert.Null(harness.Store.Get(record.Id));
        await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.ResolveAsync("web", default));
        await events.WaitForAsync("destroy");

        // The name is free for reuse.
        var recreated = await harness.CreateAsync("alpine", "web");
        Assert.NotEqual(record.Id, recreated.Id);
    }

    [Fact]
    public async Task A_container_the_daemon_holds_is_never_dropped_even_after_it_vanishes()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.RunShellAsync("sleep 30", "web");

        // Apple's services restart and lose track of it (ARCHITECTURE §6/§9), but this daemon still
        // holds the init process directly — it never went through `RemoveContainerAsync`.
        harness.Runtime.VanishContainer("web");

        for (var i = 0; i < 5; i++)
        {
            await poller.PollOnceAsync(default);
        }

        // Never dropped: the record (and its name) survive every poll while the process is held.
        Assert.NotNull(harness.Store.Get(record.Id));
        var stillThere = await harness.Containers.ResolveAsync("web", default);
        Assert.Equal(record.Id, stillThere.Id);
    }

    [Fact]
    public async Task A_single_miss_does_not_drop_a_container_seen_again_next_poll()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var poller = NewPoller(harness);

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        // One miss: marked exited, miss counter at 1.
        await poller.PollOnceAsync(default);
        Assert.Equal("exited", record.State.Status);

        // The container comes back (e.g. re-created directly through the Apple CLI with the same
        // name) before a second consecutive miss: the counter must reset instead of carrying over.
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
        });
        await poller.PollOnceAsync(default);

        Assert.Equal("running", record.State.Status);
        await events.WaitForAsync("start");

        // One more miss on its own must not drop it: the earlier miss no longer counts.
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);
        await poller.PollOnceAsync(default);

        Assert.NotNull(harness.Store.Get(record.Id));
        Assert.Equal("exited", record.State.Status);
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

    // ---- poll interval default resolution (task cider-ede.19) -------------------------------

    [Fact]
    public async Task Interval_defaults_to_3s_on_the_cli_transport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        await using var poller = new StatePoller(
            harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(3), poller.Interval);
    }

    [Fact]
    public async Task Interval_defaults_to_1s_on_the_xpc_transport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.IsXpcTransport = true;

        await using var poller = new StatePoller(
            harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(1), poller.Interval);
    }

    [Fact]
    public async Task An_explicit_poll_interval_wins_over_the_xpc_default()
    {
        await using var harness = await ContainerTestHarness.CreateAsync(options => options.PollIntervalSeconds = 7);
        harness.Runtime.IsXpcTransport = true;

        await using var poller = new StatePoller(
            harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(7), poller.Interval);
    }

    [Fact]
    public async Task An_explicit_poll_interval_wins_over_the_cli_default()
    {
        await using var harness = await ContainerTestHarness.CreateAsync(options => options.PollIntervalSeconds = 9);

        await using var poller = new StatePoller(
            harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(9), poller.Interval);
    }

    private static StatePoller NewPoller(ContainerTestHarness harness) =>
        new(harness.Containers, harness.Runtime, harness.Events, harness.Options, NullLogger<StatePoller>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(20),
            FastInterval = TimeSpan.FromMilliseconds(10),
        };
}
