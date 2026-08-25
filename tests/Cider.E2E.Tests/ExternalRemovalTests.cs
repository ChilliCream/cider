using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #14 — a container removed behind cider's back with the Apple CLI directly (someone ran
/// <c>container delete</c>/<c>rm -f</c>, or Apple's services restarted and lost it —
/// ARCHITECTURE §6/§9): the state poller drops the stale record after two consecutive misses so its
/// name is free for reuse, and a <c>docker start</c> attempted in the window before that drop says
/// plainly what happened and what to do instead of the runtime's bare "not found".
/// </summary>
/// <remarks>
/// Shares <see cref="RestartableDaemonFixture"/> with <see cref="DaemonRestartTests"/>: a fresh
/// daemon process is needed for the <c>docker start</c> assertion below, because
/// <c>AppleContainerRuntime</c> caches a container's tty flag from its <c>create</c>/<c>start</c>
/// call for the life of the process, and a cache hit skips the inspect that would otherwise notice
/// the container is gone — exactly the gap a real Apple-services hiccup (a genuinely fresh runtime
/// view) reproduces.
/// </remarks>
[Collection(RestartCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ExternalRemovalTests(RestartableDaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task A_container_deleted_through_the_Apple_CLI_is_dropped_and_its_name_freed()
    {
        var name = DaemonFixture.NewName("ext");

        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, Image, "sleep", "300"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            // Stop it first: cider only holds a running container's init process while it is
            // running, and a `docker start` on a record that still looks running to cider is a
            // no-op 304 that never reaches the runtime at all — the case this test is after is a
            // *stopped* record whose runtime container is gone.
            var stop = await daemon.DockerAsync(["stop", "-t", "1", name], timeout: TimeSpan.FromMinutes(2));
            Assert.True(stop.Ok, stop.ToString());

            // A fresh process (see the remarks above): its runtime has no cached tty for this
            // container, so the `docker start` below actually inspects instead of skipping straight
            // to launching `container start`.
            await daemon.RestartAsync();

            // Removed straight through the Apple CLI — cider never sees this, exactly like the
            // "container delete <name>" / "container rm -f" case the bug report describes.
            var delete = await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(60));
            Assert.True(delete.Ok, delete.ToString());

            // A start attempted in the window before the poller has dropped the record (still
            // reachable right after the delete, before two consecutive misses) gets a 404 that says
            // what happened and what to do, not the runtime's bare "not found".
            var start = await daemon.DockerAsync(["start", name], timeout: TimeSpan.FromMinutes(2));
            Assert.False(start.Ok, start.ToString());
            Assert.Contains("removed outside cider", start.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("docker rm", start.Stderr, StringComparison.OrdinalIgnoreCase);

            // Within 3x the poll interval the record is gone for good: `docker ps -a` no longer
            // lists it (the poller needs two consecutive misses before it drops anything).
            var dropped = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var ps = await daemon.DockerAsync(
                        ["ps", "-a", "--format", "{{.Names}}"],
                        timeout: TimeSpan.FromSeconds(30));
                    return ps.Ok && !ps.Stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(name, StringComparer.Ordinal);
                },
                TimeSpan.FromSeconds((daemon.Options.PollIntervalSeconds * 3) + 15),
                TimeSpan.FromSeconds(1));
            Assert.True(dropped, $"container {name} was never dropped after being removed outside cider");

            // The name is free for reuse, exactly as if `docker rm` had been run for it.
            var recreate = await daemon.DockerAsync(
                ["run", "-d", "--name", name, Image, "sleep", "5"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(recreate.Ok, recreate.ToString());
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }
}
