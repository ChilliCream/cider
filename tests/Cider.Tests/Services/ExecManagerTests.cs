using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Xunit;

namespace Cider.Tests.Services;

public sealed class ExecManagerTests
{
    [Fact]
    public async Task Create_start_and_inspect_report_the_exit_code()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        await using var events = await harness.CollectEventsAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");

        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest
        {
            Cmd = ["sh", "-c", "echo hi; echo oops 1>&2; exit 7"],
            AttachStdout = true,
            AttachStderr = true,
        }, default);

        Assert.Equal(64, created.Id.Length);

        var before = await harness.Execs.InspectAsync(created.Id, default);
        Assert.False(before.Running);
        Assert.Equal(container.Id, before.ContainerID);
        Assert.Equal("sh", before.ProcessConfig.Entrypoint);
        Assert.Equal(["-c", "echo hi; echo oops 1>&2; exit 7"], before.ProcessConfig.Arguments);

        await using var session = await harness.Execs.StartAsync(created.Id, tty: false, consoleSize: null, default);

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in session.Output.ReadAllAsync(cts.Token))
        {
            (chunk.Stream == StdStream.Stderr ? errors : output).Append(Encoding.UTF8.GetString(chunk.Data.Span));
        }

        Assert.Equal(7, await session.Exited.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("hi\n", output.ToString());
        Assert.Equal("oops\n", errors.ToString());

        var after = await harness.Execs.InspectAsync(created.Id, default);
        Assert.False(after.Running);
        Assert.Equal(7, after.ExitCode);

        await events.WaitForAsync("exec_die");
        Assert.Contains(events.Actions, action => action.StartsWith("exec_create:", StringComparison.Ordinal));
        Assert.Contains(events.Actions, action => action.StartsWith("exec_start:", StringComparison.Ordinal));

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Exec_stdin_is_forwarded()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");

        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest
        {
            Cmd = ["cat"],
            AttachStdin = true,
            AttachStdout = true,
        }, default);

        await using var session = await harness.Execs.StartAsync(created.Id, tty: false, consoleSize: null, default);
        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("echoed\n"), default);
        await session.CloseStdinAsync();

        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in session.Output.ReadAllAsync(cts.Token))
        {
            output.Append(Encoding.UTF8.GetString(chunk.Data.Span));
        }

        Assert.Equal("echoed\n", output.ToString());
        Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(5)));

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Exec_right_after_start_retries_past_a_transient_not_running_error()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");

        // Simulates Apple's `container exec` rejecting for a moment right after `container start -a`
        // already holds the init process (docs/apple-container-notes.md §12): the first attempt fails
        // with "is not running" even though the daemon's own record already shows it running.
        harness.Runtime.FailExecUntilRunning("web", 1);

        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest { Cmd = ["true"] }, default);
        await using var session = await harness.Execs.StartAsync(created.Id, tty: false, consoleSize: null, default);

        Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, harness.Runtime.Calls.Count(call => call.StartsWith("ExecAsync:web:true", StringComparison.Ordinal)));

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Exec_on_a_container_that_is_not_running_is_409()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.CreateAsync("alpine", "web");

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Execs.CreateAsync(container.Id, new ExecCreateRequest { Cmd = ["true"] }, default));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);
        Assert.Contains("is not running", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_exec_id_is_a_404()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Execs.InspectAsync("deadbeef", default));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.Status);
        Assert.Equal("No such exec instance: deadbeef", error.Message);
    }

    [Fact]
    public async Task Starting_an_exec_twice_is_409()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");
        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest { Cmd = ["true"] }, default);

        await using var session = await harness.Execs.StartAsync(created.Id, false, null, default);

        var error = await Assert.ThrowsAsync<DockerApiException>(() =>
            harness.Execs.StartAsync(created.Id, false, null, default));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, error.Status);

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task A_detached_exec_still_runs_to_completion()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");
        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest { Cmd = ["sh", "-c", "exit 5"] }, default);

        await harness.Execs.StartDetachedAsync(created.Id, default);

        await ContainerTestHarness.WaitUntilAsync(
            () => harness.Execs.InspectAsync(created.Id, default).GetAwaiter().GetResult().ExitCode == 5,
            "the detached exec to finish");

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }

    [Fact]
    public async Task Resize_reaches_the_exec_process()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var container = await harness.RunShellAsync("sleep 30", "web");
        var created = await harness.Execs.CreateAsync(container.Id, new ExecCreateRequest
        {
            Cmd = ["sh", "-c", "sleep 5"],
            Tty = true,
        }, default);

        await using var session = await harness.Execs.StartAsync(created.Id, tty: true, consoleSize: [24, 80], default);
        await harness.Execs.ResizeAsync(created.Id, 100, 30, default);

        var process = harness.Runtime.ExecProcesses[^1];
        Assert.Equal((100, 30), process.LastResize!.Value);
        Assert.True(session.Tty);

        await harness.Containers.KillAsync(container.Id, "SIGKILL", default);
    }
}
