namespace Cider.Core.Runtime;

/// <summary>A live container init process or exec process held by the runtime: its stdio and exit code.</summary>
public interface IContainerProcess : IAsyncDisposable
{
    /// <summary>Host-side pid of the launched runtime process, when known.</summary>
    int? Pid { get; }

    /// <summary><c>true</c> when the process runs on a pty (stdout carries everything, stderr is null).</summary>
    bool HasTty { get; }

    /// <summary>Writable stdin, or <c>null</c> when stdin was not opened.</summary>
    Stream? Stdin { get; }

    /// <summary>Readable stdout. In TTY mode this carries the merged output.</summary>
    Stream Stdout { get; }

    /// <summary>Readable stderr, or <c>null</c> in TTY mode.</summary>
    Stream? Stderr { get; }

    /// <summary>Completes with the process exit code. Never throws; <c>-1</c> when unknown.</summary>
    Task<int> Exited { get; }

    /// <summary>Half-closes stdin (Docker's <c>CloseWrite</c>) while output keeps streaming.</summary>
    Task CloseStdinAsync();

    /// <summary>Resizes the pty. No-op for pipe mode.</summary>
    Task ResizeAsync(int cols, int rows, CancellationToken ct);

    /// <summary>Best-effort signal delivery to the process.</summary>
    Task KillAsync(string signal, CancellationToken ct);
}
