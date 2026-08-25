using Cider.AppleContainer.Xpc;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerProcess"/> (task cider-ede.7) against fake <c>waitAsync</c>/<c>resizeAsync</c>/
/// <c>killAsync</c> delegates — the class takes no direct <see cref="Cider.AppleContainer.Xpc.XpcClient"/>
/// dependency precisely so it can be exercised this way, without a live
/// <c>com.apple.container.apiserver</c> connection.
/// </summary>
public sealed class XpcContainerProcessTests
{
    private static XpcContainerProcess Create(
        bool hasTty = false,
        Stream? stdin = null,
        Stream? stdout = null,
        Stream? stderr = null,
        Func<CancellationToken, Task<(int ExitCode, DateTimeOffset ExitedAt)?>>? waitAsync = null,
        Func<int, int, CancellationToken, Task>? resizeAsync = null,
        Func<string, CancellationToken, Task>? killAsync = null) =>
        new(
            hasTty,
            stdin,
            stdout ?? new MemoryStream(),
            stderr,
            // Never completes by default — a still-running process — so kill/resize forwarding tests
            // that don't care about Exited are not tripped up by its own no-op-once-exited guard.
            // Tests that care about Exited pass their own waitAsync.
            waitAsync ?? (_ => new TaskCompletionSource<(int ExitCode, DateTimeOffset ExitedAt)?>().Task),
            resizeAsync ?? ((_, _, _) => Task.CompletedTask),
            killAsync ?? ((_, _) => Task.CompletedTask),
            NullLogger.Instance);

    [Fact]
    public void Pid_is_always_null()
    {
        var process = Create();
        Assert.Null(process.Pid);
    }

    [Fact]
    public async Task Exited_completes_with_the_wait_delegates_exit_code()
    {
        var process = Create(waitAsync: _ => Task.FromResult<(int ExitCode, DateTimeOffset ExitedAt)?>((7, DateTimeOffset.UnixEpoch)));

        Assert.Equal(7, await process.Exited);
    }

    [Fact]
    public async Task Exited_is_minus_one_when_the_wait_delegate_answers_null()
    {
        // containerWait mapped to notFound/invalidState (XpcContainerRuntime.WaitContainerAsync's own
        // contract) — the process has no recoverable exit code.
        var process = Create(waitAsync: _ => Task.FromResult<(int ExitCode, DateTimeOffset ExitedAt)?>(null));

        Assert.Equal(-1, await process.Exited);
    }

    [Fact]
    public async Task Exited_is_minus_one_when_the_wait_delegate_throws()
    {
        // IContainerProcess.Exited's own contract: "Never throws; -1 when unknown".
        var process = Create(waitAsync: _ => throw new InvalidOperationException("transport exploded"));

        Assert.Equal(-1, await process.Exited);
    }

    [Fact]
    public async Task Stdin_closes_for_real_and_then_reports_null()
    {
        var stdin = new MemoryStream();
        var process = Create(stdin: stdin);

        Assert.Same(stdin, process.Stdin);

        await process.CloseStdinAsync();

        Assert.Null(process.Stdin);
        Assert.Throws<ObjectDisposedException>(() => stdin.WriteByte(1));
    }

    [Fact]
    public async Task CloseStdinAsync_is_idempotent()
    {
        var stdin = new MemoryStream();
        var process = Create(stdin: stdin);

        await process.CloseStdinAsync();
        await process.CloseStdinAsync();

        Assert.Null(process.Stdin);
    }

    [Fact]
    public void Stdin_is_null_when_never_attached()
    {
        var process = Create(stdin: null);
        Assert.Null(process.Stdin);
    }

    [Fact]
    public async Task ResizeAsync_forwards_cols_and_rows_only_with_a_tty()
    {
        (int Cols, int Rows)? seen = null;
        var process = Create(hasTty: true, resizeAsync: (cols, rows, _) =>
        {
            seen = (cols, rows);
            return Task.CompletedTask;
        });

        await process.ResizeAsync(120, 40, default);

        Assert.Equal((120, 40), seen);
    }

    [Fact]
    public async Task ResizeAsync_is_a_no_op_without_a_tty()
    {
        var called = false;
        var process = Create(hasTty: false, resizeAsync: (_, _, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await process.ResizeAsync(120, 40, default);

        Assert.False(called);
    }

    [Fact]
    public async Task ResizeAsync_is_a_no_op_once_the_process_has_exited()
    {
        var called = false;
        var process = Create(
            hasTty: true,
            waitAsync: _ => Task.FromResult<(int ExitCode, DateTimeOffset ExitedAt)?>((0, DateTimeOffset.UnixEpoch)),
            resizeAsync: (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });

        await process.Exited;
        await process.ResizeAsync(80, 24, default);

        Assert.False(called);
    }

    [Fact]
    public async Task KillAsync_forwards_the_signal_string_exactly()
    {
        string? seen = null;
        var process = Create(killAsync: (signal, _) =>
        {
            seen = signal;
            return Task.CompletedTask;
        });

        await process.KillAsync("SIGKILL", default);

        Assert.Equal("SIGKILL", seen);
    }

    [Fact]
    public async Task KillAsync_swallows_a_RuntimeException_from_the_delegate()
    {
        // Best-effort signal delivery (IContainerProcess.KillAsync's own contract) — a failure must
        // never surface to whatever endpoint is forwarding a client's docker kill.
        var process = Create(killAsync: (_, _) => throw RuntimeException.Unavailable("apiserver down"));

        await process.KillAsync("SIGTERM", default);
    }

    [Fact]
    public async Task KillAsync_is_a_no_op_once_the_process_has_exited()
    {
        var called = false;
        var process = Create(
            waitAsync: _ => Task.FromResult<(int ExitCode, DateTimeOffset ExitedAt)?>((0, DateTimeOffset.UnixEpoch)),
            killAsync: (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });

        await process.Exited;
        await process.KillAsync("SIGKILL", default);

        Assert.False(called);
    }

    [Fact]
    public async Task DisposeAsync_closes_the_stdio_streams_and_never_signals_the_container()
    {
        var stdin = new MemoryStream();
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var killCalled = false;
        var resizeCalled = false;

        var process = Create(
            hasTty: true,
            stdin: stdin,
            stdout: stdout,
            stderr: stderr,
            killAsync: (_, _) =>
            {
                killCalled = true;
                return Task.CompletedTask;
            },
            resizeAsync: (_, _, _) =>
            {
                resizeCalled = true;
                return Task.CompletedTask;
            });

        await process.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stdin.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => stdout.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => stderr.WriteByte(1));
        Assert.False(killCalled);
        Assert.False(resizeCalled);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var process = Create();

        await process.DisposeAsync();
        await process.DisposeAsync();
    }
}
