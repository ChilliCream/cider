using System.Globalization;
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
