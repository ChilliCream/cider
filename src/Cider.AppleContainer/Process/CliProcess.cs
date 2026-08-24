using Cider.AppleContainer.Native;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using SysProcess = System.Diagnostics.Process;

namespace Cider.AppleContainer.Process;

/// <summary>
/// A held <c>container start -a</c> / <c>container exec</c> child process exposed as an
/// <see cref="IContainerProcess"/>. The exit code of the CLI child is the guest process's own
/// exit code (docs/apple-container-notes.md §4).
/// </summary>
internal sealed class CliProcess : IContainerProcess
{
    /// <summary>How long the pty master may still have unread bytes before the slave is released anyway.</summary>
    private static readonly TimeSpan PtyDrainBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PtyDrainPoll = TimeSpan.FromMilliseconds(2);

    private readonly SysProcess _process;
    private readonly Stream? _stdin;
    private readonly Stream _stdout;
    private readonly Stream? _stderr;
    private readonly int _slaveFd;
    private readonly int _inputSlaveFd;
    private readonly IPtySyscalls _syscalls;
    private readonly Func<string, CancellationToken, Task>? _signalDelegate;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    /// <summary>
    /// The master of the pty the CLI's output arrives on, or <c>-1</c> once it was closed. Read and
    /// written under <see cref="_gate"/> only.
    /// </summary>
    private int _masterFd;

    /// <summary>
    /// The master of the second pty, the one carrying the client's input into the CLI's stdin (see
    /// <see cref="ProcessLauncher.StartPty"/> for why the two directions cannot share one), or
    /// <c>-1</c> once it was closed. Read and written under <see cref="_gate"/> only.
    /// </summary>
    private int _inputMasterFd;
    private int _slaveReleased;
    private int _inputSlaveReleased;
    private volatile bool _stdinClosed;
    private volatile bool _disposed;

    public CliProcess(
        SysProcess process,
        Stream? stdin,
        Stream stdout,
        Stream? stderr,
        bool hasTty,
        int masterFd,
        int slaveFd,
        int inputMasterFd,
        int inputSlaveFd,
        Func<string, CancellationToken, Task>? signalDelegate,
        IPtySyscalls syscalls,
        ILogger logger)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _masterFd = masterFd;
        _slaveFd = slaveFd;
        _inputMasterFd = inputMasterFd;
        _inputSlaveFd = inputSlaveFd;
        _syscalls = syscalls;
        _signalDelegate = signalDelegate;
        _logger = logger;
        HasTty = hasTty;

