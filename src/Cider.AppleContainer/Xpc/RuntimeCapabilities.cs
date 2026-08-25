namespace Cider.AppleContainer.Xpc;

/// <summary>Which transport a <see cref="RuntimeCapabilities"/> describes.</summary>
public enum RuntimeTransportKind
{
    /// <summary>Talks to <c>com.apple.container.apiserver</c> directly over XPC.</summary>
    Xpc,

    /// <summary>Shells out to the Apple <c>container</c> CLI.</summary>
    Cli,
}

/// <summary>
/// What the transport <see cref="RuntimeTransportSelector"/> decided on at startup can actually do —
/// computed once and registered as a DI singleton (task cider-ede.4's fix direction §2) so a call
/// site gates on a capability up front (e.g. before offering <c>networkCreate</c> or
/// <c>maskedPaths</c>) instead of discovering the apiserver does not support it mid-call.
/// </summary>
public sealed class RuntimeCapabilities
{
    /// <summary>
    /// The transport the version gate settled on. Until cider-ede.5 lands the XPC container runtime,
    /// the <see cref="RuntimeSelection.Runtime"/> handed back alongside this is always CLI-backed
    /// regardless of this value (see <see cref="RuntimeTransportSelector"/>'s own doc comment) — this
    /// field already reports the real decision so later tasks, and this task's own tests, can rely
    /// on it without waiting for X5.
    /// </summary>
    public required RuntimeTransportKind Transport { get; init; }

    /// <summary>
    /// The apiserver's own reported version, set whenever a <c>ping</c> got a parseable reply at all
    /// (both <c>auto</c> and <c>xpc</c> configurations attempt one) — regardless of whether the
    /// version gate then accepted or rejected it. <c>null</c> when the apiserver never answered, or
    /// when <c>RuntimeTransport=cli</c> skipped the ping entirely.
    /// </summary>
    public ApiServerVersion? ApiServerVersion { get; init; }

    /// <summary>
    /// Why <see cref="Transport"/> came out <see cref="RuntimeTransportKind.Cli"/> when the
    /// configuration did not simply request that (<c>RuntimeTransport=cli</c>): the apiserver never
    /// answered, or answered older than <see cref="Xpc.ApiServerVersion.Minimum"/>. <c>null</c> for
    /// <see cref="RuntimeTransportKind.Xpc"/> and for an explicit <c>cli</c> configuration — there is
    /// nothing to explain in either case.
    /// </summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// <c>true</c> when <c>networkCreate</c> is available — Apple's own gate, not the apiserver's:
    /// the route exists only on macOS 26+ (task description), independent of <see cref="Transport"/>.
    /// </summary>
    public bool NetworkCreate { get; init; }

    /// <summary>
    /// <c>true</c> when <c>containerCreate</c>'s <c>maskedPaths</c>/<c>readonlyPaths</c> are honoured
    /// — added in apiserver 1.2.0 (task description), so this is <c>true</c> whenever
    /// <see cref="ApiServerVersion"/> reported at least that version.
    /// </summary>
    public bool MaskedPaths { get; init; }
}
