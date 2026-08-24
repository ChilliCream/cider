using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #10 — <c>docker events</c> streams the container lifecycle live and honours filters.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class EventsTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

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

    private static bool Has(string stream, string action) => stream
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(line => string.Equals(line, action, StringComparison.Ordinal));
}
