using System.Globalization;
using Cider.Daemon.Hosting;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #2 — the detached lifecycle: run, ps/inspect, exec, logs, stop, rm.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class LifecycleTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task Detached_container_is_inspectable_execable_and_stops_cleanly()
    {
        var name = DaemonFixture.NewName("life");
        var run = await daemon.DockerAsync(["run", "-d", "--name", name, Image, "sleep", "120"], timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());
        var id = run.Stdout.Trim();
        Assert.Equal(64, id.Length);

        try
        {
            // ---- ps ----
            var ps = await daemon.DockerAsync("ps", "--format", "{{.Names}}|{{.Image}}|{{.State}}");
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains($"{name}|{Image}|running", ps.Stdout, StringComparison.Ordinal);

            // ---- inspect ----
            var inspect = await daemon.DockerAsync(
                "inspect",
                "-f",
                "{{.State.Running}}|{{.NetworkSettings.IPAddress}}|{{index .NetworkSettings.Networks \"bridge\" | printf \"%v\" | len}}",
                name);
            Assert.True(inspect.Ok, inspect.ToString());
            var fields = inspect.Stdout.Trim().Split('|');
            Assert.Equal("true", fields[0]);
            Assert.False(string.IsNullOrWhiteSpace(fields[1]), "NetworkSettings.IPAddress was empty: " + inspect);
            Assert.True(int.Parse(fields[2], CultureInfo.InvariantCulture) > 0, "NetworkSettings.Networks has no 'bridge' entry: " + inspect);

            var bridgeIp = await daemon.DockerAsync("inspect", "-f", "{{.NetworkSettings.Networks.bridge.IPAddress}}", name);
            Assert.True(bridgeIp.Ok, bridgeIp.ToString());
            Assert.Equal(fields[1], bridgeIp.Stdout.Trim());

            // ---- exec, straight after start ----
            var exec = await daemon.DockerAsync(["exec", name, "echo", "ok"], timeout: TimeSpan.FromMinutes(2));
            Assert.True(exec.Ok, exec.ToString());
            Assert.Equal("ok", exec.Stdout.Trim());

            var execStreams = await daemon.DockerAsync(["exec", name, "sh", "-c", "echo o; echo e 1>&2; exit 7"]);
            Assert.Equal(7, execStreams.ExitCode);
            Assert.Equal("o", execStreams.Stdout.Trim());
            Assert.Equal("e", execStreams.Stderr.Trim());

            // ---- exec -i (stdin) ----
            var stdin = await daemon.DockerAsync(["exec", "-i", name, "cat"], stdin: "abc\n");
            Assert.True(stdin.Ok, stdin.ToString());
            Assert.Equal("abc", stdin.Stdout.Trim());

            // ---- stop / exit code ----
            var stop = await daemon.DockerAsync(["stop", "-t", "3", name], timeout: TimeSpan.FromMinutes(2));
            Assert.True(stop.Ok, stop.ToString());

            var state = await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}|{{.State.Running}}|{{.State.ExitCode}}", name);
            Assert.True(state.Ok, state.ToString());
            var after = state.Stdout.Trim().Split('|');
            Assert.Equal("exited", after[0]);
            Assert.Equal("false", after[1]);
            var exitCode = int.Parse(after[2], CultureInfo.InvariantCulture);
            Assert.True(
                exitCode is 137 or 143 or 0,
                $"a SIGTERM/SIGKILLed `sleep` should report 143/137 (or 0 when the code was lost), got {exitCode}: {state}");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }

        var gone = await daemon.DockerAsync("inspect", name);
        Assert.False(gone.Ok, gone.ToString());
        Assert.Contains("No such", gone.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task Run_with_piped_stdin_delivers_the_input_and_its_eof()
    {
        // `docker run -i` attaches before it starts the container and copies stdin at once, so a
        // piped run hands the daemon its input — and its EOF — while there is still no process.
        // Both have to reach the container, or `sh` never runs the line and never exits.
        var run = await daemon.DockerAsync(
            ["run", "--rm", "-i", Image, "sh"],
            stdin: "echo attach-ok\n",
            timeout: TimeSpan.FromMinutes(4));

        Assert.True(run.Ok, run.ToString());
        Assert.Equal("attach-ok", run.Stdout.Trim());
    }

    [E2EFact]
    public async Task Logs_capture_the_init_process_output_with_timestamps()
    {
        var name = DaemonFixture.NewName("logs");
        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, Image, "sh", "-c", "echo L1; echo L2 1>&2; sleep 60"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var got = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var logs = await daemon.DockerAsync("logs", name);
                    return logs.Stdout.Contains("L1", StringComparison.Ordinal)
                        && logs.Stderr.Contains("L2", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(30));

            var final = await daemon.DockerAsync("logs", name);
            Assert.True(got, "expected L1 on stdout and L2 on stderr: " + final);

            var stamped = await daemon.DockerAsync("logs", "--timestamps", name);
            Assert.True(stamped.Ok, stamped.ToString());
            var firstLine = stamped.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z ", firstLine);
            Assert.Contains("L1", firstLine, StringComparison.Ordinal);

            var tail = await daemon.DockerAsync("logs", "--tail", "1", name);
            Assert.True(tail.Ok, tail.ToString());
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// cider-ede.9: <c>docker logs</c> for a container the Apple CLI started directly (never went
    /// through cider's own <c>LogStore</c> capture — <c>ContainerManager.LogsAsync</c>,
    /// <c>_logs.HasCapture</c> is false) reads the apiserver's merged <c>stdio.log</c> fd through
    /// <c>XpcContainerRuntime.OpenLogsAsync</c> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.10),
    /// exactly like today's CLI-fallback behaviour (docs/spikes/xpc/03-limitations-audit-1.3.md "Logs
    /// merged for containers the daemon did not start" row) — and <c>docker logs -f</c> ends once the
    /// container stops, even though the merged file itself never signals that on its own
    /// (<see cref="Cider.AppleContainer.Xpc.XpcContainerRuntime"/>'s stop-watcher supplies it).
    /// </summary>
    [E2EFact]
    public async Task Logs_on_a_container_started_outside_cider_read_the_merged_apiserver_file()
    {
        var name = DaemonFixture.NewName("ext");
        var run = await Cmd.RunAsync(
            "container",
            ["run", "-d", "--name", name, Image, "sh", "-c", "echo hi; sleep 30"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            // cider never created this container — StatePoller only reconciles records it already
            // has (StatePoller.PollOnceAsync: EnumerateRecords), so a container the Apple CLI
            // started directly is only adopted by an explicit sync — POST /_cider/sync, the
            // endpoint behind `cider sync` (StateSynchronizer, SyncTests.PostSyncAsync).
            using (var client = DaemonClient.Create(daemon.Options.SocketPath, TimeSpan.FromMinutes(2)))
            using (var sync = await client.PostAsync(new Uri("/_cider/sync", UriKind.Relative), content: null))
            {
                var body = await sync.Content.ReadAsStringAsync();
                Assert.True(sync.IsSuccessStatusCode, $"POST /_cider/sync -> {(int)sync.StatusCode}: {body}");
            }

            var ps = await daemon.DockerAsync(["ps", "-a", "--format", "{{.Names}}"], timeout: TimeSpan.FromSeconds(30));
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains(name, ps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var sawHi = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    var logs = await daemon.DockerAsync("logs", name);
                    return logs.Ok && logs.Stdout.Contains("hi", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(30));
            var final = await daemon.DockerAsync("logs", name);
            Assert.True(sawHi, "expected 'hi' from the foreign container's merged apiserver log: " + final);

            // docker logs -f must end on its own once the container stops — the stop-watcher polls
            // containerList and calls FollowingFileStream.Stop the moment it is no longer running.
            var followTask = daemon.DockerAsync(["logs", "-f", name], timeout: TimeSpan.FromSeconds(45));
            await Task.Delay(TimeSpan.FromSeconds(1));

            var stop = await Cmd.RunAsync("container", ["stop", name], timeout: TimeSpan.FromSeconds(60));
            Assert.True(stop.Ok, stop.ToString());

            var follow = await followTask;
            Assert.True(follow.Ok, follow.ToString());
            Assert.Contains("hi", follow.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
            await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(30));
        }
    }
}
