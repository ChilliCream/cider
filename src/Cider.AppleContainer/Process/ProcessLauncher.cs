using System.Diagnostics;
using System.Text.RegularExpressions;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Native;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SysProcess = System.Diagnostics.Process;

namespace Cider.AppleContainer.Process;

/// <summary>
/// Launches the <c>container</c> CLI so that the daemon can hold the guest process's stdio:
/// either with ordinary pipes, or on a pty cloned from <c>/dev/ptmx</c>
/// (the recipe validated in docs/apple-container-notes.md §5b).
/// </summary>
internal sealed partial class ProcessLauncher
{
    private const int DefaultCols = 80;
    private const int DefaultRows = 24;

    private readonly ContainerCli _cli;
    private readonly IPtySyscalls _syscalls;
    private readonly ILogger _logger;

    public ProcessLauncher(ContainerCli cli, ILogger logger, IPtySyscalls? syscalls = null)
    {
        _cli = cli;
        _syscalls = syscalls ?? LibcSyscalls.Instance;
        _logger = logger;
    }

    [GeneratedRegex(@"^/dev/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TtyPathRegex();

    /// <summary>Pipe mode: stdin/stdout/stderr are separate pipes, exit code from the child.</summary>
    public CliProcess StartPipe(
        IReadOnlyList<string> args,
        bool openStdin,
        Func<string, CancellationToken, Task>? signalDelegate)
    {
        var startInfo = _cli.CreateStartInfo(args);
        // Positive marker for OrphanReaper: ppid==1 alone also matches a user's own launchd-managed
        // or nohup'd `container start -a`, which the daemon has no business killing. Only children
        // carrying this env var (visible via `ps -axeo`) are ever reaped.
        startInfo.Environment[OrphanReaper.HeldChildMarker] = "1";
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        _logger.LogDebug("container CLI (pipe): {Cli} {Args}", _cli.CliPath, string.Join(' ', args));

        var process = new SysProcess { StartInfo = startInfo };
        Start(process, args);

        Stream? stdin = process.StandardInput.BaseStream;
        if (!openStdin)
        {
            try
            {
                stdin.Dispose();
            }
            catch (IOException)
            {
                // The child may already be gone.
            }

            stdin = null;
        }

        return new CliProcess(
            process,
            stdin,
            process.StandardOutput.BaseStream,
            process.StandardError.BaseStream,
            hasTty: false,
            masterFd: -1,
            slaveFd: -1,
            inputMasterFd: -1,
            inputSlaveFd: -1,
            signalDelegate,
            _syscalls,
            _logger);
    }

    /// <summary>
    /// PTY mode: allocate a pty for the CLI's stdin and a second one for its stdout, hand both
    /// slaves to a <c>/bin/sh</c> wrapper that redirects the CLI's fds onto them, and keep the two
    /// masters as the daemon-side streams. The CLI's stderr deliberately stays an ordinary pipe.
    /// <para>
    /// The three fds cannot share one pty. <c>container exec -i</c> puts its
    /// <em>stdin</em> terminal into its own idea of raw mode, and that setting re-enables
    /// <c>OPOST</c>/<c>ONLCR</c> — on a shared pty that translation also applies to the guest's
    /// output, which already carries CRLF, and every line reaches the client as <c>\r\r\n</c>.
    /// Splitting the two directions leaves the output pty's line discipline under the daemon's own
    /// control, where <see cref="Libc.SetRawIo"/> has already turned output processing off.
    /// Apple's boot spinner and the hide/show-cursor pair it brackets a session with are written to
    /// <em>stderr</em>, and only when that stderr is a terminal: giving the CLI a pipe there stops
    /// it emitting them at all instead of filtering them out afterwards.
    /// </para>
    /// </summary>
    public CliProcess StartPty(
        IReadOnlyList<string> args,
        int cols,
        int rows,
        Func<string, CancellationToken, Task>? signalDelegate)
    {
        var size = new WinSize
        {
            Cols = (ushort)(cols > 0 ? cols : DefaultCols),
            Rows = (ushort)(rows > 0 ? rows : DefaultRows),
        };

        // Both ends of both pairs come back close-on-exec, set atomically by `open` itself: no fd
        // may leak into any other CLI child, and marking them afterwards leaves a window in which a
        // child started on another thread inherits them. An inherited slave keeps the pty alive for
        // as long as that unrelated process lives — its reader then never sees the end of the
        // stream — and an inherited master keeps the device allocated. The child gets its ptys by
        // path.
        var output = OpenPty(ref size);
        PtyPair input;
        try
        {
            input = OpenPty(ref size);
        }
        catch
        {
            output.Close();
            throw;
        }

        // The wrapper opens both slaves itself and then execs the CLI, so no file descriptor has to
        // be inherited. fd 2 is left as the pipe .NET redirected, which is what keeps Apple's
        // terminal-only progress rendering switched off.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        // exec(2) preserves the environment, so the marker survives the wrapper becoming the CLI.
        startInfo.Environment[OrphanReaper.HeldChildMarker] = "1";

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"exec \"$0\" \"$@\" 0<>{input.Path} 1>{output.Path}");
        startInfo.ArgumentList.Add(_cli.CliPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogDebug(
            "container CLI (pty in {In}, out {Out}): {Cli} {Args}",
            input.Path,
            output.Path,
            _cli.CliPath,
            string.Join(' ', args));

        var process = new SysProcess { StartInfo = startInfo };
        try
        {
            Start(process, args);
        }
        catch
        {
            output.Close();
            input.Close();
            throw;
        }

        // The parent deliberately keeps its own slave fds open for as long as the child runs.
        // Darwin ends reads on a master the moment no slave fd is left open at all, and the child
        // only opens the slave devices itself once it has been scheduled, so closing them here
        // makes the very first read of a slow-starting child return EOF and silently truncates the
        // whole stream. CliProcess releases them once the child exited and the master ran dry.
        var stdin = MasterStream(input);

        // Apple's boot spinner no longer reaches the output pty at all (it follows stderr, which is
        // a pipe now), but the filter stays wired as the guard its own contract describes: it only
        // ever activates on a stream whose very first bytes are the hide-cursor sequence.
        var stdout = new PtyBootFilterStream(MasterStream(output), _logger);

        DrainCliErrors(process, args);

        return new CliProcess(
            process,
            stdin,
            stdout,
            stderr: null,
            hasTty: true,
            masterFd: output.Master,
            slaveFd: output.Slave,
            inputMasterFd: input.Master,
            inputSlaveFd: input.Slave,
            signalDelegate,
            _syscalls,
            _logger);
    }

    /// <summary>A pty pair and the slave device path the wrapper shell opens it by.</summary>
    private readonly record struct PtyPair(int Master, int Slave, string Path)
    {
        public void Close()
        {
            Libc.Close(Master);
            Libc.Close(Slave);
        }
    }

    private static PtyPair OpenPty(ref WinSize size)
    {
        if (Libc.OpenPty(out var master, out var slave, out var slavePath, out var openError, ref size) != 0)
        {
            throw RuntimeException.Internal($"cannot allocate a pseudo terminal: {openError}");
        }

        if (slavePath is null || !TtyPathRegex().IsMatch(slavePath))
        {
            Libc.Close(master);
            Libc.Close(slave);
            throw RuntimeException.Internal($"unexpected pty device path '{slavePath}'");
        }

        return new PtyPair(master, slave, slavePath);
    }

    private PtyStream MasterStream(PtyPair pty)
    {
        var handle = new SafeFileHandle((IntPtr)pty.Master, ownsHandle: true);
        return new PtyStream(
            new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false),
            _logger,
            pty.Path);
    }

    /// <summary>
    /// Keeps the CLI's stderr pipe empty and logs whatever came out of it. Nothing may be left
    /// unread — a full pipe blocks the CLI. It stays out of the client's stream on purpose: with
    /// stderr no longer a terminal this is where Apple's progress chatter would land, while the
    /// errors that matter to the client are reported on the session's own stream and by the exit
    /// code.
    /// </summary>
    private void DrainCliErrors(SysProcess process, IReadOnlyList<string> args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogDebug(
                        "container CLI (pty) wrote to stderr: {Args} :: {Stderr}",
                        string.Join(' ', args),
                        text.Trim());
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The child is gone and the pipe with it.
            }
        });
    }

    /// <summary>Runs a command whose stdout is streamed (e.g. <c>container logs -f</c>), killing it on dispose.</summary>
    public ProcessOutputStream StartStreaming(IReadOnlyList<string> args)
    {
        var startInfo = _cli.CreateStartInfo(args);
        // Positive marker for OrphanReaper: ppid==1 alone also matches a user's own launchd-managed
        // or nohup'd `container start -a`, which the daemon has no business killing. Only children
        // carrying this env var (visible via `ps -axeo`) are ever reaped.
        startInfo.Environment[OrphanReaper.HeldChildMarker] = "1";
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        _logger.LogDebug("container CLI (stream): {Cli} {Args}", _cli.CliPath, string.Join(' ', args));

        var process = new SysProcess { StartInfo = startInfo };
        Start(process, args);
        process.StandardInput.Close();

        return new ProcessOutputStream(process);
    }

    private void Start(SysProcess process, IReadOnlyList<string> args)
    {
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            throw new RuntimeException(
                RuntimeErrorKind.Unavailable,
                $"cannot run '{_cli.CliPath} {string.Join(' ', args)}': {ex.Message}",
                ex);
        }
    }
}
