using Cider.AppleContainer.Native;
using Cider.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// One host-owned <c>pipe(2)+CLOEXEC</c> pair (task cider-ede.7 fix direction §1): one end is handed
/// to the guest as an <c>xpc_fd</c> in a <c>containerBootstrap</c> request (<see cref="ReadFd"/> for
/// stdin, <see cref="WriteFd"/> for stdout/stderr — <c>xpc_dictionary_set_fd</c>/<c>xpc_fd_create</c>
/// dup the descriptor into the message rather than taking it over, so the original still has to be
/// closed by the caller once the far end has been handed off), the other stays with the daemon as
/// the process's own stdio stream (<see cref="DetachReadStream"/>/<see cref="DetachWriteStream"/>).
/// Disposing at any point before a stream is detached closes whichever ends are still open — every
/// <c>Close*</c>/<c>Detach*</c> operation is idempotent, so a caller never has to track what it
/// already did to this instance.
/// </summary>
internal sealed class HostPipe : IDisposable
{
    private int _readFd;
    private int _writeFd;

    private HostPipe(int readFd, int writeFd)
    {
        _readFd = readFd;
        _writeFd = writeFd;
    }

    /// <summary>The pipe's read end, or <c>-1</c> once closed/detached.</summary>
    public int ReadFd => _readFd;

    /// <summary>The pipe's write end, or <c>-1</c> once closed/detached.</summary>
    public int WriteFd => _writeFd;

    /// <summary>Allocates a fresh pipe. Throws <see cref="RuntimeException"/> (kind
    /// <see cref="RuntimeErrorKind.Internal"/>) on a syscall failure — this is host-side resource
    /// exhaustion (too many open files), not anything the apiserver could have rejected.</summary>
    public static HostPipe Create()
    {
        if (Libc.Pipe(out var readFd, out var writeFd, out var error) != 0)
        {
            throw RuntimeException.Internal($"pipe() failed: {error}");
        }

        return new HostPipe(readFd, writeFd);
    }

    /// <summary>Closes the read end, once. Safe to call whether or not it was already
    /// closed/detached.</summary>
    public void CloseReadFd()
    {
        var fd = Interlocked.Exchange(ref _readFd, -1);
        if (fd >= 0)
        {
            Libc.Close(fd);
        }
    }

    /// <summary>Closes the write end, once. Safe to call whether or not it was already
    /// closed/detached.</summary>
    public void CloseWriteFd()
    {
        var fd = Interlocked.Exchange(ref _writeFd, -1);
        if (fd >= 0)
        {
            Libc.Close(fd);
        }
    }

    /// <summary>Hands the read end to the caller as an owning <see cref="FileStream"/>; this pipe no
    /// longer closes it. Throws if the read end was already closed or detached.</summary>
    public FileStream DetachReadStream()
    {
        var fd = Interlocked.Exchange(ref _readFd, -1);
        if (fd < 0)
        {
            throw new InvalidOperationException("the pipe's read end was already closed or detached");
        }

        return new FileStream(new SafeFileHandle((nint)fd, ownsHandle: true), FileAccess.Read);
    }

    /// <summary>Hands the write end to the caller as an owning <see cref="FileStream"/>; this pipe no
    /// longer closes it. Throws if the write end was already closed or detached.</summary>
    public FileStream DetachWriteStream()
    {
        var fd = Interlocked.Exchange(ref _writeFd, -1);
        if (fd < 0)
        {
            throw new InvalidOperationException("the pipe's write end was already closed or detached");
        }

        return new FileStream(new SafeFileHandle((nint)fd, ownsHandle: true), FileAccess.Write);
    }

    /// <summary>Closes whichever ends have not already been closed or handed off.</summary>
    public void Dispose()
    {
        CloseReadFd();
        CloseWriteFd();
    }
}
