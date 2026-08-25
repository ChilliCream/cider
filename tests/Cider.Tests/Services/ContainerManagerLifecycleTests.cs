using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Logs;
using Cider.Core.Services;
using Xunit;

namespace Cider.Tests.Services;

public sealed class ContainerManagerLifecycleTests
{
    [Fact]
    public async Task Start_marks_the_container_running_and_emits_start()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();

        var record = await harness.CreateShellAsync("sleep 5", "web");
        await harness.Containers.StartAsync(record.Id, default);

        Assert.Equal("running", record.State.Status);
        Assert.True(record.State.Running);
        Assert.NotNull(record.State.StartedAt);
        Assert.NotEqual(0, record.State.Pid);

        await events.WaitForAsync("start");
        Assert.Contains("StartContainerAsync:web", harness.Runtime.Calls);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_of_a_running_container_is_304()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 5", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.StartAsync(record.Id, default));
        Assert.Equal(System.Net.HttpStatusCode.NotModified, error.Status);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_of_a_container_removed_outside_cider_is_a_404_that_says_what_to_do()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 5", "web");

        // Someone ran `container delete`/`rm -f` (or Apple's services restarted and lost it)
        // before the poller ever got a chance to notice: the engine has no idea what this id is.
        await harness.Runtime.RemoveContainerAsync("web", force: true, default);

