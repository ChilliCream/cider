using System.Globalization;
using System.Linq;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #9 — the daemon's own restart supervisor and healthcheck probe against real VMs.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class RestartHealthTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task On_failure_restart_policy_retries_a_failing_container_then_gives_up()
    {
        var name = DaemonFixture.NewName("restart");
        var run = await daemon.DockerAsync(
            ["run", "-d", "--restart", "on-failure:2", "--name", name, Image, "sh", "-c", "exit 1"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var restarted = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var count = await daemon.DockerAsync("inspect", "-f", "{{.RestartCount}}", name);
                    return count.Ok
                        && int.TryParse(count.Stdout.Trim(), CultureInfo.InvariantCulture, out var value)
                        && value >= 1;
                },
                TimeSpan.FromSeconds(90),
                TimeSpan.FromSeconds(2));

            var final = await daemon.DockerAsync("inspect", "-f", "{{.RestartCount}}|{{.State.Status}}|{{.HostConfig.RestartPolicy.Name}}|{{.HostConfig.RestartPolicy.MaximumRetryCount}}", name);
            Assert.True(restarted, "the container was never restarted: " + final);
            var fields = final.Stdout.Trim().Split('|');
            Assert.Equal("on-failure", fields[2]);
            Assert.Equal("2", fields[3]);

            var settled = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var state = await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}", name);
                    return state.Ok && state.Stdout.Trim() == "exited";
                },
                TimeSpan.FromSeconds(90),
                TimeSpan.FromSeconds(2));

            var after = await daemon.DockerAsync("inspect", "-f", "{{.RestartCount}}|{{.State.Status}}", name);
            Assert.True(settled, "the container never settled in `exited`: " + after);
            Assert.True(
                int.Parse(after.Stdout.Trim().Split('|')[0], CultureInfo.InvariantCulture) <= 2,
                "the supervisor exceeded MaximumRetryCount: " + after);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Unless_stopped_backs_off_instead_of_flapping_and_gives_up_when_removed_outside_cider()
    {
        var name = DaemonFixture.NewName("flap");
        var run = await daemon.DockerAsync(
            ["run", "-d", "--restart", "unless-stopped", "--name", name, Image, "false"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            // `false` exits at once every time, so without backoff this would restart several
            // times a second forever (cider-msj). Give it 30 s of that and see how many times it
            // actually came back — sampling `docker ps` throughout rather than once at the end,
            // since the state poller (cider-4y2) and the supervisor's own "restarting" mark race
            // each other and either can be showing at any single instant.
            var sawRestarting = false;
            string? lastPsLine = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                var ps = await daemon.DockerAsync("ps", "--format", "{{.Names}}|{{.Status}}");
                if (ps.Ok)
                {
                    lastPsLine = ps.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(line => line.StartsWith(name + "|", StringComparison.Ordinal));
                    if (lastPsLine is not null && lastPsLine.Contains("Restarting", StringComparison.Ordinal))
                    {
                        sawRestarting = true;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            Assert.True(sawRestarting, $"{name} was never seen as `Restarting` in `docker ps`, last saw: {lastPsLine ?? "(not listed)"}");

            var inspect = await daemon.DockerAsync("inspect", "-f", "{{.RestartCount}}", name);
            Assert.True(inspect.Ok, inspect.ToString());
            var restartCount = int.Parse(inspect.Stdout.Trim(), CultureInfo.InvariantCulture);
            Assert.True(restartCount <= 10, $"backoff did not slow the flap down: RestartCount={restartCount} after 30s (expected <= 10)");

            // Removed straight through the Apple CLI while cider is mid-backoff: the state poller
            // (cider-4y2) notices on its own schedule and drops the record, independent of whatever
            // the restart supervisor's own (by-now multi-second) backoff delay happens to be.
            var delete = await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(60));
            Assert.True(delete.Ok, delete.ToString());

            var dropped = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var psA = await daemon.DockerAsync(
                        ["ps", "-a", "--format", "{{.Names}}"],
                        timeout: TimeSpan.FromSeconds(30));
                    return psA.Ok && !psA.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(name, StringComparer.Ordinal);
                },
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(250));
            Assert.True(dropped, $"{name} was never dropped after being removed outside cider");

            // No restart-loop spam: at most a handful of lines mention this container (one Warning
            // once the backoff had grown past attempt 5, one for the poller's drop), not thousands.
            var relevant = daemon.DaemonLog.Where(line => line.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.True(relevant.Length < 50, $"expected no restart-loop log spam for {name}, saw {relevant.Length} lines:\n{string.Join('\n', relevant)}");

            var warnings = relevant.Where(line => line.Contains(" Warning ", StringComparison.Ordinal)).ToArray();
            Assert.True(warnings.Length is >= 1 and <= 5, $"expected roughly one Warning for {name}, saw {warnings.Length}:\n{string.Join('\n', warnings)}");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Healthcheck_goes_unhealthy_and_recovers_to_healthy()
    {
        var name = DaemonFixture.NewName("health");
        var run = await daemon.DockerAsync(
            [
                "run", "-d", "--name", name,
                "--health-cmd", "test -f /tmp/ok",
                "--health-interval", "1s",
                "--health-retries", "2",
                "--health-timeout", "5s",
                Image, "sleep", "180",
            ],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var unhealthy = await DaemonFixture.EventuallyAsync(
                async () => (await HealthAsync(name)) == "unhealthy",
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(1));
            Assert.True(unhealthy, "health never became `unhealthy`, it is: " + await HealthAsync(name));

            var touch = await daemon.DockerAsync(["exec", name, "touch", "/tmp/ok"], timeout: TimeSpan.FromMinutes(2));
            Assert.True(touch.Ok, touch.ToString());

            var healthy = await DaemonFixture.EventuallyAsync(
                async () => (await HealthAsync(name)) == "healthy",
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(1));
            Assert.True(healthy, "health never became `healthy`, it is: " + await HealthAsync(name));

            var log = await daemon.DockerAsync("inspect", "-f", "{{len .State.Health.Log}}", name);
            Assert.True(log.Ok, log.ToString());
            Assert.True(int.Parse(log.Stdout.Trim(), CultureInfo.InvariantCulture) > 0, "State.Health.Log is empty: " + log);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    private async Task<string> HealthAsync(string name)
    {
        var status = await daemon.DockerAsync("inspect", "-f", "{{.State.Health.Status}}", name);
        return status.Stdout.Trim();
    }
}
