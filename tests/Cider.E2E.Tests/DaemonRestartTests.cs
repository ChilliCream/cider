using System.Globalization;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>A daemon whose process the test can cycle while its containers keep running.</summary>
public sealed class RestartableDaemonFixture : DaemonFixture
{
    /// <inheritdoc />
    protected override string InstanceSuffix => "r";

    /// <summary>Stops the daemon and starts a fresh one on the very same data dir and socket.</summary>
    public async Task RestartAsync()
    {
        await StopDaemonAsync();
        await StartDaemonAsync();
    }
}

/// <summary>The collection owning the restartable daemon, so the main suite is unaffected.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RestartCollection : ICollectionFixture<RestartableDaemonFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "cider-e2e-restart";
}

/// <summary>
/// E2E #12 — a daemon restart with a container still running: the new process reconciles the
/// container out of Apple's runtime, can still stop and remove it, reports the exit code it could
/// not observe as unknown, and falls back to the runtime's own log for output it never captured.
/// </summary>
[Collection(RestartCollection.Name)]
[Trait("Category", "E2E")]
public sealed class DaemonRestartTests(RestartableDaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task A_running_container_survives_a_daemon_restart_and_stays_manageable()
    {
        var name = DaemonFixture.NewName("rst");
        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, Image, "sh", "-c", "echo BEFORE_RESTART; sleep 300"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());
        var id = run.Stdout.Trim();

        // The captured log exists while this daemon owns the container's stdio.
        var before = await DaemonFixture.EventuallyAsync(
            async () => (await daemon.DockerAsync("logs", name)).Stdout.Contains("BEFORE_RESTART", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
        Assert.True(before, "the container's output was not captured before the restart");

        await daemon.RestartAsync();

        try
        {
            // ---- reconciled: still listed, still running, same id ----
            var ps = await daemon.DockerAsync("ps", "--format", "{{.ID}}|{{.Names}}|{{.State}}");
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains($"{id[..12]}|{name}|running", ps.Stdout, StringComparison.Ordinal);

            var state = await daemon.DockerAsync("inspect", "-f", "{{.State.Running}}|{{.Id}}", name);
            Assert.True(state.Ok, state.ToString());
            Assert.Equal($"true|{id}", state.Stdout.Trim());

            // ---- logs still answer; the runtime's own log is the fallback ----
            var logs = await daemon.DockerAsync("logs", name);
            Assert.True(logs.Ok, logs.ToString());

            // ---- exec still works against a container this daemon never started ----
            var exec = await daemon.DockerAsync(["exec", name, "echo", "still-here"], timeout: TimeSpan.FromMinutes(2));
            Assert.True(exec.Ok, exec.ToString());
            Assert.Equal("still-here", exec.Stdout.Trim());

            // ---- stop works, and the exit code it could not observe is reported as unknown ----
            var stop = await daemon.DockerAsync(["stop", "-t", "3", name], timeout: TimeSpan.FromMinutes(2));
            Assert.True(stop.Ok, stop.ToString());

            var settled = await DaemonFixture.EventuallyAsync(
                async () => (await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}", name)).Stdout.Trim() == "exited",
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1));

            var after = await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}|{{.State.ExitCode}}|{{.State.Error}}", name);
            Assert.True(after.Ok, after.ToString());
            Assert.True(settled, "the container never reached `exited` after stop: " + after);
            var fields = after.Stdout.Trim().Split('|');
            Assert.Equal("exited", fields[0]);
            Assert.Contains("unknown", fields[2], StringComparison.OrdinalIgnoreCase);
            var exitCode = int.Parse(fields[1], CultureInfo.InvariantCulture);
            Assert.True(
                exitCode == 0 || exitCode is 137 or 143,
                "a container the daemon did not start has no recoverable exit code, so 0 (with State.Error) "
                + $"or a signal code is expected, got {exitCode}: {after}");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }
}
