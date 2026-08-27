using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Logs;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
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

        // cider-ede.26: DNS/address registration is a detached follow-up now, no longer something
        // RunShellAsync's underlying StartAsync has necessarily finished by the time it returns —
        // so this waits for it explicitly instead of assuming it is already done.
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the container's DNS name to be registered");

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

        // cider-ede.26: the poll that gets past the delayed attachments now runs detached from
        // Start's own return, so both the DNS registration and the inspect-call count it takes to
        // get there are awaited explicitly rather than assumed complete the instant Start returns.
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the container's DNS name to be registered past the delayed attachment");

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
    public async Task Start_returns_immediately_without_waiting_on_the_address_at_all()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // The old test proved only that Start honored its own (tiny, test-only) budgets — a
        // loop-bound restatement of the very thing under test. This proves the actual cider-ede.26
        // claim: Start no longer waits on network registration at all, so it returns fast even with
        // the *default* NetworkPollInterval/StartReturnBudget/NetworkPollBudget left untouched and
        // the attachment delayed several poll cycles deep — carrying over cider-ede.18's criterion
        // that `docker start` returns in <= 200 ms (excluding VM boot; the fake runtime here has
        // none to exclude). Under the old, inline behaviour this alone would take >= 5 poll
        // intervals (~1.25 s at the default 250 ms) to resolve before Start could return.
        var record = await harness.CreateShellAsync("sleep 5", "web");
        harness.Runtime.DelayNetworkAttachment("web", 5);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await harness.Containers.StartAsync(record.Id, default);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"StartAsync took {stopwatch.Elapsed} to return; it must not wait on network registration at all");
        Assert.True(record.State.Running);

        // Confirms the wait really is still happening, just detached: the address does eventually
        // resolve once the delayed attachment clears, without anyone calling StartAsync again.
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the container's DNS name to be registered by the detached follow-up");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_gives_up_on_the_address_within_budget_when_it_never_shows_up()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(10);
        harness.Containers.StartReturnBudget = TimeSpan.FromMilliseconds(30);
        harness.Containers.NetworkPollBudget = TimeSpan.FromMilliseconds(60);
        var record = await harness.CreateShellAsync("sleep 5", "web");

        // 1000 delayed inspects vastly outlives NetworkPollBudget (60 ms / ~6 polls at a 10 ms
        // interval): the delay never actually clears within the follow-up's own budget, so it must
        // give up on its own rather than poll forever.
        harness.Runtime.DelayNetworkAttachment("web", 1000);

        await harness.Containers.StartAsync(record.Id, default);
        Assert.True(record.State.Running);

        // The detached follow-up gives up on the address after NetworkPollBudget; give it
        // comfortable headroom past that and confirm it really did stop rather than eventually
        // succeed.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(harness.NameRegistry.TryResolve("bridge", "web", out _));

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_returns_with_the_published_listener_bound_but_registration_and_publication_still_pending()
    {
        // cider-ede.27: Start returns once containerStartProcess succeeds and cider-ede.18's
        // listeners are bound, not once every side effect of the start has settled. This proves both
        // halves of that split are observably still in flight the instant Start hands back control —
        // not merely "eventually done" — and that both settle on their own afterwards without anyone
        // calling Start (or the poller) again.
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(10);

        // Twenty empty inspects vastly outlives anything the detached continuation could race through
        // between StartAsync's own await returning and the assertions right below it, so the pending
        // state asserted here does not depend on winning a timing race — the delay count guarantees
        // no address has been found yet regardless of how many inspects happen to have already run.
        harness.Runtime.DelayNetworkAttachment("web", 20);

        var record = await harness.CreateShellAsync("sleep 5", "web", request =>
            request.HostConfig = new HostConfig { PortBindings = { ["8080/tcp"] = [new PortBinding()] } });

        await harness.Containers.StartAsync(record.Id, default);

        Assert.True(record.State.Running);
        Assert.False(
            harness.NameRegistry.TryResolve("bridge", "web", out _),
            "DNS must not already be registered the instant Start returns -- that would mean registration ran inline again");

        var pending = harness.Publisher.LiveFor(record.Id);
        Assert.NotEmpty(pending);
        Assert.All(pending, port => Assert.Null(port.ContainerIp));

        // Both settle on their own, on a later tick, with nobody calling Start (or the poller) again.
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "DNS to register once the detached continuation finds the address");
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.Publisher.LiveFor(record.Id).Any(port => port.ContainerIp is not null),
            "the port publisher to learn the backend address once the detached continuation finds it");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Start_of_a_container_that_exits_immediately_reaches_HandleExitAsync_exactly_once_despite_a_pending_address()
    {
        // cider-ede.27 fix direction: "HandleExitAsync must still win a race with the continuation."
        // Withholding the address for the whole test is the worst case for that race -- the detached
        // post-start continuation (cider-ede.26/27) never gets a resolved address to stop early on, so
        // whatever inspecting it manages to do happens against a container that is, by the time it
        // gets anywhere, already exited.
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        harness.Containers.NetworkPollInterval = TimeSpan.FromMilliseconds(5);

        var record = await harness.CreateShellAsync("exit 0", "web");
        harness.Runtime.DelayNetworkAttachment("web", 1000);

        await harness.Containers.StartAsync(record.Id, default);
        await events.WaitForAsync("die");

        Assert.Equal(1, events.Actions.Count(action => action == "die"));
        Assert.Equal("exited", record.State.Status);
        Assert.False(
            harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the detached continuation must not win a race with HandleExitAsync and leave DNS registered for an exited container");
    }

    [Fact]
    public async Task Network_refresh_racing_past_an_exit_does_not_re_register_dns()
    {
        // cider-ede.27 correction: the test above pins HandleExitAsync's own race against the
        // detached post-start continuation, but nothing exercised ApplyNetworkInfo's
        // `!record.State.Running` guard itself -- a mutant that deletes that guard still passed
        // every test in this file. RefreshNetworkInfoAsync calls ApplyNetworkInfo unconditionally,
        // so calling it directly against an already-exited record (as an apply that was already
        // in flight when HandleExitAsync ran would land) is the direct way to pin the guard.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.RunShellAsync("sleep 5", "web");
        await ContainerTestHarness.WaitUntilAsync(
            () => harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the container's DNS name to be registered");

        await harness.Containers.KillAsync(record.Id, "SIGKILL", default);
        await ContainerTestHarness.WaitUntilAsync(
            () => !record.State.Running && !harness.NameRegistry.TryResolve("bridge", "web", out _),
            "the exit to be accounted and the DNS name unregistered");

        // An apply that was already in flight when HandleExitAsync ran (the detached post-start
        // continuation, or a poller tick) lands here against an already-exited record.
        await harness.Containers.RefreshNetworkInfoAsync(record, default);

        Assert.False(
            harness.NameRegistry.TryResolve("bridge", "web", out _),
            "an apply that lands after HandleExitAsync must not re-register DNS");
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
    public async Task Next_exit_wait_issued_after_a_run_already_exited_is_not_resolved_by_that_stale_result()
    {
        // cider-ede.33 fix direction: a container that exits twice across a restart/start cycle
        // must not complete a stale TCS -- HandleExitAsync swaps in a fresh NextExit for the run
        // that follows, and a wait issued after the first run already finished must capture that
        // fresh one, not still observe the run that had already completed before it was issued.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("exit 5", "web");

        await harness.Containers.StartAsync(record.Id, default);
        await ContainerTestHarness.WaitUntilAsync(() => !record.State.Running, "the first run to exit");
        Assert.Equal(5, record.State.ExitCode);

        var waiting = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(waiting.IsCompleted, "a wait issued after the prior run already exited must not resolve from that stale exit");

        await harness.Containers.StartAsync(record.Id, default);
        var response = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, response.StatusCode);
        Assert.Equal("exited", record.State.Status);
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
    public async Task Stop_of_an_adopted_container_with_no_held_process_completes_a_pending_docker_wait()
    {
        // cider-ede.33 correction: `docker stop` on a container cider only adopted (no held
        // process, so HandleExitAsync never runs for it) used to leave a pending `docker wait`
        // blocked forever -- MarkStoppedWithoutHandle flipped the record to exited and persisted
        // without ever completing NextExit, and once the record is no longer Running neither
        // StatePoller die branch (StatePoller.cs:154, :210) can fire to rescue it either.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        await harness.Containers.StopAsync(record.Id, timeoutSeconds: 1, signal: null, default);

        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, nextExitResponse.StatusCode);
        Assert.Equal(0, notRunningResponse.StatusCode);
        Assert.Equal("exit code unknown (daemon restarted)", nextExitResponse.Error?.Message);
    }

    [Fact]
    public async Task ReconcileStatus_of_an_adopted_container_completes_a_pending_docker_wait()
    {
        // cider-ede.33 correction: StateSynchronizer's running->exited transition
        // (StateSynchronizer.cs:126 -> ReconcileStatus) is a fifth uncovered observer -- it flipped
        // a live adopted record to exited and persisted with no CompleteExitWait, leaving a
        // `docker wait` from before the resync blocked forever.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        var runtimeContainer = new RuntimeContainer { RuntimeId = record.RuntimeId, State = RuntimeContainerState.Stopped };
        var changed = harness.Containers.ReconcileStatus(record, runtimeContainer);

        Assert.True(changed);
        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, nextExitResponse.StatusCode);
        Assert.Equal(0, notRunningResponse.StatusCode);
        Assert.Equal("exited", record.State.Status);
    }

    [Fact]
    public async Task Remove_of_an_adopted_running_container_completes_a_pending_docker_wait()
    {
        // cider-1ki enumeration: `docker rm -f` on a container cider only adopted is the same class
        // as the three cider-ede.33/.40/1ki instances. RemoveAsync's teardown completed the
        // `removed` waiter and the attachments but never NextExit, and with no held process
        // WaitForExitHandlingAsync has nothing to wait for and HandleExitAsync never runs -- so the
        // record disappeared with a `docker wait` still blocked on it. Both removal paths
        // (RemoveAsync and ForgetVanishedAsync) now share one teardown that completes it.
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        await harness.Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, default);

        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(record.State.ExitCode, nextExitResponse.StatusCode);
        Assert.Equal(record.State.ExitCode, notRunningResponse.StatusCode);
        Assert.Null(harness.Store.Get(record.Id));
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

    /// <summary>
    /// task cider-ede.7 fix direction §4: a daemon restart leaves a persisted <c>State.Running</c>
    /// record with no in-memory process — exactly what a fresh <see cref="ContainerManager"/> that
    /// never itself called <c>StartAsync</c> for this id, reconciling against a runtime that still
    /// reports the container <see cref="RuntimeContainerState.Running"/>, reproduces without actually
    /// restarting a process. Under <see cref="IContainerRuntime.IsXpcTransport"/> the real exit code
    /// must be recovered (via the fake's own genuinely-blocking <c>WaitContainerAsync</c>) instead of
    /// settling for "exit code unknown (daemon restarted)".
    /// </summary>
    [Fact]
    public async Task Reconcile_recovers_the_real_exit_code_of_a_container_still_running_after_a_restart()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        harness.Runtime.IsXpcTransport = true;

        const string runtimeId = "survivor";
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = runtimeId,
            State = RuntimeContainerState.Running,
            ImageReference = "alpine",
            Argv = ["sh", "-c", "sleep 0.05; exit 7"],
        });

        // The process is running on the (fake) engine already — attached directly, the way a
        // container this daemon bootstrapped before an earlier process's restart would already be
        // running when the new process comes up, with nothing in this ContainerManager's own
        // in-memory handles pointing at it yet.
        var process = new FakeProcess(["sh", "-c", "sleep 0.05; exit 7"], [], tty: false, openStdin: false);
        harness.Runtime.GetContainer(runtimeId)!.Process = process;

        var record = new ContainerRecord
        {
            Id = "survivor-id",
            Name = "survivor",
            RuntimeId = runtimeId,
            Managed = true,
            Request = new ContainerCreateRequest { Image = "alpine" },
            State = new ContainerState { Status = "running", StartedAt = DateTimeOffset.UtcNow },
        };
        harness.Store.Upsert(record.Id, record);

        await harness.Containers.ReconcileAsync(default);

        // ReconcileAsync itself must return promptly — the wait for the real exit code runs detached.
        Assert.Equal("running", harness.Store.Get(record.Id)!.State.Status);

        await ContainerTestHarness.WaitUntilAsync(
            () => harness.Store.Get(record.Id)!.State.Status == "exited",
            "the reconciled container to report its real exit");

        var settled = harness.Store.Get(record.Id)!;
        Assert.Equal(7, settled.State.ExitCode);
        Assert.Null(settled.State.Error);

        await events.WaitForAsync("die");
        var die = events.First("die");
        Assert.Equal("7", die.Actor.Attributes["exitCode"]);
    }

    /// <summary>The CLI transport has no <c>containerWait</c> equivalent — the fake's own
    /// <see cref="IContainerRuntime.IsXpcTransport"/> default (<c>false</c>) must leave a still-running
    /// record exactly as reconcile always has (untouched, still "running"), never spin up a wait that
    /// can only ever answer <c>null</c>.</summary>
    [Fact]
    public async Task Reconcile_does_not_wait_for_exit_on_the_cli_transport()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        const string runtimeId = "survivor-cli";
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = runtimeId,
            State = RuntimeContainerState.Running,
            ImageReference = "alpine",
            Argv = ["sh", "-c", "sleep 300"],
        });

        var record = new ContainerRecord
        {
            Id = "survivor-cli-id",
            Name = "survivor-cli",
            RuntimeId = runtimeId,
            Managed = true,
            Request = new ContainerCreateRequest { Image = "alpine" },
            State = new ContainerState { Status = "running", StartedAt = DateTimeOffset.UtcNow },
        };
        harness.Store.Upsert(record.Id, record);

        await harness.Containers.ReconcileAsync(default);

        Assert.DoesNotContain("WaitContainerAsync:" + runtimeId, harness.Runtime.Calls);
        Assert.Equal("running", harness.Store.Get(record.Id)!.State.Status);
    }

    private static string Text(OutputChunk chunk) => Encoding.UTF8.GetString(chunk.Data.Span);
}
