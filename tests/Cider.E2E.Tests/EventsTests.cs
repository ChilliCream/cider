using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>Matches <c>StatePoller.StartAsync</c>'s own startup line (<c>StatePoller.cs:77-82</c>):
    /// <c>"state poller: interval {N}s ({default|configured}, transport {xpc|cli})"</c>.</summary>
    private static readonly Regex StatePollerLine = new(
        @"state poller: interval (?<seconds>\d+)s \((?<source>default|configured), transport (?<transport>xpc|cli)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// task cider-27t's own named E2E verification, closed directly: asserts the daemon's actual
    /// resolved poll interval straight from its own startup log line, rather than trying to infer it
    /// from a die-latency budget. No <c>/events</c>-watching latency test can do that inference — see
    /// the second remark on <see cref="Events_on_xpc_sees_die_within_1_5s_of_container_exit"/>'s doc
    /// comment for why. This needs no container and is deterministic: it asserts the log line's source
    /// token is <c>"default"</c> (proving <see cref="DaemonFixture"/> no longer pins
    /// <c>PollIntervalSeconds</c> explicit — exactly what task cider-27t changed) and that the interval
    /// it resolved to matches the transport table task cider-ede.19 introduced: 1s under xpc, 3s under
    /// the CLI transport.
    /// </summary>
    [E2EFact]
    public void The_daemons_own_log_shows_the_unpinned_transport_aware_poll_interval()
    {
        var match = daemon.DaemonLog
            .Select(line => StatePollerLine.Match(line))
            .FirstOrDefault(m => m.Success);

        Assert.True(
            match is { Success: true },
            "expected a \"state poller: interval …\" startup line in the daemon log; got:\n"
            + string.Join('\n', daemon.DaemonLog));

        var seconds = int.Parse(match!.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        var source = match.Groups["source"].Value;
        var transport = match.Groups["transport"].Value;

        Assert.Equal("default", source);

        var expectedSeconds = transport switch
        {
            "xpc" => 1,
            "cli" => 3,
            _ => throw new InvalidOperationException($"unexpected transport token \"{transport}\" in: {match.Value}"),
        };
        Assert.Equal(expectedSeconds, seconds);
    }

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
    /// Separately, and regardless of transport: this test cannot exercise <c>Interval</c> at all, even
    /// for the (inapplicable) poller-drop path, because it keeps a <c>docker events</c> stream open for
    /// its whole run, and <c>StatePoller.RunAsync</c> switches to <c>FastInterval</c> (a fixed 1s)
    /// whenever <c>EventBus.SubscriberCount</c> is above zero — so an events-watching test times
    /// <c>FastInterval</c>, never <c>Interval</c>, whichever path emits <c>die</c>. Closing the actual
    /// gap this leaves — observing the resolved <c>Interval</c> default end to end — is what
    /// <see cref="The_daemons_own_log_shows_the_unpinned_transport_aware_poll_interval"/> does instead.
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
                    detectionLatency > TimeSpan.Zero,
                    $"detection latency was {detectionLatency.TotalSeconds:F2}s (<= 0): the readiness "
                    + $"anchor at {readyAt:O} overshot the container's real exit, so the budget below "
                    + "was not actually exercised — this run would otherwise pass empty-handed");

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
