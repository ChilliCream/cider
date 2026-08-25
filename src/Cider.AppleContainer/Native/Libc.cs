using System.Runtime.InteropServices;

namespace Cider.AppleContainer.Native;

/// <summary>The terminal window size passed to a fresh pty and to <c>TIOCSWINSZ</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinSize
{
    public ushort Rows;
    public ushort Cols;
    public ushort XPixels;
    public ushort YPixels;
}

/// <summary>
/// Darwin's <c>struct termios</c>. Every flag word is a 64-bit <c>tcflag_t</c> and the control
/// characters are a fixed 20-byte array, so the layout below is the ABI one — a mismatch would
/// have <c>tcsetattr</c> write the wrong words, which is why
/// <c>PtyProcessTests.The_pty_is_allocated_with_output_processing_off</c> round-trips a real pty
/// through it instead of trusting the declaration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Termios
{
    public ulong IFlag;
    public ulong OFlag;
    public ulong CFlag;
    public ulong LFlag;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] Cc;

    public ulong ISpeed;
    public ulong OSpeed;
}

/// <summary>The handful of libc entry points the PTY launcher needs on macOS.</summary>
internal static class Libc
{
    /// <summary><c>c_oflag</c>: perform output processing at all.</summary>
    public const ulong OPOST = 0x00000001;

    /// <summary><c>c_oflag</c>: map an outgoing <c>NL</c> to <c>CR NL</c>.</summary>
    public const ulong ONLCR = 0x00000002;

    /// <summary><c>c_lflag</c>: echo input characters back to the reader.</summary>
    public const ulong ECHO = 0x00000008;

    private const int TCSANOW = 0;

    private const string LibSystem = "libSystem.dylib";

    /// <summary><c>ioctl</c> request code for "set window size" on Darwin (<c>_IOW('t', 103, struct winsize)</c>).</summary>
    public const ulong TIOCSWINSZ = 0x80087467;

    /// <summary><c>ioctl</c> request code for "bytes queued for reading" on Darwin (<c>_IOR('f', 127, int)</c>).</summary>
    public const ulong FIONREAD = 0x4004667F;

    private const int O_RDWR = 0x0002;
    private const int O_NOCTTY = 0x00020000;
    private const int O_CLOEXEC = 0x01000000;

    public const int SIGWINCH = 28;
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    /// <summary><c>ptsname</c> answers from a static buffer, so callers have to take turns.</summary>
    private static readonly object PtsNameGate = new();

    /// <summary>The pty master clone device; opening it allocates a fresh pair.</summary>
    private static readonly byte[] PtmxPathBytes = "/dev/ptmx\0"u8.ToArray();

    // `open` is variadic (`mode` follows `oflag`), but the mode is only read for O_CREAT, which
    // this file never passes — so the two-argument declaration is safe on every ABI.
    [DllImport(LibSystem, EntryPoint = "open", SetLastError = true)]
    private static extern int OpenCore(byte[] path, int flags);

    [DllImport(LibSystem, EntryPoint = "open", SetLastError = true)]
    private static extern int OpenCore(IntPtr path, int flags);

    // Internal (not private) so a test can drive a genuine, root-free syscall failure directly —
    // `grantpt` on any fd that is not a ptmx-cloned master fails deterministically with ENOTTY,
    // letting a test exercise the real errno-naming path in `Describe` below.
    [DllImport(LibSystem, EntryPoint = "grantpt", SetLastError = true)]
    internal static extern int GrantPt(int fd);

