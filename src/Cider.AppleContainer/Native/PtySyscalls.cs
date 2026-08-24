namespace Cider.AppleContainer.Native;

/// <summary>
/// The syscalls a held CLI process still makes after launch. They all take a descriptor or a pid
/// that can go stale, so they sit behind a seam: tests can then assert that nothing is issued
/// against a released fd or a reaped pid, which is unobservable once the numbers are recycled.
/// </summary>
internal interface IPtySyscalls
{
    /// <summary>Resizes a pty through its master fd.</summary>
    int SetWindowSize(int master, int cols, int rows);

    /// <summary>Bytes already queued on <paramref name="fd"/>; <c>-1</c> when the query fails.</summary>
    int PendingBytes(int fd);

    /// <summary>Sends a signal to a process.</summary>
    int Kill(int pid, int signal);

    /// <summary>Closes a descriptor.</summary>
    int Close(int fd);
}

/// <summary>The real syscalls, as used in production.</summary>
internal sealed class LibcSyscalls : IPtySyscalls
{
    public static readonly LibcSyscalls Instance = new();

    private LibcSyscalls()
    {
    }

    public int SetWindowSize(int master, int cols, int rows) => Libc.SetWindowSize(master, cols, rows);

    public int PendingBytes(int fd) => Libc.PendingBytes(fd);

    public int Kill(int pid, int signal) => Libc.Kill(pid, signal);

    public int Close(int fd) => Libc.Close(fd);
}
