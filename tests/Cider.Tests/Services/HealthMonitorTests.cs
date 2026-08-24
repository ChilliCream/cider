using Cider.Core.DockerApi.Models;
using Cider.Core.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class HealthMonitorTests
{
    [Fact]
    public async Task A_passing_probe_turns_the_container_healthy_and_emits_the_event()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var monitor = NewMonitor(harness);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.Healthcheck = new HealthConfig { Test = ["CMD", "true"], Retries = 1 });

        Assert.Equal("starting", record.State.Health!.Status);

        await monitor.TickAsync(default);
        await ContainerTestHarness.WaitUntilAsync(() => record.State.Health!.Status == "healthy", "a healthy container");

        var probe = Assert.Single(record.State.Health!.Log);
        Assert.Equal(0, probe.ExitCode);
        Assert.EndsWith("Z", probe.Start, StringComparison.Ordinal);

        await events.WaitForAsync("health_status: healthy");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task A_failing_probe_turns_the_container_unhealthy_after_the_retries()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        await using var monitor = NewMonitor(harness);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.Healthcheck = new HealthConfig
            {
                Test = ["CMD", "false"],
                Retries = 2,
                Interval = 1_000_000,   // 1 ms, so a second tick probes again
            });

        await monitor.TickAsync(default);
        await ContainerTestHarness.WaitUntilAsync(() => record.State.Health!.FailingStreak == 1, "one failure");
        Assert.Equal("starting", record.State.Health!.Status);

        await Task.Delay(10);
        await monitor.TickAsync(default);
        await ContainerTestHarness.WaitUntilAsync(() => record.State.Health!.Status == "unhealthy", "an unhealthy container");

        Assert.Equal(2, record.State.Health!.FailingStreak);
        Assert.Equal(1, record.State.Health!.Log[^1].ExitCode);
        await events.WaitForAsync("health_status: unhealthy");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task A_CMD_SHELL_probe_runs_through_the_shell()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var monitor = NewMonitor(harness);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.Healthcheck = new HealthConfig { Test = ["CMD-SHELL", "exit 0"], Retries = 1 });

        await monitor.TickAsync(default);
        await ContainerTestHarness.WaitUntilAsync(() => record.State.Health!.Status == "healthy", "a healthy container");

        Assert.Contains(harness.Runtime.Calls, call => call.Contains("ExecAsync:web:/bin/sh -c exit 0", StringComparison.Ordinal));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task A_NONE_healthcheck_is_never_probed()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var monitor = NewMonitor(harness);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.Healthcheck = new HealthConfig { Test = ["NONE"] });

        await monitor.TickAsync(default);
        await Task.Delay(50);

        Assert.Null(record.State.Health);
        Assert.DoesNotContain(harness.Runtime.Calls, call => call.StartsWith("ExecAsync:", StringComparison.Ordinal));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task The_probe_log_keeps_the_last_five_results()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var monitor = NewMonitor(harness);

        var record = await harness.RunShellAsync("sleep 30", "web", request =>
            request.Healthcheck = new HealthConfig { Test = ["CMD", "true"], Retries = 1, Interval = 1_000_000 });

        for (var i = 0; i < 7; i++)
        {
            await monitor.TickAsync(default);
            await ContainerTestHarness.WaitUntilAsync(() => record.State.Health!.Log.Count >= Math.Min(i + 1, 5), "a probe result");
            await Task.Delay(5);
        }

        Assert.Equal(5, record.State.Health!.Log.Count);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    private static HealthMonitor NewMonitor(ContainerTestHarness harness) =>
        new(harness.Containers, harness.Execs, harness.Events, harness.Store, NullLogger<HealthMonitor>.Instance)
        {
            TickInterval = TimeSpan.FromMilliseconds(20),
        };
}
