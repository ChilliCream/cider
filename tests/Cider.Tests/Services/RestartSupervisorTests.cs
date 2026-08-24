using Cider.Core.DockerApi.Models;
using Cider.Core.Restart;
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
