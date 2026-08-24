using System.Diagnostics;
using System.Text;
using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Native;
using Cider.AppleContainer.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// The pty side of the CLI launcher, driven against a real <c>/bin/sh</c> instead of the
/// <c>container</c> CLI: every byte a pty child writes has to reach the reader, and the stream may
/// only end once that child is gone (docs/apple-container-notes.md §5b).
/// </summary>
public class PtyProcessTests
{
    private static ProcessLauncher Launcher(IPtySyscalls? syscalls = null) =>
        new(
            new ContainerCli(new AppleContainerOptions { CliPath = "/bin/sh" }, NullLogger.Instance),
            NullLogger.Instance,
            syscalls);

    [Fact]
    public async Task A_burst_of_output_followed_by_an_immediate_exit_is_delivered_in_full()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            await using var process = Launcher().StartPty(
                ["-c", "i=0; while [ $i -lt 300 ]; do echo line$i; i=$((i+1)); done"],
                cols: 100,
                rows: 24,
                signalDelegate: null);

            var text = await ReadToEndAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, await process.Exited);
            Assert.Contains("line0", text, StringComparison.Ordinal);
            Assert.Contains("line299", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The regression this ticket is about: Darwin ends reads on a pty master as soon as no slave
    /// fd is open anywhere, so a child that closes its own stdio (or has not opened the pty yet)
    /// used to hand the reader a clean EOF in the middle of a live session — the rest of the
    /// output, and everything the client typed afterwards, was silently dropped.
    /// </summary>
    [Fact]
    public async Task A_child_that_drops_its_own_stdio_does_not_end_the_stream()
    {
        await using var process = Launcher().StartPty(
            ["-c", "echo AAA; exec 0<&- 1>&- 2>&-; sleep 1; exit 0"],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        var started = Stopwatch.StartNew();
        var text = await ReadToEndAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(30));
        var elapsed = started.Elapsed;

        Assert.Contains("AAA", text, StringComparison.Ordinal);
        Assert.Equal(0, await process.Exited);
        Assert.True(
            elapsed > TimeSpan.FromMilliseconds(700),
            $"the pty stream ended after {elapsed.TotalMilliseconds:F0} ms, while the child was still running");
    }

    /// <summary>
    /// The guest has a terminal of its own inside the VM, so what the CLI writes
    /// to this pty already carries CRLF. With the line discipline's own <c>ONLCR</c> left on, that
    /// output is translated a second time and every line reaches the client as <c>\r\r\n</c> —
    /// docker's raw TTY contract is that the bytes pass through unmodified.
    /// </summary>
    [Fact]
    public async Task Crlf_output_is_not_translated_a_second_time()
    {
        await using var process = Launcher().StartPty(
            ["-c", @"printf 'tty-hello\r\n'"],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        var bytes = await ReadAllBytesAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("tty-hello\r\n"u8.ToArray(), bytes);
        Assert.Equal(0, await process.Exited);
    }

    /// <summary>The other half of the same contract: a bare <c>LF</c> stays a bare <c>LF</c>.</summary>
    [Fact]
    public async Task A_bare_newline_is_not_turned_into_a_carriage_return_pair()
    {
        await using var process = Launcher().StartPty(
            ["-c", @"printf 'a\nb\n'"],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        var bytes = await ReadAllBytesAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("a\nb\n"u8.ToArray(), bytes);
    }

    /// <summary>
    /// The allocation itself is what turns the translation off, and those flag words only land in
    /// the right place if <see cref="Termios"/> matches Darwin's ABI — so the pty is read back.
    /// </summary>
    [Fact]
    public void A_fresh_pty_has_output_processing_and_echo_switched_off()
    {
        var size = new WinSize { Cols = 100, Rows = 24 };
        Assert.Equal(0, Libc.OpenPty(out var master, out var slave, out var path, out var error, ref size));
        Assert.Null(error);
        Assert.NotNull(path);

        try
        {
            var flags = Libc.GetIoFlags(slave);
            Assert.NotNull(flags);
            Assert.Equal(0UL, flags!.Value.OFlag & (Libc.OPOST | Libc.ONLCR));
            Assert.Equal(0UL, flags.Value.LFlag & Libc.ECHO);

            // The same words read back through the master: one line discipline, one set of flags.
            var fromMaster = Libc.GetIoFlags(master);
            Assert.NotNull(fromMaster);
            Assert.Equal(flags.Value.OFlag, fromMaster!.Value.OFlag);
        }
        finally
        {
            Libc.Close(slave);
            Libc.Close(master);
        }
    }

    /// <summary>
    /// The CLI's stderr is a pipe, not the session's terminal: that is what stops `container` 1.2.2
    /// emitting its boot spinner and the <c>ESC[?25l</c>/<c>ESC[?25h</c> pair around a session, and
    /// none of what it does write there may be spliced into the client's raw stream.
    /// </summary>
    [Fact]
    public async Task The_childs_stderr_stays_out_of_the_tty_stream()
    {
        await using var process = Launcher().StartPty(
            ["-c", "echo OUT; echo NOISE 1>&2"],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        var text = await ReadToEndAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("OUT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOISE", text, StringComparison.Ordinal);
        Assert.Null(process.Stderr);
    }

    [Fact]
    public async Task The_stream_ends_once_the_child_exits()
    {
        await using var process = Launcher().StartPty(["-c", "echo done"], cols: 100, rows: 24, signalDelegate: null);

        var text = await ReadToEndAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("done", text, StringComparison.Ordinal);
        Assert.Equal(0, await process.Exited);
    }

    [Fact]
    public async Task An_interactive_shell_delivers_everything_it_echoed_before_exiting()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var process = Launcher().StartPty(["-i"], cols: 100, rows: 24, signalDelegate: null);

            var reader = ReadToEndAsync(process.Stdout);
            await Task.Delay(200);
            var stdin = process.Stdin!;
            await stdin.WriteAsync(Encoding.ASCII.GetBytes("echo AAA\nstty size\necho BBB\nexit\n"));
            await stdin.FlushAsync();

            var text = await reader.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                text.Contains("24 100", StringComparison.Ordinal),
                $"iteration {iteration}: the `stty size` output is missing: '{Escape(text)}'");
            Assert.True(
                text.Contains("BBB", StringComparison.Ordinal),
                $"iteration {iteration}: the tail of the session is missing: '{Escape(text)}'");
        }
    }

    /// <summary>
    /// The pty must not leak into unrelated CLI children: an inherited slave keeps a pty alive for
    /// as long as that other process lives, so the session it belongs to never sees the end of its
    /// own output, and an inherited master keeps the device allocated.
    /// </summary>
    [Fact]
    public async Task The_pty_is_not_inherited_by_other_children()
    {
        var launcher = Launcher();
        var baseline = await ListDescriptorsAsync(launcher);

        await using var held = launcher.StartPty(["-c", "sleep 5"], cols: 100, rows: 24, signalDelegate: null);
        var reader = ReadToEndAsync(held.Stdout);
        var withPty = await ListDescriptorsAsync(launcher);

        await held.KillAsync("SIGKILL", CancellationToken.None);
        await reader.WaitAsync(TimeSpan.FromSeconds(20));

        var leaked = withPty.Except(baseline).ToArray();
        Assert.True(
            leaked.Length == 0,
            $"an unrelated child inherited descriptors {string.Join(", ", leaked)} (baseline {string.Join(", ", baseline)}, with pty {string.Join(", ", withPty)})");
    }

    /// <summary>
    /// A live session is the baseline the two no-op tests below are measured against: the ioctl
    /// reaches the pty (the child's own <c>stty size</c> proves it) and the child is told about it.
    /// </summary>
    [Fact]
    public async Task A_resize_on_a_live_session_reaches_the_pty_and_the_child()
    {
        var syscalls = new RecordingSyscalls();
        await using var process = Launcher(syscalls).StartPty(["-i"], cols: 100, rows: 24, signalDelegate: null);

        var reader = ReadToEndAsync(process.Stdout);
        await Task.Delay(200);
        await process.ResizeAsync(120, 40, CancellationToken.None);

        var stdin = process.Stdin!;
        await stdin.WriteAsync(Encoding.ASCII.GetBytes("stty size\nexit\n"));
        await stdin.FlushAsync();

        var text = await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            text.Contains("40 120", StringComparison.Ordinal),
            $"the resized `stty size` output is missing: '{Escape(text)}'");
        Assert.Contains("winsize", syscalls.Calls);
        Assert.Contains($"kill {Libc.SIGWINCH}", syscalls.Calls);
    }

    /// <summary>
    /// The docker CLI resizes on every terminal <c>SIGWINCH</c>, so a resize routinely arrives
    /// after the session's process is gone. Its pid was reaped by then and macOS hands pids out
    /// again, so signalling it would hit whatever process inherited the number.
    /// </summary>
    [Fact]
    public async Task A_resize_after_the_child_exited_does_not_signal_a_reaped_pid()
    {
        var syscalls = new RecordingSyscalls();
        await using var process = Launcher(syscalls).StartPty(["-c", "echo done"], cols: 100, rows: 24, signalDelegate: null);

        await ReadToEndAsync(process.Stdout).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, await process.Exited);

        syscalls.Clear();
        await process.ResizeAsync(120, 40, CancellationToken.None);

        // The master is still the daemon's own fd until dispose, so the ioctl is fine; the pid is not.
        Assert.Contains("winsize", syscalls.Calls);
        Assert.DoesNotContain(syscalls.Calls, call => call.StartsWith("kill", StringComparison.Ordinal));
    }

    /// <summary>
    /// Once the pty master is closed its descriptor number is free, and the OS may already have
    /// handed it to another container's pty, a file or a socket — so a late resize must not touch
    /// it. It still has to complete quietly: the resize endpoint answers 200 in every case.
    /// </summary>
    [Fact]
    public async Task A_resize_after_dispose_touches_no_descriptor()
    {
        var syscalls = new RecordingSyscalls();
        var process = Launcher(syscalls).StartPty(["-c", "sleep 30"], cols: 100, rows: 24, signalDelegate: null);
        var reader = ReadToEndAsync(process.Stdout);

        await process.DisposeAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(20));

        syscalls.Clear();
        await process.ResizeAsync(120, 40, CancellationToken.None);

        Assert.Empty(syscalls.Calls);
    }

    /// <summary>
    /// The same retirement covers the drain loop: it polls the master with <c>FIONREAD</c> for up
    /// to five seconds after the child exits, which outlives a dispose that happens in between.
    /// </summary>
    [Fact]
    public async Task The_pty_master_is_not_polled_after_it_was_closed()
    {
        var syscalls = new RecordingSyscalls();
        // Nothing reads this burst, so it stays queued and the drain loop keeps polling. It has to
        // stay under the pty's own buffer, or the child would block on its own output and never exit.
        var process = Launcher(syscalls).StartPty(
            ["-c", "i=0; while [ $i -lt 20 ]; do echo line$i; i=$((i+1)); done"],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(20)));
        await Task.Delay(200);
        Assert.Contains("pending", syscalls.Calls);

        await process.DisposeAsync();
        var polls = syscalls.Calls.Count(call => call == "pending");

        await Task.Delay(300);
        Assert.Equal(polls, syscalls.Calls.Count(call => call == "pending"));
    }

    /// <summary>
    /// A terminal has no "EOF and then keep reading" state, so a client's <c>CloseWrite</c> cannot
    /// end input on a pty the way it ends a pipe: the session is still interactive, and dropping
    /// what the client types next would lose it silently, with no error anywhere.
    /// </summary>
    [Fact]
    public async Task A_tty_session_still_forwards_stdin_after_a_half_close()
    {
        await using var process = Launcher().StartPty(["-i"], cols: 100, rows: 24, signalDelegate: null);

        var reader = ReadToEndAsync(process.Stdout);
        await Task.Delay(200);

        await process.CloseStdinAsync();

        var stdin = process.Stdin;
        Assert.NotNull(stdin);

        // Both the echo of the line and the shell acting on `exit` need these bytes to arrive.
        await stdin.WriteAsync(Encoding.ASCII.GetBytes("echo AFTER-HALF-CLOSE\nexit\n"));
        await stdin.FlushAsync();

        var text = await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            text.Contains("AFTER-HALF-CLOSE", StringComparison.Ordinal),
            $"input written after the half-close never reached the pty: '{Escape(text)}'");
    }

    /// <summary>The pipe side is unchanged: a half-close is a real one, and the child reads EOF.</summary>
    [Fact]
    public async Task A_pipe_session_ends_stdin_on_a_half_close()
    {
        await using var process = Launcher().StartPipe(
            ["-c", "cat; echo EOF-SEEN"],
            openStdin: true,
            signalDelegate: null);

        var stdin = process.Stdin!;
        await stdin.WriteAsync("hello\n"u8.ToArray());
        await stdin.FlushAsync();
        await process.CloseStdinAsync();

        Assert.Null(process.Stdin);

        var text = await new StreamReader(process.Stdout).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("hello", text, StringComparison.Ordinal);
        Assert.Contains("EOF-SEEN", text, StringComparison.Ordinal);
        Assert.Equal(0, await process.Exited);
    }

    /// <summary>The descriptors an unrelated CLI child is started with.</summary>
    private static async Task<int[]> ListDescriptorsAsync(ProcessLauncher launcher)
    {
        await using var child = launcher.StartPipe(["-c", "ls /dev/fd"], openStdin: false, signalDelegate: null);
        var listing = await new StreamReader(child.Stdout).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await child.Exited.WaitAsync(TimeSpan.FromSeconds(20));

        return [.. listing
            .Split(['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => int.TryParse(entry, out _))
            .Select(int.Parse)
            .Order()];
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        var buffer = new byte[4096];
        using var bytes = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return bytes.ToArray();
            }

            bytes.Write(buffer, 0, read);
        }
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return text.ToString();
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    private static string Escape(string text) =>
        text.Replace("\u001b", "<ESC>", StringComparison.Ordinal).Replace("\r", "<CR>", StringComparison.Ordinal);

    /// <summary>
    /// The real syscalls, with a note of every call. Descriptor numbers and pids are recycled the
    /// moment they are released, so "the ioctl went to a stranger's fd" is unobservable after the
    /// fact — the only way to assert it is to watch what is issued at all.
    /// </summary>
    private sealed class RecordingSyscalls : IPtySyscalls
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Clear()
        {
            lock (_calls)
            {
                _calls.Clear();
            }
        }

        public int SetWindowSize(int master, int cols, int rows)
        {
            Record("winsize");
            return LibcSyscalls.Instance.SetWindowSize(master, cols, rows);
        }

        public int PendingBytes(int fd)
        {
            Record("pending");
            return LibcSyscalls.Instance.PendingBytes(fd);
        }

        public int Kill(int pid, int signal)
        {
            Record($"kill {signal}");
            return LibcSyscalls.Instance.Kill(pid, signal);
        }

        public int Close(int fd)
        {
            Record("close");
            return LibcSyscalls.Instance.Close(fd);
        }

        private void Record(string call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }
        }
    }
}