        Exited = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync();
                return _process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
                return -1;
            }
            finally
            {
                // Deliberately not awaited: the exit code must not wait for a reader to catch up,
                // and the release itself has to happen only once the master has been drained.
                _ = ReleasePtyAsync();
            }
        });
    }

    public int? Pid
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool HasTty { get; }

    /// <summary>
    /// Writable stdin. A pty keeps it for the whole session: the master carries both directions,
    /// so a client's half-close cannot end input the way an EOF on a pipe does, and a terminal has
    /// no "EOF and then keep reading" state — dropping input after a <c>CloseWrite</c> would
    /// silently swallow every later keystroke of a still-interactive session.
    /// </summary>
    public Stream? Stdin => _stdinClosed && !HasTty ? null : _stdin;

    public Stream Stdout => _stdout;

    public Stream? Stderr => _stderr;

    public Task<int> Exited { get; }

    /// <summary>
    /// Docker's <c>CloseWrite</c>. On a pipe it is a real half-close: the write end is disposed and
    /// the child reads EOF. On a pty there is nothing to half-close — the master carries both
    /// directions — so it is recorded and nothing else, and <see cref="Stdin"/> deliberately stays
    /// open (a terminal has no "EOF, then keep reading" state, and the session is still live).
    /// </summary>
    public Task CloseStdinAsync()
    {
        lock (_gate)
        {
            if (_stdinClosed)
            {
                return Task.CompletedTask;
            }

            _stdinClosed = true;
        }

        _logger.LogDebug("stdin of the held CLI process was closed (tty={Tty})", HasTty);

        if (!HasTty && _stdin is not null)
        {
            try
            {
                _stdin.Flush();
                _stdin.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The child already went away.
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resizes the pty. A resize arrives on every terminal <c>SIGWINCH</c> the client sees, so it
    /// routinely races the end of the session: once the master is closed or the child is gone,
    /// the descriptor number and the pid belong to whatever the OS handed them to next, and the
    /// resize has to become a silent no-op rather than signal a stranger. It never throws — the
    /// endpoint contract is 200 in every case (docs/ARCHITECTURE.md §3).
    /// </summary>
    public Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        if (!HasTty)
        {
            return Task.CompletedTask;
        }

        // The ioctl runs under the same lock that closes the masters, so an fd cannot be released
        // (and its number reused) between the check and the call. Both ptys are resized: the CLI
        // reads the size off its stdin, and anything asking its stdout has to get the same answer.
        lock (_gate)
        {
            if (_masterFd < 0 && _inputMasterFd < 0)
            {
                return Task.CompletedTask;
            }

            // `|` and not `||`: the second pty is resized even when the first one failed.
            if (!(Resize(_inputMasterFd) | Resize(_masterFd)))
            {
                return Task.CompletedTask;
            }

            bool Resize(int master)
            {
                if (master < 0)
                {
                    return false;
                }

                if (_syscalls.SetWindowSize(master, cols, rows) != 0)
                {
                    _logger.LogDebug("TIOCSWINSZ failed for pty {Fd}", master);
                    return false;
                }

                return true;
            }
        }

        SignalChild(Libc.SIGWINCH);
        return Task.CompletedTask;
    }

    public async Task KillAsync(string signal, CancellationToken ct)
    {
        if (Exited.IsCompleted)
        {
            return;
        }

        if (_signalDelegate is not null)
        {
            try
            {
                await _signalDelegate(signal, ct);
                return;
            }
            catch (RuntimeException ex)
            {
                _logger.LogDebug("signalling the container failed: {Message}", ex.Message);
                return;
            }
        }

        // Exec processes have no CLI-level signal command: signal the client process instead.
        SignalChild(SignalNumber(signal));
    }

    /// <summary>
    /// Signals the CLI child, but only while it is demonstrably alive. macOS hands a reaped pid to
    /// the next process it starts, so signalling on the strength of a remembered <c>Process.Id</c>
    /// can hit an unrelated program.
    /// </summary>
    private void SignalChild(int signal)
    {
        if (_disposed || Exited.IsCompleted)
        {
            return;
        }

        int pid;
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            pid = _process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            return;
        }

        if (pid > 0)
        {
            _syscalls.Kill(pid, signal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Already gone.
        }

        await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(2)));
        ReleaseSlaves();

        // Disposing the streams closes the masters, and a descriptor number is free the instant it
        // does. Retiring the fields first — under the gate every other user of the fds holds — is
        // what makes those users see "released" instead of a number the OS has already handed to
        // someone else's pty, file or socket.
        lock (_gate)
        {
            _masterFd = -1;
            _inputMasterFd = -1;
        }

        DisposeQuietly(_stdin);
        DisposeQuietly(_stdout);
        DisposeQuietly(_stderr);
        _process.Dispose();
    }

    /// <summary>
    /// Hands the pty back once the child is gone and the master has nothing left to deliver.
    /// <para>
    /// Darwin ends a read on the pty master as soon as <em>no</em> slave fd is open anywhere, so
    /// the daemon holds one itself for the child's whole lifetime: without it the first read of a
    /// child that has not opened the slave device yet returns a clean EOF and the reader stops for
    /// good, and any moment the child drops its own stdio truncates the stream the same way.
    /// Closing this last slave fd is what turns the master into an ordinary end-of-stream — but
    /// closing it while bytes are still queued makes Darwin discard them, so it waits for the
    /// master to run dry first (the reader drains it concurrently).
    /// </para>
    /// </summary>
    private async Task ReleasePtyAsync()
    {
        // Nothing reads the input pty, so its slave has nothing to drain — it only had to outlive
        // the child so that a write racing the start of the session could not fail with EIO.
        ReleaseInputSlave();

        if (_slaveFd < 0 || _slaveReleased != 0)
        {
            return;
        }

        try
        {
            var waited = TimeSpan.Zero;
            while (!_disposed && waited < PtyDrainBudget && PendingOnMaster() > 0)
            {
                await Task.Delay(PtyDrainPoll);
                waited += PtyDrainPoll;
            }

            if (waited >= PtyDrainBudget)
            {
                _logger.LogWarning(
                    "nothing read the pty of an exited CLI process within {Budget}; its last output is lost",
                    PtyDrainBudget);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "waiting for the pty to drain failed");
        }

        ReleaseSlaves();
    }

    /// <summary>
    /// Bytes still queued on the pty master, or <c>-1</c> once it was retired. The query runs under
    /// the same gate that retires the fd, so it can never be issued against a descriptor number
    /// <see cref="DisposeAsync"/> has already released.
    /// </summary>
    private int PendingOnMaster()
    {
        lock (_gate)
        {
            return _masterFd < 0 ? -1 : _syscalls.PendingBytes(_masterFd);
        }
    }

    /// <summary>Closes both of the daemon's own slave fds, each exactly once.</summary>
    private void ReleaseSlaves()
    {
        ReleaseInputSlave();

        if (_slaveFd < 0 || Interlocked.Exchange(ref _slaveReleased, 1) != 0)
        {
            return;
        }

        _syscalls.Close(_slaveFd);
    }

    /// <summary>Closes the daemon's own slave fd of the input pty exactly once.</summary>
    private void ReleaseInputSlave()
    {
        if (_inputSlaveFd < 0 || Interlocked.Exchange(ref _inputSlaveReleased, 1) != 0)
        {
            return;
        }

        _syscalls.Close(_inputSlaveFd);
    }

    /// <summary>macOS signal numbers for the names Docker clients use.</summary>
    internal static int SignalNumber(string? signal)
    {
        var name = Cli.ArgBuilder.NormalizeSignal(signal);
        return name switch
        {
            "HUP" => 1,
            "INT" => 2,
            "QUIT" => 3,
            "ABRT" => 6,
            "KILL" => Libc.SIGKILL,
            "ALRM" => 14,
            "TERM" => Libc.SIGTERM,
            "USR1" => 30,
            "USR2" => 31,
            "WINCH" => Libc.SIGWINCH,
            _ => Libc.SIGTERM,
        };
    }

    private static void DisposeQuietly(Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Nothing to do.
        }
    }
}
