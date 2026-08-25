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

    /// <summary>
    /// task cider-ede.7: whether this run picked the XPC transport (<see cref="RuntimeTransportSelector"/>'s
    /// own "runtime transport: xpc, apiserver …" log line, captured in <see cref="DaemonFixture.DaemonLog"/>)
    /// — the real-exit-code recovery this task adds (<c>ContainerManager.ReconcileAsync</c>'s
    /// <c>ReconcileWaitForExitAsync</c>) only ever runs under XPC (<c>IContainerRuntime.WaitContainerAsync</c>
    /// answers <c>null</c> unconditionally on the CLI transport), so only there does the pre-existing
    /// "exit code unknown (daemon restarted)" expectation stop holding.
    /// </summary>
    private static bool RanUnderXpc(RestartableDaemonFixture fixture) =>
        fixture.DaemonLog.Any(line => line.Contains("runtime transport: xpc", StringComparison.Ordinal));

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

            if (!RanUnderXpc(daemon))
            {
                // The CLI transport has no containerWait equivalent: ContainerManager.Lifecycle.cs's
                // MarkStoppedWithoutHandle is the only thing that ever marks this record exited, and
                // it always reports the exit code as unknown.
                Assert.Contains("unknown", fields[2], StringComparison.OrdinalIgnoreCase);
            }

            // Under XPC, task cider-ede.7's own background wait (fired from ReconcileAsync at
            // startup, since this container was still State.Running when the restarted daemon came
            // up) races MarkStoppedWithoutHandle to observe the very same process exit that `stop`
            // triggers — whichever gets there first decides whether State.Error ends up "unknown" or
            // empty, but the exit code itself is a real one (0/137/143) either way, so only the
            // range is asserted unconditionally.
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

    /// <summary>
    /// E2E #12b (task cider-ede.7 verification): a container that exits entirely on its own — no
    /// <c>stop</c>/<c>kill</c> involved, nothing racing to claim the exit through a different path —
    /// while a daemon restart is in flight reports its real, application-chosen exit code afterwards
    /// under XPC, replacing the "exit code unknown (daemon restarted)" this same scenario reported
    /// before this task (fix direction §4: <c>ReconcileAsync</c>'s <c>containerWait</c> recovery).
    /// </summary>
    [E2EFact]
    public async Task A_container_that_exits_on_its_own_during_a_restart_reports_its_real_exit_code_on_xpc()
    {
        var name = DaemonFixture.NewName("rst-exit");
        // Long enough that the restart below (stop + start of a throwaway in-process daemon) is
        // done well before the guest exits on its own.
        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, Image, "sh", "-c", "sleep 8; exit 7"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            await daemon.RestartAsync();

            var settled = await DaemonFixture.EventuallyAsync(
                async () => (await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}", name)).Stdout.Trim() == "exited",
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1));

            var after = await daemon.DockerAsync(
                "inspect", "-f", "{{.State.Status}}|{{.State.ExitCode}}|{{.State.Error}}", name);
            Assert.True(after.Ok, after.ToString());
            Assert.True(settled, "the container never reached `exited` after restarting: " + after);
            var fields = after.Stdout.Trim().Split('|');
            Assert.Equal("exited", fields[0]);

            if (RanUnderXpc(daemon))
            {
                Assert.Equal("7", fields[1]);
                Assert.DoesNotContain("unknown", fields[2], StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Unchanged pre-existing behavior: the CLI transport cannot recover an exit code it
                // never observed.
                Assert.Contains("unknown", fields[2], StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }
}