    [DllImport(LibSystem, EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int UnlockPt(int fd);

    [DllImport(LibSystem, EntryPoint = "ptsname", SetLastError = true)]
    private static extern IntPtr PtsNameCore(int fd);

    [DllImport(LibSystem, EntryPoint = "strerror", SetLastError = false)]
    private static extern IntPtr StrErrorCore(int errnum);

    // `ioctl` is variadic. On Apple ARM64 every variadic argument is passed on the stack, not in a
    // register, so the pointer has to land in the ninth argument slot (x2…x7 are padding); passing
    // it as the third argument makes the kernel read a garbage address. On x86-64 the ordinary
    // three-argument form is correct.
    [DllImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlStack(
        int fd,
        ulong request,
        nint pad2,
        nint pad3,
        nint pad4,
        nint pad5,
        nint pad6,
        nint pad7,
        ref WinSize winp);

    [DllImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlRegister(int fd, ulong request, ref WinSize winp);

    [DllImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlStack(
        int fd,
        ulong request,
        nint pad2,
        nint pad3,
        nint pad4,
        nint pad5,
        nint pad6,
        nint pad7,
        ref int value);

    [DllImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlRegister(int fd, ulong request, ref int value);

    /// <summary>Calls <c>ioctl(fd, request, &amp;winp)</c> honouring the platform's variadic ABI.</summary>
    public static int Ioctl(int fd, ulong request, ref WinSize winp) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlStack(fd, request, 0, 0, 0, 0, 0, 0, ref winp)
            : IoctlRegister(fd, request, ref winp);

    /// <summary>Calls <c>ioctl(fd, request, &amp;value)</c> honouring the platform's variadic ABI.</summary>
    public static int Ioctl(int fd, ulong request, ref int value) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlStack(fd, request, 0, 0, 0, 0, 0, 0, ref value)
            : IoctlRegister(fd, request, ref value);

    /// <summary>Bytes already queued on <paramref name="fd"/>; <c>-1</c> when the query fails.</summary>
    public static int PendingBytes(int fd)
    {
        var pending = 0;
        return Ioctl(fd, FIONREAD, ref pending) != 0 ? -1 : pending;
    }

    [DllImport(LibSystem, EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int TcGetAttr(int fd, ref Termios termios);

    [DllImport(LibSystem, EntryPoint = "tcsetattr", SetLastError = true)]
    private static extern int TcSetAttr(int fd, int actions, ref Termios termios);

    /// <summary>
    /// Turns off the line discipline's own output processing (<c>OPOST</c>/<c>ONLCR</c>) and its
    /// input echo on a freshly allocated pty.
    /// <para>
    /// Docker's contract for a TTY stream is that the guest's bytes reach the client unmodified,
    /// and the guest already has a terminal of its own inside the VM: what arrives on this pty is
    /// therefore <em>already</em> CRLF-terminated. Leaving <c>ONLCR</c> on runs that output through
    /// a second NL→CR-NL translation, so every line reaches the client as <c>\r\r\n</c>.
    /// Echo is turned off for the same reason it would be on a raw terminal —
    /// the guest's own tty echoes what the client types, and nothing reads this side back.
    /// </para>
    /// </summary>
    public static int SetRawIo(int fd, out string? error)
    {
        error = null;
        var attrs = new Termios { Cc = new byte[20] };
        if (TcGetAttr(fd, ref attrs) != 0)
        {
            error = Describe("tcgetattr");
            return -1;
        }

        attrs.OFlag &= ~(OPOST | ONLCR);
        attrs.LFlag &= ~ECHO;

        if (TcSetAttr(fd, TCSANOW, ref attrs) != 0)
        {
            error = Describe("tcsetattr");
            return -1;
        }

        return 0;
    }

    /// <summary>Reads a pty's current <c>c_oflag</c>/<c>c_lflag</c>; <c>null</c> when the call fails.</summary>
    public static (ulong OFlag, ulong LFlag)? GetIoFlags(int fd)
    {
        var attrs = new Termios { Cc = new byte[20] };
        return TcGetAttr(fd, ref attrs) != 0 ? null : (attrs.OFlag, attrs.LFlag);
    }

    [DllImport(LibSystem, EntryPoint = "kill", SetLastError = true)]
    public static extern int Kill(int pid, int signal);

    [DllImport(LibSystem, EntryPoint = "close", SetLastError = true)]
    public static extern int Close(int fd);

    /// <summary><c>fcntl</c>'s <c>F_SETFD</c> command (set the descriptor flags — just <c>FD_CLOEXEC</c>
    /// in practice).</summary>
    private const int F_SETFD = 2;

    /// <summary>The one descriptor flag <c>F_SETFD</c> ever needs to set here: close-on-exec.</summary>
    private const int FD_CLOEXEC = 1;

    [DllImport(LibSystem, EntryPoint = "pipe", SetLastError = true)]
    private static extern int PipeCore(int[] fds);

    [DllImport(LibSystem, EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlSetFd(int fd, int cmd, int arg);

    /// <summary>
    /// <c>pipe(2)</c> with <c>FD_CLOEXEC</c> set on both ends — Darwin has no <c>pipe2</c>, so the
    /// flag has to be applied with a separate <c>fcntl(F_SETFD)</c> per end right after the pipe is
    /// created (cider-ede.7: the daemon-owned stdio pipes handed to <c>containerBootstrap</c> must
    /// never leak into an unrelated child the .NET process later spawns, e.g. a CLI-fallback
    /// invocation). Returns <c>0</c> on success and leaves no descriptor behind on failure, matching
    /// <see cref="OpenPty"/>'s own contract.
    /// </summary>
    public static int Pipe(out int readFd, out int writeFd, out string? error)
    {
        readFd = -1;
        writeFd = -1;
        error = null;

        var fds = new int[2];
        if (PipeCore(fds) != 0)
        {
            error = Describe("pipe");
            return -1;
        }

        if (FcntlSetFd(fds[0], F_SETFD, FD_CLOEXEC) != 0)
        {
            error = Describe("fcntl(F_SETFD, read end)");
            Close(fds[0]);
            Close(fds[1]);
            return -1;
        }

        if (FcntlSetFd(fds[1], F_SETFD, FD_CLOEXEC) != 0)
        {
            error = Describe("fcntl(F_SETFD, write end)");
            Close(fds[0]);
            Close(fds[1]);
            return -1;
        }

        readFd = fds[0];
        writeFd = fds[1];
        return 0;
    }

    /// <summary>
    /// Names a just-failed step for <see cref="OpenPty"/>'s <c>error</c> output. Must be called
    /// before any other P/Invoke into this class — including <see cref="Close"/> — since every one
    /// of them is <c>SetLastError</c> and would overwrite the errno being reported here. Internal
    /// (not private) so a test can exercise this exact formatting against a real errno left by a
    /// deliberately-broken call (see <see cref="GrantPt"/>).
    /// </summary>
    internal static string Describe(string call)
    {
        var errno = Marshal.GetLastPInvokeError();
        var detail = Marshal.PtrToStringUTF8(StrErrorCore(errno));
        return $"{call} failed (errno {errno}{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")})";
    }

    /// <summary>
    /// Allocates a pty pair with an initial window size and hands back the slave device path;
    /// returns <c>0</c> on success, and leaves no descriptor behind on failure. On failure,
    /// <paramref name="error"/> names the step that failed and the errno it left behind, so a pty
    /// allocation failure is diagnosable in the field instead of a bare "cannot allocate".
    /// <para>
    /// The pair is opened straight from <c>/dev/ptmx</c> — exactly what <c>openpty</c> does
    /// internally — because only <c>open</c> can mark a descriptor close-on-exec as part of the
    /// same call. <c>openpty</c> cannot, and setting the flag afterwards leaves a window in which
    /// a CLI child forked on another thread inherits both ends: an inherited slave keeps the pty
    /// alive for that child's whole life, so the session it belongs to never sees the end of its
    /// own output, and an inherited master keeps the device allocated.
    /// </para>
    /// </summary>
    public static int OpenPty(out int master, out int slave, out string? slavePath, out string? error, ref WinSize size)
    {
        master = -1;
        slave = -1;
        slavePath = null;
        error = null;

        var masterFd = OpenCore(PtmxPathBytes, O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (masterFd < 0)
        {
            error = Describe("open(/dev/ptmx)");
            return -1;
        }

        if (GrantPt(masterFd) != 0)
        {
            error = Describe("grantpt");
            Close(masterFd);
            return -1;
        }

        if (UnlockPt(masterFd) != 0)
        {
            error = Describe("unlockpt");
            Close(masterFd);
            return -1;
        }

        int slaveFd;
        lock (PtsNameGate)
        {
            var name = PtsNameCore(masterFd);
            if (name == IntPtr.Zero)
            {
                error = Describe("ptsname");
                Close(masterFd);
                return -1;
            }

            slavePath = Marshal.PtrToStringUTF8(name);
            slaveFd = OpenCore(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
            if (slaveFd < 0)
            {
                error = Describe("open(slave pty)");
            }
        }

        // A pty left at its default 0×0 makes `stty size` and every TUI inside the guest fail
        // (docs/apple-container-notes.md §5b), so the initial size is part of the allocation.
        if (slaveFd >= 0 && string.IsNullOrEmpty(slavePath))
        {
            error = "ptsname returned an empty slave path";
            Close(slaveFd);
            slaveFd = -1;
        }
        else if (slaveFd >= 0 && Ioctl(slaveFd, TIOCSWINSZ, ref size) != 0)
        {
            error = Describe("ioctl(TIOCSWINSZ)");
            Close(slaveFd);
            slaveFd = -1;
        }
        // The guest's output already carries CRLF from its own terminal, so this pty must not add
        // a second CR of its own — see SetRawIo. It is part of the allocation for the same reason
        // the window size is: the CLI child inherits whatever is set here.
        else if (slaveFd >= 0 && SetRawIo(slaveFd, out var rawError) != 0)
        {
            error = rawError;
            Close(slaveFd);
            slaveFd = -1;
        }

        if (slaveFd < 0)
        {
            Close(masterFd);
            slavePath = null;
            return -1;
        }

        master = masterFd;
        slave = slaveFd;
        return 0;
    }

    /// <summary>Resizes a pty through its master fd.</summary>
    public static int SetWindowSize(int master, int cols, int rows)
    {
        var size = new WinSize
        {
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            Cols = (ushort)Math.Clamp(cols, 1, ushort.MaxValue),
        };

        return Ioctl(master, TIOCSWINSZ, ref size);
    }
}
