using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #10 — <c>docker events</c> streams the container lifecycle live and honours filters.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class EventsTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    /// <summary>
    /// Whether this run's daemon picked the XPC transport. Log-based, like
    /// <c>DaemonRestartTests.RanUnderXpc</c>: <see cref="DaemonFixture.Options"/>'s
    /// <c>RuntimeTransport</c> stays <c>"auto"</c> either way — only
    /// <see cref="DaemonFixture.DaemonLog"/>'s own "runtime transport: xpc, apiserver …" line (emitted
    /// by <c>RuntimeTransportSelector</c>) says what actually got picked for this process.
    /// </summary>
    private static bool RanUnderXpc(DaemonFixture fixture) =>
        fixture.DaemonLog.Any(line => line.Contains("runtime transport: xpc", StringComparison.Ordinal));

    [E2EFact]
    public async Task Events_stream_create_start_die_and_stop_for_one_container()
    {
        var name = DaemonFixture.NewName("ev");

        await using var events = daemon.DockerBackground("events", "--filter", "container=" + name, "--format", "{{.Action}}");
        await Task.Delay(TimeSpan.FromSeconds(2));

        try
        {
            var create = await daemon.DockerAsync(["create", "--name", name, Image, "sleep", "120"], timeout: TimeSpan.FromMinutes(3));
            Assert.True(create.Ok, create.ToString());

            var start = await daemon.DockerAsync(["start", name], timeout: TimeSpan.FromMinutes(3));
            Assert.True(start.Ok, start.ToString());

            var stop = await daemon.DockerAsync(["stop", "-t", "2", name], timeout: TimeSpan.FromMinutes(3));
            Assert.True(stop.Ok, stop.ToString());

            var seen = await DaemonFixture.EventuallyAsync(
                () => Task.FromResult(
                    Has(events.Stdout, "create") && Has(events.Stdout, "start")
                    && Has(events.Stdout, "die") && Has(events.Stdout, "stop")),
                TimeSpan.FromSeconds(45),
                TimeSpan.FromMilliseconds(500));

            Assert.True(
                seen,
                $"expected create/start/die/stop for {name}; the stream carried:\n{events.Stdout}\n--- stderr ---\n{events.Stderr}");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// cider-ede.19's named E2E verification: "EventsTests on xpc sees die within 1.5s of a container
    /// exit". This budgets the attached-exit → event-stream detection path on xpc: for a container the
    /// daemon itself started, <c>die</c> comes from the attached exit handler
    /// (<c>ContainerManager.HandleExitAsync</c>), because <c>StatePoller</c> skips containers it does
    /// not hold (<c>StatePoller.IsHeldByUs</c>). The transport-aware poll-interval default (1s on xpc,
    /// 3s on the CLI transport) remains covered by the unit tests; task cider-27t un-pinning
    /// <see cref="DaemonFixture.PollIntervalOverride"/> just lets the shared <see cref="DaemonCollection"/>
    /// daemon run under that real default instead of a fixture-pinned value. Off xpc (the CLI transport,
    /// or a machine <c>RuntimeTransportSelector</c> fell back from) the budget is not asserted — the CLI
    /// default is 3s, above the 1.5s bar by design — but the event must still eventually show up.
    /// </summary>
    [E2EFact]
    public async Task Events_on_xpc_sees_die_within_1_5s_of_container_exit()
    {
        var name = DaemonFixture.NewName("ev-die");
        const int sleepSeconds = 2;

        await using var events = daemon.DockerBackground("events", "--filter", "container=" + name, "--format", "{{.Action}}");
        await Task.Delay(TimeSpan.FromSeconds(2));

        try
        {
            var run = await daemon.DockerAsync(
                ["run", "-d", "--name", name, Image, "sh", "-c", $"echo up; sleep {sleepSeconds}"],
                timeout: TimeSpan.FromMinutes(3));
            Assert.True(run.Ok, run.ToString());

            // Anchor on the guest actually being up (its "up" marker showing in `docker logs`), not on
            // when we issued `run` — that would fold create/start/VM-boot/pull latency into what is
            // supposed to be a pure event-detection budget. This also avoids cider's own die-detection
            // instant (docker inspect's FinishedAt is stamped by cider itself at the moment it decides
            // the container died, which would make the assertion self-referential).
            string? logs = null;
            var ready = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var result = await daemon.DockerAsync(["logs", name], timeout: TimeSpan.FromMinutes(1));
                    logs = result.Stdout;
                    return result.Ok && logs.Contains("up", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(100));
            var readyAt = DateTimeOffset.UtcNow;
            Assert.True(ready, $"expected 'up' in logs for {name}; got:\n{logs}");

            var seen = await DaemonFixture.EventuallyAsync(
                () => Task.FromResult(Has(events.Stdout, "die")),
                TimeSpan.FromSeconds(sleepSeconds + 30),
                TimeSpan.FromMilliseconds(100));
            var observedAt = DateTimeOffset.UtcNow;

            Assert.True(
                seen,
                $"expected a die event for {name}; the stream carried:\n{events.Stdout}\n--- stderr ---\n{events.Stderr}");

            if (RanUnderXpc(daemon))
            {
                // The guest's echo happened at or before readyAt, so the container's exit (sleepSeconds
                // later) happened at or before this bound. The only remaining understatement is the
                // log-poll lag, bounded by the 100ms poll interval above — not cider's own unbounded
                // detection instant.
                var exitedAtUpperBound = readyAt + TimeSpan.FromSeconds(sleepSeconds);
                var detectionLatency = observedAt - exitedAtUpperBound;

                Assert.True(
                    detectionLatency <= TimeSpan.FromSeconds(1.5),
                    $"die was observed {detectionLatency.TotalSeconds:F2}s after the container's exit "
                    + $"upper bound at {exitedAtUpperBound:O} (budget 1.5s)");
            }
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    private static bool Has(string stream, string action) => stream
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(line => string.Equals(line, action, StringComparison.Ordinal));
}
