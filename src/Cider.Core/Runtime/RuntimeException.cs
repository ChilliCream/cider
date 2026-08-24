namespace Cider.Core.Runtime;

/// <summary>The error classes a runtime can report; managers map them to HTTP status codes.</summary>
public enum RuntimeErrorKind
{
    NotFound,
    Conflict,
    InvalidArgument,
    NotSupported,
    Unavailable,
    Internal,

    /// <summary>
    /// The runtime took the call and then never answered within the budget the daemon gave it.
    /// Distinct from <see cref="Unavailable"/> — the runtime is reachable, it is simply not
    /// responding — and mapped to 500, the status dockerd answers a failed operation with, rather
    /// than the 503 that invites a client to retry into the same stall.
    /// </summary>
    Timeout,
}

/// <summary>
/// A finer-grained cause within a <see cref="RuntimeErrorKind"/>, for the conditions the daemon has
/// to branch on rather than merely report. A reason never changes the HTTP status its kind maps to:
/// <see cref="ContainerNotRunning"/> is a <see cref="RuntimeErrorKind.Conflict"/> and still answers
/// 409. Runtimes classify these themselves — nothing above the <see cref="IContainerRuntime"/> seam
/// may read an exception's message text to recognise them.
/// </summary>
public enum RuntimeErrorReason
{
    /// <summary>Nothing beyond the <see cref="RuntimeErrorKind"/> itself.</summary>
    None,

    /// <summary>
    /// The runtime turned the operation down because the target container is not running. Two paths
    /// depend on it: the retry that covers an <c>exec</c> racing a container that has only just
    /// started, and the <c>docker cp</c>-into-a-container-that-is-not-running fallback that stages
    /// the tar for replay. A runtime that reports a bare <see cref="RuntimeErrorKind.Conflict"/>
    /// here loses both, silently.
    /// </summary>
    ContainerNotRunning,
}

/// <summary>An error raised by an <see cref="IContainerRuntime"/> implementation.</summary>
public sealed class RuntimeException : Exception
{
    public RuntimeException(RuntimeErrorKind kind, string message)
        : this(kind, message, RuntimeErrorReason.None)
    {
    }

    public RuntimeException(RuntimeErrorKind kind, string message, RuntimeErrorReason reason)
        : base(message)
    {
        Kind = kind;
        Reason = reason;
    }

    public RuntimeException(RuntimeErrorKind kind, string message, Exception? innerException)
        : this(kind, message, innerException, RuntimeErrorReason.None)
    {
    }

    public RuntimeException(RuntimeErrorKind kind, string message, Exception? innerException, RuntimeErrorReason reason)
        : base(message, innerException)
    {
        Kind = kind;
        Reason = reason;
    }

    /// <summary>What went wrong, in runtime-agnostic terms.</summary>
    public RuntimeErrorKind Kind { get; }

    /// <summary>The finer cause within <see cref="Kind"/>, when the runtime could name one.</summary>
    public RuntimeErrorReason Reason { get; }

    /// <summary><c>true</c> when the runtime refused because the container is not running.</summary>
    public bool IsContainerNotRunning => Reason == RuntimeErrorReason.ContainerNotRunning;

    public static RuntimeException NotFound(string message) => new(RuntimeErrorKind.NotFound, message);

    public static RuntimeException Conflict(string message) => new(RuntimeErrorKind.Conflict, message);

    /// <summary>
    /// A <see cref="RuntimeErrorKind.Conflict"/> the runtime raised because the container is not
    /// running — still a 409, but recognisable as
    /// <see cref="RuntimeErrorReason.ContainerNotRunning"/> without reading <paramref name="message"/>.
    /// </summary>
    public static RuntimeException ContainerNotRunning(string message) =>
        new(RuntimeErrorKind.Conflict, message, RuntimeErrorReason.ContainerNotRunning);

    public static RuntimeException InvalidArgument(string message) => new(RuntimeErrorKind.InvalidArgument, message);

    public static RuntimeException NotSupported(string message) => new(RuntimeErrorKind.NotSupported, message);

    public static RuntimeException Unavailable(string message) => new(RuntimeErrorKind.Unavailable, message);

    public static RuntimeException Timeout(string message) => new(RuntimeErrorKind.Timeout, message);

    public static RuntimeException Internal(string message) => new(RuntimeErrorKind.Internal, message);
}
