using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Core.Time;
using Xunit;

namespace Cider.Tests.Services;

public sealed class ContainerManagerQueryTests
{
    [Fact]
    public async Task List_hides_stopped_containers_unless_all_is_set()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var running = await harness.RunShellAsync("sleep 30", "up");
        await harness.CreateAsync("alpine", "down");

        var visible = await harness.Containers.ListAsync(all: false, null, false, Filters.Empty, default);
        Assert.Equal(["/up"], visible.Select(summary => summary.Names[0]));

        var all = await harness.Containers.ListAsync(all: true, null, false, Filters.Empty, default);
        Assert.Equal(2, all.Count);

        await harness.Containers.KillAsync(running.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task List_shapes_a_summary_the_way_Docker_does()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web", request =>
        {
            request.Cmd = ["sh", "-c", "sleep 30"];
            request.Labels = new Dictionary<string, string> { ["role"] = "db" };
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["80/tcp"] = [new PortBinding { HostIp = "127.0.0.1", HostPort = "" }] },
            };
        });
        await harness.Containers.StartAsync(record.Id, default);

        var summary = Assert.Single(await harness.Containers.ListAsync(true, null, false, Filters.Empty, default));

        Assert.Equal(record.Id, summary.Id);
        Assert.Equal(["/web"], summary.Names);
        Assert.Equal("alpine:latest", summary.Image);
        Assert.Equal("sh -c sleep 30", summary.Command);
        Assert.Equal("running", summary.State);
        Assert.StartsWith("Up ", summary.Status, StringComparison.Ordinal);
        Assert.Equal("db", summary.Labels["role"]);
        Assert.Equal("bridge", summary.HostConfig.NetworkMode);
        Assert.True(summary.NetworkSettings.Networks.ContainsKey("bridge"));

        var port = Assert.Single(summary.Ports);
        Assert.Equal(80, port.PrivatePort);
        Assert.Equal("tcp", port.Type);
        Assert.Equal("127.0.0.1", port.IP);
        Assert.NotNull(port.PublicPort);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task List_filters_by_name_label_status_and_id()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var web = await harness.CreateAsync("alpine", "web-1", request =>
            request.Labels = new Dictionary<string, string> { ["role"] = "web" });
        await harness.CreateAsync("alpine", "db-1", request =>
            request.Labels = new Dictionary<string, string> { ["role"] = "db" });

        var byName = await harness.Containers.ListAsync(true, null, false, Filters.Parse("""{"name":{"web":true}}"""), default);
        Assert.Equal(["/web-1"], byName.Select(s => s.Names[0]));

        var byLabel = await harness.Containers.ListAsync(true, null, false, Filters.Parse("""{"label":{"role=db":true}}"""), default);
        Assert.Equal(["/db-1"], byLabel.Select(s => s.Names[0]));

        var byStatus = await harness.Containers.ListAsync(true, null, false, Filters.Parse("""{"status":{"created":true}}"""), default);
        Assert.Equal(2, byStatus.Count);

        var byRunning = await harness.Containers.ListAsync(true, null, false, Filters.Parse("""{"status":{"running":true}}"""), default);
        Assert.Empty(byRunning);

        var byId = await harness.Containers.ListAsync(true, null, false, Filters.Parse("{\"id\":{\"" + web.Id[..10] + "\":true}}"), default);
        Assert.Single(byId);

        var byAncestor = await harness.Containers.ListAsync(true, null, false, Filters.Parse("""{"ancestor":{"alpine:latest":true}}"""), default);
        Assert.Equal(2, byAncestor.Count);
    }

    [Fact]
    public async Task List_honours_limit_and_returns_the_newest_first()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.CreateAsync("alpine", "first");
        await Task.Delay(10);
        await harness.CreateAsync("alpine", "second");

        var all = await harness.Containers.ListAsync(true, null, false, Filters.Empty, default);
        Assert.Equal(["/second", "/first"], all.Select(s => s.Names[0]));

        var limited = await harness.Containers.ListAsync(true, 1, false, Filters.Empty, default);
        Assert.Equal(["/second"], limited.Select(s => s.Names[0]));
    }

    [Fact]
    public async Task Inspect_fills_the_Docker_shape()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web", request =>
        {
            request.Cmd = ["sh", "-c", "sleep 30"];
            request.HostConfig = new HostConfig
            {
                PortBindings = { ["80/tcp"] = [new PortBinding()] },
                Binds = ["/host:/data"],
            };
        });
        await harness.Containers.StartAsync(record.Id, default);

        // cider-ede.26: address registration is a detached follow-up of Start now, no longer
        // something guaranteed complete by the time it returns, so this waits for it explicitly
        // before inspecting the network settings it fills in.
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the container's DNS name to be registered");

        var inspect = await harness.Containers.InspectAsync(record.Id, size: false, default);

        Assert.Equal(record.Id, inspect.Id);
        Assert.Equal("/web", inspect.Name);
        Assert.Equal("sh", inspect.Path);
        Assert.Equal(["-c", "sleep 30"], inspect.Args);
        Assert.Equal("running", inspect.State.Status);
        Assert.True(inspect.State.Running);
        Assert.False(inspect.State.Paused);
        Assert.EndsWith("Z", inspect.Created, StringComparison.Ordinal);
        Assert.NotEqual(DockerTime.ZeroTime, inspect.State.StartedAt);
        Assert.Equal(DockerTime.ZeroTime, inspect.State.FinishedAt);
        Assert.Equal("apple-container", inspect.Driver);
        Assert.Equal("linux", inspect.Platform);
        Assert.Equal(harness.Logs.PathFor(record.Id), inspect.LogPath);
        Assert.Equal(record.ImageId, inspect.Image);
        Assert.Equal(["sh", "-c", "sleep 30"], inspect.Config.Cmd!);
        Assert.Equal("/data", Assert.Single(inspect.Mounts).Destination);
        Assert.NotEmpty(inspect.NetworkSettings.Ports["80/tcp"]!);
        Assert.StartsWith("192.168.64.", inspect.NetworkSettings.Networks["bridge"].IPAddress, StringComparison.Ordinal);
        Assert.StartsWith("192.168.64.", inspect.NetworkSettings.IPAddress, StringComparison.Ordinal);
        Assert.Equal(0, inspect.NetworkSettings.Networks["bridge"].GwPriority);

        Assert.Equal("private", inspect.HostConfig.CgroupnsMode);
        Assert.Equal("private", inspect.HostConfig.IpcMode);
        Assert.Equal("apple-container", inspect.HostConfig.Runtime);

        // Real dockerd reports exactly these twelve masked and five read-only paths, in this order
        // (moby's oci/defaults.go), for every non-privileged container.
        Assert.Equal(
            [
                "/proc/asound",
                "/proc/acpi",
                "/proc/interrupts",
                "/proc/kcore",
                "/proc/keys",
                "/proc/latency_stats",
                "/proc/timer_list",
                "/proc/timer_stats",
                "/proc/sched_debug",
                "/proc/scsi",
                "/sys/firmware",
                "/sys/devices/virtual/powercap",
            ],
            inspect.HostConfig.MaskedPaths!);
        Assert.Equal(
            ["/proc/bus", "/proc/fs", "/proc/irq", "/proc/sys", "/proc/sysrq-trigger"],
            inspect.HostConfig.ReadonlyPaths!);
        Assert.NotNull(inspect.HostConfig.LogConfig.Config);
        Assert.Empty(inspect.HostConfig.LogConfig.Config);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Inspect_leaves_client_supplied_HostConfig_fields_untouched()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "explicit", request =>
        {
            request.HostConfig = new HostConfig
            {
                CgroupnsMode = "host",
                IpcMode = "shareable",
                Runtime = "runc",
                MaskedPaths = ["/proc/custom"],
                ReadonlyPaths = [],
            };
        });

        var inspect = await harness.Containers.InspectAsync(record.Id, size: false, default);

        Assert.Equal("host", inspect.HostConfig.CgroupnsMode);
        Assert.Equal("shareable", inspect.HostConfig.IpcMode);
        Assert.Equal("runc", inspect.HostConfig.Runtime);
        Assert.Equal(["/proc/custom"], inspect.HostConfig.MaskedPaths);
        Assert.Empty(inspect.HostConfig.ReadonlyPaths!);
    }

    [Fact]
    public async Task Inspect_does_not_write_its_synthesized_defaults_into_the_stored_record()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");

        await harness.Containers.InspectAsync(record.Id, size: false, default);
        await harness.Containers.InspectAsync(record.Id, size: false, default);

        // `CreateAsync` always leaves a HostConfig on the record, so inspect has a live object to
        // fill in — but the cgroup/IPC mode, the runtime and the masked/read-only paths belong to
        // the response only. Writing them into the request would make the state file claim the
        // client had sent them.
        var stored = harness.Store.Get(record.Id)!.Request.HostConfig!;
        Assert.Equal("", stored.CgroupnsMode);
        Assert.Equal("", stored.IpcMode);
        Assert.Equal("", stored.Runtime);
        Assert.Null(stored.MaskedPaths);
        Assert.Null(stored.ReadonlyPaths);
    }

    [Fact]
    public async Task Inspect_reports_no_masked_or_readonly_paths_for_a_privileged_container()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "priv", request =>
            request.HostConfig = new HostConfig { Privileged = true });

        var inspect = await harness.Containers.InspectAsync(record.Id, size: false, default);

        Assert.Empty(inspect.HostConfig.MaskedPaths!);
        Assert.Empty(inspect.HostConfig.ReadonlyPaths!);
    }

    [Fact]
    public async Task Resolve_accepts_id_name_and_unique_prefix_and_rejects_ambiguity()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");

        Assert.Equal(record.Id, (await harness.Containers.ResolveAsync(record.Id, default)).Id);
        Assert.Equal(record.Id, (await harness.Containers.ResolveAsync("web", default)).Id);
        Assert.Equal(record.Id, (await harness.Containers.ResolveAsync("/web", default)).Id);
        Assert.Equal(record.Id, (await harness.Containers.ResolveAsync(record.Id[..12], default)).Id);

        var missing = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.ResolveAsync("nope", default));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("No such container: nope", missing.Message);
    }

    [Fact]
    public async Task An_ambiguous_prefix_is_a_400()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // Two records sharing a prefix, written straight into the store.
        harness.Store.Upsert("ab1", NewRecord("ab1"));
        harness.Store.Upsert("ab2", NewRecord("ab2"));

        var error = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.ResolveAsync("ab", default));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
        Assert.Equal("Multiple IDs found with provided prefix: ab", error.Message);
    }

    [Theory]
    [InlineData("created", 0, "Created")]
    [InlineData("exited", 3, "Exited (3) 5 seconds ago")]
    [InlineData("dead", 0, "Dead")]
    [InlineData("removing", 0, "Removal In Progress")]
    public void Status_text_matches_Docker(string status, int exitCode, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var record = NewRecord("id");
        record.State.Status = status;
        record.State.ExitCode = exitCode;
        record.State.FinishedAt = now.AddSeconds(-5);

        Assert.Equal(expected, ContainerManager.FormatStatus(record, now));
    }

    [Fact]
    public void Running_status_text_includes_the_health()
    {
        var now = DateTimeOffset.UtcNow;
        var record = NewRecord("id");
        record.State.Status = "running";
        record.State.StartedAt = now.AddSeconds(-3);
        Assert.Equal("Up 3 seconds", ContainerManager.FormatStatus(record, now));

        record.State.Health = new Core.State.HealthState { Status = "healthy" };
        Assert.Equal("Up 3 seconds (healthy)", ContainerManager.FormatStatus(record, now));
    }

    [Theory]
    [InlineData(0.5, "Less than a second")]
    [InlineData(1, "1 second")]
    [InlineData(45, "45 seconds")]
    [InlineData(61, "About a minute")]
    [InlineData(600, "10 minutes")]
    [InlineData(3600, "About an hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(60 * 60 * 72, "3 days")]
    public void HumanDuration_matches_Gos_units_HumanDuration(double seconds, string expected) =>
        Assert.Equal(expected, ContainerManager.HumanDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Stats_are_mapped_into_Dockers_shape_with_a_previous_sample()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30", "web");

        var first = await harness.Containers.StatsAsync(record.Id, default);
        Assert.Equal(record.Id, first.Id);
        Assert.Equal("/web", first.Name);
        Assert.Equal(DockerTime.ZeroTime, first.Preread);
        Assert.Equal(1_500_000UL * 1000UL, first.CpuStats.CpuUsage.TotalUsage);
        Assert.NotNull(first.CpuStats.SystemCpuUsage);
        Assert.Equal((uint)Environment.ProcessorCount, first.CpuStats.OnlineCpus);
        Assert.Equal(32UL * 1024 * 1024, first.MemoryStats.Usage);
        Assert.Equal(2UL * 1024 * 1024 * 1024, first.MemoryStats.Limit);
        Assert.Equal(3UL, first.PidsStats.Current);
        Assert.Equal(1024UL, first.Networks!["eth0"].RxBytes);
        Assert.Equal(2048UL, first.Networks["eth0"].TxBytes);

        harness.Runtime.Stats = harness.Runtime.Stats! with { CpuUsageUsec = 2_500_000, ReadAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        var second = await harness.Containers.StatsAsync(record.Id, default);

        Assert.Equal(first.Read, second.Preread);
        Assert.Equal(first.CpuStats.CpuUsage.TotalUsage, second.PreCpuStats.CpuUsage.TotalUsage);
        Assert.True(second.CpuStats.SystemCpuUsage >= first.CpuStats.SystemCpuUsage);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Stats_of_a_stopped_container_are_zeroed()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");

        var stats = await harness.Containers.StatsAsync(record.Id, default);

        Assert.Equal(0UL, stats.MemoryStats.Usage);
        Assert.Equal(0UL, stats.CpuStats.CpuUsage.TotalUsage);
        Assert.Equal("/web", stats.Name);
    }

    [Fact]
    public async Task Count_reports_totals_per_status()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await harness.CreateAsync("alpine", "one");
        var running = await harness.RunShellAsync("sleep 30", "two");

        Assert.Equal(2, harness.Containers.Count());
        Assert.Equal(1, harness.Containers.Count("running"));
        Assert.Equal(1, harness.Containers.Count("created"));

        await harness.Containers.KillAsync(running.Id, "SIGKILL", default);
    }

    private static Core.State.ContainerRecord NewRecord(string id) => new()
    {
        Id = id,
        Name = "n" + id,
        RuntimeId = id,
        Created = DateTimeOffset.UtcNow,
        Request = new ContainerCreateRequest { Image = "alpine" },
    };
}