        var error = await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.StartAsync(record.Id, default));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.Status);
        Assert.Contains("removed outside cider", error.Message, StringComparison.Ordinal);
        Assert.Contains("docker rm web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_registers_the_container_addresses_for_dns()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 5", "web", request =>
            request.Labels = new Dictionary<string, string> { ["com.docker.compose.service"] = "api" });

        Assert.True(harness.NameRegistry.TryResolve("bridge", "web", out var ip));
        Assert.StartsWith("192.168.64.", ip.ToString(), StringComparison.Ordinal);
        Assert.True(harness.NameRegistry.TryResolve("bridge", "api", out _));
        Assert.Equal(ip.ToString(), record.Networks["bridge"].IPAddress);
        Assert.Equal("192.168.64.1", record.Networks["bridge"].Gateway);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
        await ContainerTestHarness.WaitUntilAsync(
            () => !harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the DNS names to be dropped on exit");
    }

    [Fact]
    public async Task Start_polls_past_a_delayed_network_attachment_and_still_registers_dns()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(20);
        var record = await harness.CreateShellAsync("sleep 5", "web");

        // Apple container 1.2.2 reports `status.networks: []` for the first ~1-2s after start; make
        // the fake do the same for the first few inspects so the fix actually gets exercised.
        harness.Runtime.DelayNetworkAttachment("web", 3);

        await harness.Containers.StartAsync(record.Id, default);

        Assert.True(harness.NameRegistry.TryResolve("bridge", "web", out var ip));
        Assert.StartsWith("192.168.64.", ip.ToString(), StringComparison.Ordinal);
        Assert.Equal(ip.ToString(), record.Networks["bridge"].IPAddress);
        Assert.Equal("192.168.64.1", record.Networks["bridge"].Gateway);
        Assert.NotEmpty(record.Networks["bridge"].NetworkID);
        Assert.True(
            harness.Runtime.Calls.Count(call => call.StartsWith("InspectContainerAsync:web", StringComparison.Ordinal)) >= 4,
            "expected Start to have polled inspect past the delayed attachments");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_returns_within_budget_even_when_the_address_never_shows_up()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(10);
        harness.Containers.StartReturnBudget = TimeSpan.FromMilliseconds(30);
        harness.Containers.NetworkPollBudget = TimeSpan.FromMilliseconds(60);
        var record = await harness.CreateShellAsync("sleep 5", "web");

        // Outlives every budget above: Start must give up and return rather than hang on the IP.
        harness.Runtime.DelayNetworkAttachment("web", 1000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await harness.Containers.StartAsync(record.Id, default);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"StartAsync took {stopwatch.Elapsed}");
        Assert.True(record.State.Running);
        Assert.False(harness.NameRegistry.TryResolve("bridge", "web", out _));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Wait_next_exit_returns_the_exit_code_of_the_run_that_follows()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("echo out; echo err 1>&2; exit 3", "web");

        // docker run waits before it starts the container.
        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);

        var response = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, response.StatusCode);
        Assert.Equal("exited", record.State.Status);
        Assert.Equal(3, record.State.ExitCode);
        Assert.NotNull(record.State.FinishedAt);
    }

    [Fact]
    public async Task Wait_not_running_returns_immediately_for_a_container_that_already_exited()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("exit 2", "web");
        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        var response = await harness.Containers.WaitAsync(record.Id, "not-running", default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, response.StatusCode);
    }

    [Fact]
    public async Task Output_is_captured_and_demultiplexed_into_the_log()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("echo out; echo err 1>&2; exit 3", "web");
        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        var entries = new List<LogEntry>();
        await foreach (var entry in harness.Containers.LogsAsync(record.Id, new LogReadOptions(), default))
        {
            entries.Add(entry);
        }

        // stdout and stderr are pumped by two independent tasks, so which of the two lines lands in
        // the log first is genuinely unordered — Docker gives no cross-stream ordering guarantee
        // either (docs/apple-container-notes.md §12). Assert per stream, not by index.
        Assert.Equal(2, entries.Count);
        var stdoutEntry = Assert.Single(entries, entry => entry.Stream == StdStream.Stdout);
        Assert.Equal("out\n", Encoding.UTF8.GetString(stdoutEntry.Data.Span));
        var stderrEntry = Assert.Single(entries, entry => entry.Stream == StdStream.Stderr);
        Assert.Equal("err\n", Encoding.UTF8.GetString(stderrEntry.Data.Span));

        var stdoutOnly = new List<LogEntry>();
        await foreach (var entry in harness.Containers.LogsAsync(record.Id, new LogReadOptions { Stderr = false }, default))
        {
            stdoutOnly.Add(entry);
        }

        Assert.Single(stdoutOnly);
    }

    [Fact]
    public async Task Attach_before_start_receives_the_output_of_the_run()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("echo hello; echo bad 1>&2", "web");

        await using var attachment = await harness.Containers.AttachAsync(record.Id, new AttachOptions(), default);
        await harness.Containers.StartAsync(record.Id, default);

        var chunks = new List<OutputChunk>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in attachment.Output.ReadAllAsync(cts.Token))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(chunks, chunk => chunk.Stream == StdStream.Stdout && Text(chunk) == "hello\n");
        Assert.Contains(chunks, chunk => chunk.Stream == StdStream.Stderr && Text(chunk) == "bad\n");
        await attachment.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attach_forwards_stdin_to_the_container()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "cat", request =>
        {
            request.Cmd = ["cat"];
            request.OpenStdin = true;
            request.AttachStdin = true;
        });

        await using var attachment = await harness.Containers.AttachAsync(
            record.Id,
            new AttachOptions { Stdin = true },
            default);
        await harness.Containers.StartAsync(record.Id, default);

        await attachment.WriteStdinAsync(Encoding.UTF8.GetBytes("ping\n"), default);
        await attachment.CloseStdinAsync();

        var text = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in attachment.Output.ReadAllAsync(cts.Token))
        {
            text.Append(Text(chunk));
        }

        Assert.Equal("ping\n", text.ToString());
    }

    [Fact]
    public async Task Attach_stdin_written_and_half_closed_before_the_start_still_reaches_the_container()
    {
        // `docker run -i` attaches before it starts the container, and its stdin copier runs at
        // once: a piped `echo x | docker run -i ... sh` writes and half-closes while there is no
        // process yet. Both have to survive until the process exists, or the command never sees
        // its input, never sees EOF, and the run hangs.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "cat", request =>
        {
            request.Cmd = ["cat"];
            request.OpenStdin = true;
            request.AttachStdin = true;
        });

        await using var attachment = await harness.Containers.AttachAsync(
            record.Id,
            new AttachOptions { Stdin = true },
            default);

        await attachment.WriteStdinAsync(Encoding.UTF8.GetBytes("ping\n"), default);
        await attachment.CloseStdinAsync();

        await harness.Containers.StartAsync(record.Id, default);

        var text = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in attachment.Output.ReadAllAsync(cts.Token))
        {
            text.Append(Text(chunk));
        }

        Assert.Equal("ping\n", text.ToString());
        await attachment.Exited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Attach_with_logs_replays_the_capture_of_an_exited_container()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("echo replayed", "web");
        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        await using var attachment = await harness.Containers.AttachAsync(
            record.Id,
            new AttachOptions { Logs = true, Stream = false },
            default);

        var chunks = new List<OutputChunk>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in attachment.Output.ReadAllAsync(cts.Token))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("replayed\n", Text(Assert.Single(chunks)));
    }

    [Fact]
    public async Task Stop_terminates_the_container_and_emits_die_then_stop()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        var record = await harness.RunShellAsync("sleep 30", "web");

        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, default);

        Assert.Equal("exited", record.State.Status);
        Assert.Equal(143, record.State.ExitCode);
        Assert.True(record.UserStopped);

        await events.WaitForAsync("die");
        await events.WaitForAsync("stop");
        Assert.Equal("143", events.First("die").Actor.Attributes["exitCode"]);
    }

    [Fact]
    public async Task Stop_of_a_stopped_container_is_304()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 5", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.StopAsync(record.Id, null, null, default));
        Assert.Equal(System.Net.HttpStatusCode.NotModified, error.Status);
    }

    [Fact]
    public async Task Kill_delivers_the_signal_and_emits_kill()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        var record = await harness.RunShellAsync("sleep 30", "web");

        await harness.Containers.KillAsync(record.Id, "KILL", default);

        await events.WaitForAsync("kill");
        Assert.Equal("SIGKILL", events.First("kill").Actor.Attributes["signal"]);
        await ContainerTestHarness.WaitUntilAsync(() => record.State.Status == "exited", "the container to exit");
        Assert.Equal(137, record.State.ExitCode);
    }

    [Fact]
    public async Task Kill_of_a_stopped_container_is_409()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 5", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.KillAsync(record.Id, null, default));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);
    }

    [Fact]
    public async Task Restart_stops_and_starts_again()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        var record = await harness.RunShellAsync("sleep 30", "web");

        await harness.Containers.RestartAsync(record.Id, timeoutSeconds: 1, default);

        Assert.Equal("running", record.State.Status);
        await events.WaitForAsync("restart");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Remove_of_a_running_container_needs_force()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 30", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.RemoveAsync(record.Id, force: false, removeVolumes: false, default));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);
        Assert.Contains("stop the container before removing", error.Message, StringComparison.Ordinal);

        await harness.Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, default);
        Assert.Null(harness.Store.Get(record.Id));
    }

    [Fact]
    public async Task Remove_releases_ports_logs_and_names_and_emits_destroy()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();

        var record = await harness.CreateShellAsync("echo hi", "web", request =>
            request.HostConfig = new HostConfig { PortBindings = { ["80/tcp"] = [new PortBinding()] } });
        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await harness.Containers.StartAsync(record.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, harness.Ports.ReservationCount);
        var logPath = harness.Logs.PathFor(record.Id);
        Assert.True(File.Exists(logPath));

        await harness.Containers.RemoveAsync(record.Id, force: false, removeVolumes: false, default);

        Assert.Equal(0, harness.Ports.ReservationCount);
        Assert.False(File.Exists(logPath));
        Assert.Empty(harness.NameRegistry.Snapshot());
        await events.WaitForAsync("destroy");
        await Assert.ThrowsAsync<DockerApiException>(() => harness.Containers.ResolveAsync(record.Id, default));
    }

    [Fact]
    public async Task Wait_removed_completes_when_the_container_is_gone()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("exit 4", "web");
        var exiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var removed = harness.Containers.WaitAsync(record.Id, "removed", default);

        await harness.Containers.StartAsync(record.Id, default);
        await exiting.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Containers.RemoveAsync(record.Id, force: false, removeVolumes: false, default);

        var response = await removed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, response.StatusCode);
    }

    [Fact]
    public async Task AutoRemove_removes_the_container_after_it_exits()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("echo bye", "web", request =>
            request.HostConfig = new HostConfig { AutoRemove = true });

        await harness.Containers.StartAsync(record.Id, default);

        await ContainerTestHarness.WaitUntilAsync(
            () => harness.Store.Get(record.Id) is null,
            "the auto-removed container to disappear");
        Assert.Contains(harness.Runtime.Calls, call => call.StartsWith("RemoveContainerAsync:web", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_updates_the_record_and_rejects_conflicts()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        var record = await harness.CreateAsync("alpine", "web");
        await harness.CreateAsync("alpine", "taken");

        await harness.Containers.RenameAsync(record.Id, "web2", default);
        Assert.Equal("web2", record.Name);
        Assert.Equal(record.Id, (await harness.Containers.ResolveAsync("web2", default)).Id);
        await events.WaitForAsync("rename");
        Assert.Equal("web", events.First("rename").Actor.Attributes["oldName"]);

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.RenameAsync(record.Id, "taken", default));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);
    }

    [Fact]
    public async Task Resize_succeeds_before_the_container_runs_and_reaches_the_process_after()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("sleep 30", "web", request => request.Tty = true);

        await harness.Containers.ResizeAsync(record.Id, 120, 40, default);

        await harness.Containers.StartAsync(record.Id, default);
        await harness.Containers.ResizeAsync(record.Id, 100, 30, default);

        var process = harness.Runtime.GetContainer("web")!.Process!;
        Assert.Equal((100, 30), process.LastResize!.Value);
        Assert.True(process.HasTty);
        Assert.Null(process.Stderr);

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Prune_rejects_an_unknown_filter_key_instead_of_pruning_unfiltered()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var stopped = await harness.CreateShellAsync("exit 0", "gone");
        var waiting = harness.Containers.WaitAsync(stopped.Id, "next-exit", default);
        await harness.Containers.StartAsync(stopped.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        // An unknown key used to be ignored, so the prune ran with no filter at all and deleted the
        // container the caller was trying to exclude.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Containers.PruneAsync(Filters.Parse("""{"bogus":["x"]}"""), default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'bogus'", ex.Message);
        Assert.NotNull(harness.Store.Get(stopped.Id));
    }

    [Fact]
    public async Task Prune_removes_stopped_containers_only()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var stopped = await harness.CreateShellAsync("exit 0", "gone");
        var waiting = harness.Containers.WaitAsync(stopped.Id, "next-exit", default);
        await harness.Containers.StartAsync(stopped.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        var running = await harness.RunShellAsync("sleep 30", "kept");

        var response = await harness.Containers.PruneAsync(Filters.Empty, default);

        Assert.Equal([stopped.Id], response.ContainersDeleted);
        Assert.NotNull(harness.Store.Get(running.Id));

        await harness.Containers.KillAsync(running.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Prune_reports_SpaceReclaimed_from_the_deleted_containers_logs()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var stopped = await harness.CreateShellAsync("exit 0", "gone");
        var waiting = harness.Containers.WaitAsync(stopped.Id, "next-exit", default);
        await harness.Containers.StartAsync(stopped.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate captured output for the container so its log file has bytes on disk;
        // PruneAsync deletes the container (and its log) but must still account for the size
        // before that happens.
        await using (var writer = harness.Logs.OpenWriter(stopped.Id))
        {
            await writer.WriteAsync(StdStream.Stdout, Encoding.UTF8.GetBytes("some output\n"), default);
        }

        var expectedBytes = new FileInfo(harness.Logs.PathFor(stopped.Id)).Length;
        Assert.True(expectedBytes > 0);

        var response = await harness.Containers.PruneAsync(Filters.Empty, default);

        Assert.Equal([stopped.Id], response.ContainersDeleted);
        Assert.Equal(expectedBytes, response.SpaceReclaimed);
        Assert.True(response.SpaceReclaimed > 0);
    }

    [Fact]
    public async Task Prune_labelBang_excludes_matching_containers()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var keep = await harness.CreateShellAsync("exit 0", "keep", request => request.Labels["keep"] = "1");
        var waitingKeep = harness.Containers.WaitAsync(keep.Id, "next-exit", default);
        await harness.Containers.StartAsync(keep.Id, default);
        await waitingKeep.WaitAsync(TimeSpan.FromSeconds(5));

        var gone = await harness.CreateShellAsync("exit 0", "gone");
        var waitingGone = harness.Containers.WaitAsync(gone.Id, "next-exit", default);
        await harness.Containers.StartAsync(gone.Id, default);
        await waitingGone.WaitAsync(TimeSpan.FromSeconds(5));

        // `label!` was accepted by Validate but had no effect on the match, so a caller asking to
        // spare everything labelled "keep" had it pruned right along with "gone".
        var response = await harness.Containers.PruneAsync(Filters.Parse("""{"label!":["keep"]}"""), default);

        Assert.Equal([gone.Id], response.ContainersDeleted);
        Assert.NotNull(harness.Store.Get(keep.Id));
        Assert.Null(harness.Store.Get(gone.Id));
    }

    [Fact]
    public async Task Prune_unparseable_until_is_400_and_removes_nothing()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var stopped = await harness.CreateShellAsync("exit 0", "gone");
        var waiting = harness.Containers.WaitAsync(stopped.Id, "next-exit", default);
        await harness.Containers.StartAsync(stopped.Id, default);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        // An unparseable `until` used to be swallowed and treated as "nothing to exclude", pruning
        // every stopped container instead of rejecting the request.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Containers.PruneAsync(Filters.Parse("""{"until":["not-a-time"]}"""), default));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal(
            "parsing time \"not-a-time\" as \"2006-01-02\": cannot parse \"not-a-time\" as \"2006\"",
            ex.Message);
        Assert.NotNull(harness.Store.Get(stopped.Id));
    }

    [Fact]
    public async Task Update_changes_the_restart_policy_and_refuses_resources()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");

        await harness.Containers.UpdateAsync(
            record.Id,
            new ContainerUpdateRequest { RestartPolicy = new RestartPolicy { Name = "always" } },
            default);
        Assert.Equal("always", record.RestartPolicy.Name);

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Containers.UpdateAsync(record.Id, new ContainerUpdateRequest { Memory = 1024 }, default));
        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, error.Status);
    }

    private static string Text(OutputChunk chunk) => Encoding.UTF8.GetString(chunk.Data.Span);
}
