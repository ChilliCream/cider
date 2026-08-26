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
    /// exit". Task cider-27t un-pins <see cref="DaemonFixture.PollIntervalOverride"/> so the shared
    /// <see cref="DaemonCollection"/> daemon actually runs the transport-aware default (1s on xpc)
    /// instead of a fixture-pinned 2s, which is what makes this budget exercisable at all. The
    /// container exits entirely on its own (no <c>stop</c>/<c>kill</c> racing a different detection
    /// path), so <c>die</c> can only ever come from the poller/xpc-wait noticing the guest's own exit.
    /// Off xpc (the CLI transport, or a machine <c>RuntimeTransportSelector</c> fell back from) the
    /// budget is not asserted — the CLI default is 3s, above the 1.5s bar by design — but the event
    /// must still eventually show up.
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
                ["run", "-d", "--name", name, Image, "sh", "-c", $"sleep {sleepSeconds}"],
                timeout: TimeSpan.FromMinutes(3));
            Assert.True(run.Ok, run.ToString());

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
                // Time from the container's own recorded exit (docker inspect's FinishedAt), not from
                // when we issued `run` — that would fold create/start (and any cold-cache pull) latency
                // into what is supposed to be a pure event-detection budget.
                var inspect = await daemon.DockerAsync(
                    ["inspect", "-f", "{{.State.FinishedAt}}", name],
                    timeout: TimeSpan.FromMinutes(1));
                Assert.True(inspect.Ok, inspect.ToString());

                var finishedAt = DateTimeOffset.Parse(
                    inspect.Stdout.Trim(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                var detectionLatency = observedAt - finishedAt;

                Assert.True(
                    detectionLatency <= TimeSpan.FromSeconds(1.5),
                    $"die was observed {detectionLatency.TotalSeconds:F2}s after the container's own "
                    + $"exit at {finishedAt:O} (budget 1.5s)");
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
