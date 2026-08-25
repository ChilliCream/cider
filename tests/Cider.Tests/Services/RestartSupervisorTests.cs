using Cider.Core.DockerApi.Models;
using Cider.Core.Restart;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class RestartSupervisorTests
{
    [Fact]
    public async Task On_failure_restarts_until_the_retry_count_is_reached()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        var record = await harness.CreateShellAsync("exit 1", "flaky", request =>
            request.HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = "on-failure", MaximumRetryCount = 2 },
            });

        await harness.Containers.StartAsync(record.Id, default);

        await ContainerTestHarness.WaitUntilAsync(() => record.RestartCount == 2, "two restart attempts");
        await Task.Delay(100);

        Assert.Equal(2, record.RestartCount);
        Assert.Equal("exited", record.State.Status);
        Assert.Equal(1, record.State.ExitCode);
    }

    [Fact]
    public async Task On_failure_does_not_restart_a_clean_exit()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        var record = await harness.CreateShellAsync("exit 0", "clean", request =>
            request.HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = "on-failure" },
            });

        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(0, record.RestartCount);
        Assert.Equal("exited", record.State.Status);
    }

    [Fact]
    public async Task A_user_stop_suppresses_the_always_policy()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        var record = await harness.RunShellAsync("sleep 30", "sticky", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, default);
        await Task.Delay(150);

        Assert.Equal(0, record.RestartCount);
        Assert.Equal("exited", record.State.Status);
    }

    [Fact]
    public async Task The_always_policy_restarts_a_container_that_exits_on_its_own()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        // The container exits on its own, so the policy has to bring it back.
        var record = await harness.CreateShellAsync("sleep 0.15", "loop", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });
        await harness.Containers.StartAsync(record.Id, default);

        await ContainerTestHarness.WaitUntilAsync(() => record.RestartCount >= 1, "one restart");
        await ContainerTestHarness.WaitUntilAsync(
            () => record.State.Status is "running" or "restarting",
            "the container to come back");

        await supervisor.StopAsync();
        record.RestartPolicy = new RestartPolicy();
    }

    [Fact]
    public async Task Backoff_doubles_while_the_container_keeps_exiting_immediately()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = new RestartSupervisor(harness.Containers, harness.Events, NullLogger<RestartSupervisor>.Instance)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(10),
        };
        await supervisor.StartAsync(default);

        await using var collector = await harness.CollectEventsAsync();

        // "exit 1" dies at once every time, well under the (default, 10 s) stable-run threshold,
        // so the delay before each attempt should keep doubling: ~100, ~200, ~400 ms.
        var record = await harness.CreateShellAsync("exit 1", "flap", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await harness.Containers.StartAsync(record.Id, default);

        await ContainerTestHarness.WaitUntilAsync(() => record.RestartCount >= 4, "four restart attempts", timeoutMs: 10_000);
        await supervisor.StopAsync();

        var restarts = collector.Messages
            .Where(message => string.Equals(message.Action, "restart", StringComparison.Ordinal))
            .OrderBy(message => message.TimeNano)
            .Take(4)
            .Select(message => message.TimeNano)
            .ToArray();

        Assert.True(restarts.Length >= 4, $"expected at least 4 restart events, saw {restarts.Length}");

        var gap1 = (restarts[1] - restarts[0]) / 1_000_000.0;
        var gap2 = (restarts[2] - restarts[1]) / 1_000_000.0;
        var gap3 = (restarts[3] - restarts[2]) / 1_000_000.0;

        Assert.True(gap2 > gap1 * 1.4, $"expected the 2nd gap to roughly double the 1st: {gap1} ms -> {gap2} ms");
        Assert.True(gap3 > gap2 * 1.4, $"expected the 3rd gap to roughly double the 2nd: {gap2} ms -> {gap3} ms");
    }

    [Fact]
    public async Task Backoff_resets_once_the_container_stays_up_past_the_stable_threshold()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = new RestartSupervisor(harness.Containers, harness.Events, NullLogger<RestartSupervisor>.Instance)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(30),
            MaxBackoff = TimeSpan.FromSeconds(5),
            StableRunThreshold = TimeSpan.FromMilliseconds(60),
        };
        await supervisor.StartAsync(default);

        await using var collector = await harness.CollectEventsAsync();

        // Every run stays up ~90 ms, comfortably past the 60 ms stable threshold, so each exit
        // should reset the backoff back to the initial delay instead of doubling.
        var record = await harness.CreateShellAsync("sleep 0.09; exit 1", "steady", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await harness.Containers.StartAsync(record.Id, default);

        await ContainerTestHarness.WaitUntilAsync(() => record.RestartCount >= 3, "three restart attempts", timeoutMs: 10_000);
        await supervisor.StopAsync();

        var restarts = collector.Messages
            .Where(message => string.Equals(message.Action, "restart", StringComparison.Ordinal))
            .OrderBy(message => message.TimeNano)
            .Take(3)
            .Select(message => message.TimeNano)
            .ToArray();

        Assert.True(restarts.Length >= 3, $"expected at least 3 restart events, saw {restarts.Length}");

        var gap1 = (restarts[1] - restarts[0]) / 1_000_000.0;
        var gap2 = (restarts[2] - restarts[1]) / 1_000_000.0;

        // A doubling loop would roughly double this gap every cycle; a reset keeps it flat.
        Assert.True(gap2 < gap1 * 1.7, $"expected the backoff to reset (flat gaps), not double: {gap1} ms -> {gap2} ms");
    }

    [Fact]
    public async Task A_vanished_runtime_container_stops_after_exactly_one_attempt()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        // The container runs long enough for the test to arm StartFailure before it exits and the
        // supervisor tries to bring it back.
        var record = await harness.RunShellAsync("sleep 0.3; exit 1", "vanished", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        harness.Runtime.StartFailure = RuntimeException.NotFound($"container {record.RuntimeId} not found");

        await ContainerTestHarness.WaitUntilAsync(
            () => record.State.Status == "exited" && record.State.Error is not null,
            "the restart to give up after the runtime reports the container gone",
            timeoutMs: 5000);
        await Task.Delay(150);

        Assert.Equal(1, record.RestartCount);
        Assert.Equal("exited", record.State.Status);
        Assert.Equal("container no longer exists in Apple container (removed outside cider)", record.State.Error);
    }

    [Theory]
    [InlineData("", 1, false, false)]
    [InlineData("no", 1, false, false)]
    [InlineData("always", 0, false, true)]
    [InlineData("unless-stopped", 0, false, true)]
    [InlineData("always", 0, true, false)]
    [InlineData("on-failure", 0, false, false)]
    [InlineData("on-failure", 1, false, true)]
    public void ShouldRestart_follows_the_policy(string policy, int exitCode, bool userStopped, bool expected)
    {
        var record = new ContainerRecord
        {
            Id = "id",
            Name = "n",
            RuntimeId = "n",
            Created = DateTimeOffset.UtcNow,
            Request = new ContainerCreateRequest { Image = "alpine" },
            RestartPolicy = new RestartPolicy { Name = policy },
            UserStopped = userStopped,
        };
        record.State.ExitCode = exitCode;

        Assert.Equal(expected, RestartSupervisor.ShouldRestart(record));
    }

    private static RestartSupervisor NewSupervisor(ContainerTestHarness harness) =>
        new(harness.Containers, harness.Events, NullLogger<RestartSupervisor>.Instance)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(5),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };
}
