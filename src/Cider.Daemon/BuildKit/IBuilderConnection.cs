namespace Cider.Daemon.BuildKit;

/// <summary>
/// The single, shared link to buildkitd inside Apple's builder VM: one <c>container exec -i buildkit
/// buildctl dial-stdio</c> child process wrapped in an HTTP/2 gRPC channel, kept alive across many
/// callers and re-established when it dies or stalls. See <see cref="BuilderConnection"/> for the
/// dial/redial/stall-recovery policy.
/// </summary>
public interface IBuilderConnection
{
    /// <summary>
    /// Returns the current live <see cref="BuilderLink"/>, dialing a new one (starting the builder
    /// VM first if needed) when there is none yet, the previous one died, or it was invalidated.
    /// Throws <see cref="BuilderUnavailableException"/> when BuildKit is disabled, the builder VM
    /// cannot be started, or the post-dial liveness probe fails.
    /// </summary>
    ValueTask<BuilderLink> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Marks <paramref name="link"/> dead so the next <see cref="GetAsync"/> dials a fresh one, and
    /// disposes it. A no-op when <paramref name="link"/> has already been superseded by a newer link
    /// (e.g. two concurrent callers both observing the same failure). <paramref name="reason"/> is
    /// logged when given; pass <see langword="null"/> for a stall (no exception to report).
    /// </summary>
    void Invalidate(BuilderLink link, Exception? reason);
}
