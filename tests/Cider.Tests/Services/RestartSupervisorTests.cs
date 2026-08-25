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
            // Comfortably above scheduling jitter under load (a busy CI runner can lose tens of ms
            // between the delay elapsing and the retry actually running), so the doubling still
            // shows up cleanly in the gap ratios below.
            InitialBackoff = TimeSpan.FromMilliseconds(175),
            MaxBackoff = TimeSpan.FromSeconds(10),
        };
        await supervisor.StartAsync(default);

        await using var collector = await harness.CollectEventsAsync();

        // "exit 1" dies at once every time, well under the (default, 10 s) stable-run threshold,
        // so the delay before each attempt should keep doubling: ~175, ~350, ~700 ms.
        var record = await harness.CreateShellAsync("exit 1", "flap", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await harness.Containers.StartAsync(record.Id, default);

        // Wait on the collected "restart" events themselves, not record.RestartCount: MarkRestarting
        // bumps RestartCount and persists before RestartAsync publishes the event, and the collector
        // delivers asynchronously, so record.RestartCount could reach 4 a moment before the 4th
        // "restart" message has actually landed in collector.Messages — leaving the snapshot below
        // one event short on a slow run.
        await ContainerTestHarness.WaitUntilAsync(
            () => collector.Messages.Count(message => string.Equals(message.Action, "restart", StringComparison.Ordinal)) >= 4,
            "four restart events to be collected",
            timeoutMs: 15_000);
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

        // Loosened from a strict "roughly doubles" to "clearly grows": under load, wall-clock gaps
        // for 175/350/700 ms scheduled delays are noisy, but a genuine doubling policy still clears
        // a 1.25x step easily, while a flat (non-backing-off) loop would not.
        Assert.True(gap2 > gap1 * 1.25, $"expected the 2nd gap to grow past the 1st: {gap1} ms -> {gap2} ms");
        Assert.True(gap3 > gap2 * 1.25, $"expected the 3rd gap to grow past the 2nd: {gap2} ms -> {gap3} ms");
    }

    [Fact]
    public async Task Backoff_resets_once_the_container_stays_up_past_the_stable_threshold()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = new RestartSupervisor(harness.Containers, harness.Events, NullLogger<RestartSupervisor>.Instance)
        {
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(5),
            StableRunThreshold = TimeSpan.FromMilliseconds(200),
        };
        await supervisor.StartAsync(default);

        await using var collector = await harness.CollectEventsAsync();

        // Every run stays up ~300 ms, comfortably past the 200 ms stable threshold, so each exit
        // should reset the backoff back to the initial delay instead of doubling. At this scale
        // (~400 ms gaps: 300 ms sleep + 100 ms backoff) there is real headroom against
        // scheduling/process-spawn jitter, unlike the 120 ms gaps a tighter timing produces.
        var record = await harness.CreateShellAsync("sleep 0.3; exit 1", "steady", request =>
            request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await harness.Containers.StartAsync(record.Id, default);

        // Wait on the collected "restart" events themselves, not record.RestartCount: MarkRestarting
        // bumps RestartCount and persists before RestartAsync publishes the event, and the collector
        // delivers asynchronously, so record.RestartCount could reach 3 a moment before the 3rd
        // "restart" message has actually landed in collector.Messages — leaving the snapshot below
        // one event short on a slow run (the same race commit 64fef25 hardened Backoff_doubles_...
        // against).
        await ContainerTestHarness.WaitUntilAsync(
            () => collector.Messages.Count(message => string.Equals(message.Action, "restart", StringComparison.Ordinal)) >= 3,
            "three restart events to be collected",
            timeoutMs: 15_000);
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

        // A doubling loop would roughly double this gap every cycle; a reset keeps it flat. Bounded
        // against the configured backoff itself (not a bare multiplier) so the tolerance scales with
        // the timing this test actually uses.
        Assert.True(
            gap2 < gap1 + 2 * supervisor.InitialBackoff.TotalMilliseconds,
            $"expected the backoff to reset (flat gaps), not double: {gap1} ms -> {gap2} ms");
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

    [Fact]
    public async Task A_started_process_that_reports_the_container_gone_stops_without_restarting()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        // Unlike the case above, the start call itself never throws here: a warm tty cache
        // (AppleContainerRuntime._ttyByContainer) can let `container start -a` spawn successfully
        // even though Apple's own container table has already dropped the runtime id (cider-msj).
        // RestartSupervisor never sees a thrown NotFound in that case, only an ordinary "die" — so
        // ContainerManager.HandleExitAsync classifies the started process's own stderr as only a
        // pre-filter, then confirms against the runtime itself: the fake's tiny shell interpreter
        // writes a "container ... not found" line to stderr before exiting non-zero, the way
        // Apple's CLI would, and VanishContainer below drops the id from the fake's table the same
        // way Apple loses track of a container when its services restart, so InspectContainerAsync
        // reports it gone too.
        var record = await harness.RunShellAsync(
            "sleep 0.05; echo Error: container not found 1>&2; exit 1",
            "gone",
            request => request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        harness.Runtime.VanishContainer(record.RuntimeId);

        await ContainerTestHarness.WaitUntilAsync(
            () => record.State.Status == "exited" && record.State.Error is not null,
            "the supervisor to recognize the container is gone and give up",
            timeoutMs: 5000);
        await Task.Delay(150);

        // Recognized on the very first exit, before any restart was ever scheduled: no reschedule,
        // and RestartCount never leaves zero.
        Assert.Equal(0, record.RestartCount);
        Assert.Equal("exited", record.State.Status);
        Assert.Equal(RestartSupervisor.VanishedError, record.State.Error);
    }

    [Fact]
    public async Task An_apps_own_container_not_found_stderr_does_not_stop_it_restarting_when_the_runtime_still_knows_it()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var supervisor = NewSupervisor(harness);
        await supervisor.StartAsync(default);

        // The application's own stderr happens to match the same wording the vanished-container
        // heuristic looks for (it could be printing its own "no such container" log line, or piping
        // through something that mimics Docker's error text) but the runtime still knows the
        // container exists — the fake's table was never touched. That must not be enough on its own
        // to stamp VanishedError: it is ordinary application output (Broadcast writes the same bytes
        // to the container log the app itself sees), not a runtime-confirmed removal, so the
        // restart policy still applies normally.
        var record = await harness.RunShellAsync(
            "echo Error: No such container: foo 1>&2; exit 1",
            "chatty",
            request => request.HostConfig = new HostConfig { RestartPolicy = new RestartPolicy { Name = "always" } });

        await ContainerTestHarness.WaitUntilAsync(
            () => record.RestartCount >= 1,
            "the container to restart despite the app's own stderr line");
        await supervisor.StopAsync();

        Assert.True(record.RestartCount >= 1);
        Assert.NotEqual(RestartSupervisor.VanishedError, record.State.Error);
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
