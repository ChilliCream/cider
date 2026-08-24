using System.Text.RegularExpressions;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #8 — interactive TTY. The test allocates a real pseudo-terminal (a small Python helper) and
/// runs <c>docker run -it</c> / <c>docker exec -it</c> under it, so the raw-stream hijack, the
/// terminal size negotiation and the pty plumbing are all exercised the way a human shell does it.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed partial class TtyTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    private const string PtyHelper = """
        import os, pty, select, struct, subprocess, sys, termios, fcntl, time

        # Allocate a real terminal with a known size, run the command on it, and only feed the
        # scripted input once the child's output has settled (docker run -it first has to boot a VM
        # and print Apple's progress spinner; anything typed before the shell is up is swallowed).
        rows, cols = 24, 100
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, 0, 0))

        child = subprocess.Popen(sys.argv[1:], stdin=slave, stdout=slave, stderr=slave, close_fds=True)
        os.close(slave)

        payload = sys.stdin.buffer.read()
        out = bytearray()
        sent = not payload
        start = time.time()
        last_out = start
        deadline = start + 180

        while time.time() < deadline:
            ready, _, _ = select.select([master], [], [], 0.5)
            if ready:
                try:
                    chunk = os.read(master, 4096)
                except OSError:
                    break
                if not chunk:
                    break
                out += chunk
                last_out = time.time()
                continue
            if not sent:
                if (out and time.time() - last_out > 1.0) or time.time() - start > 30:
                    os.write(master, payload)
                    sent = True
                    last_out = time.time()
                continue
            if child.poll() is not None:
                break
            if time.time() - last_out > 20:
                break

        try:
            code = child.wait(timeout=15)
        except subprocess.TimeoutExpired:
            child.kill()
            code = -1

        while True:
            ready, _, _ = select.select([master], [], [], 0.2)
            if not ready:
                break
            try:
                chunk = os.read(master, 4096)
            except OSError:
                break
            if not chunk:
                break
            out += chunk

        sys.stdout.buffer.write(bytes(out))
        sys.stdout.buffer.flush()
        sys.stderr.write("child exit %s\n" % code)
        """;

    [E2EFact]
    public async Task Run_dash_it_gives_the_container_a_real_terminal()
    {
        var helper = await WriteHelperAsync();

        var result = await Cmd.RunAsync(
            "python3",
            [helper, "docker", "run", "-i", "-t", "--rm", Image, "sh"],
            daemon.BuildEnvironment(null),
            stdin: "stty size\nTTY=$(tty); echo TTYOK $TTY\nexit\n",
            timeout: TimeSpan.FromMinutes(4));

        Assert.False(result.TimedOut, result.ToString());
        Assert.Contains("TTYOK", result.Stdout, StringComparison.Ordinal);

        // Apple's boot spinner ("[n/6] ..."/"Starting container [Ns]" plus its braille glyphs and
        // hide-cursor preamble) must never reach the client: assert it is absent from everything
        // captured before the first byte of real container/shell output.
        var beforeFirstOutput = result.Stdout[..result.Stdout.IndexOf("TTYOK", StringComparison.Ordinal)];
        Assert.DoesNotMatch(BootSpinnerNoise(), beforeFirstOutput);

        AssertRawTtyStream(result.Stdout);

        var size = SizeRegex().Match(result.Stdout);
        Assert.True(size.Success, "`stty size` printed no rows/cols pair: " + result);
        Assert.Equal("24", size.Groups[1].Value);
        Assert.Equal("100", size.Groups[2].Value);

        Assert.Matches(@"TTYOK\s+/dev/(pts/\d+|console|tty\S*)", result.Stdout);
    }

    [E2EFact]
    public async Task Exec_dash_it_attaches_a_pty_to_a_running_container()
    {
        var helper = await WriteHelperAsync();
        var name = DaemonFixture.NewName("tty");
        var run = await daemon.DockerAsync(["run", "-d", "--name", name, Image, "sleep", "180"], timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var result = await Cmd.RunAsync(
                "python3",
                [helper, "docker", "exec", "-i", "-t", name, "sh"],
                daemon.BuildEnvironment(null),
                stdin: "tty\nstty size\nexit\n",
                timeout: TimeSpan.FromMinutes(3));

            Assert.False(result.TimedOut, result.ToString());
            Assert.Matches(@"/dev/(pts/\d+|console)", result.Stdout);

            // `exec -t` attaches directly to a prompt (no VM boot), so no boot spinner should ever
            // appear here either — see docs/apple-container-notes.md §5c.
            Assert.DoesNotMatch(BootSpinnerNoise(), result.Stdout);

            AssertRawTtyStream(result.Stdout);

            var size = SizeRegex().Match(result.Stdout);
            Assert.True(size.Success, "`stty size` printed no rows/cols pair: " + result);
            Assert.Equal("24", size.Groups[1].Value);
            Assert.Equal("100", size.Groups[2].Value);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// Docker's raw-stream contract for a TTY: the guest's bytes reach the client unmodified.
    /// The guest's own terminal already ends its lines with CRLF, so a
    /// <c>\r\r\n</c> means something translated that output a second time on the way out, and the
    /// DECTCEM hide/show-cursor pair is Apple's CLI decorating a session that is none of its own.
    /// </summary>
    private static void AssertRawTtyStream(string transcript)
    {
        Assert.DoesNotContain("\r\r\n", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[?25l", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[?25h", transcript, StringComparison.Ordinal);
    }

    private async Task<string> WriteHelperAsync()
    {
        var path = Path.Combine(daemon.ScratchDir, "pty_spawn.py");
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, PtyHelper);
        }

        return path;
    }

    [GeneratedRegex(@"(?m)^\s*(\d+)\s+(\d+)\s*$")]
    private static partial Regex SizeRegex();

    /// <summary>
    /// Matches Apple's own boot-progress spinner: "Starting container [Ns]"/numbered "[n/6] ..."
    /// status text, or its braille spinner glyphs (U+2800-U+28FF). Deliberately does not match bare
    /// <c>ESC[...</c> sequences on their own — the guest shell's prompt legitimately emits its own
    /// (e.g. <c>ESC[6n</c> cursor-position queries, <c>ESC[J</c> clears), and the CLI also emits an
    /// unrelated hide/show-cursor pair at process shutdown.
    /// </summary>
    [GeneratedRegex(@"Starting container|\[\d+/\d+\]|[⠀-⣿]")]
    private static partial Regex BootSpinnerNoise();
}
