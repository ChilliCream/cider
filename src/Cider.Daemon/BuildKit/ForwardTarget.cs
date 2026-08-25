namespace Cider.Daemon.BuildKit;

/// <summary>
/// Throttles how fast <see cref="DuplexRequestContent"/> may push request bytes onto the target
/// connection. The peer's own HTTP/2 receive window does not bound this on its own (grpc-go's BDP
/// estimator can grow it to 16 MiB -- internal/transport/bdp_estimator.go:27-30), so the builder
/// link (T5: a token-bucket pacer keyed to the exec pipe backing <c>buildctl dial-stdio</c>) plugs
/// in here rather than trusting flow control alone. This forwarder only calls it; it never
/// implements one (see cider-ger.7 non-goals).
/// </summary>
public interface IUpstreamPacer
{
    /// <summary>
    /// Waits until <paramref name="byteCount"/> bytes may be written upstream. Called once per
    /// chunk, immediately before that chunk is written.
    /// </summary>
    ValueTask AcquireAsync(int byteCount, CancellationToken cancellationToken);
}

/// <summary>
/// Everything <see cref="GrpcForwarder.ForwardAsync"/> needs to relay one gRPC call to some other
/// HTTP/2 endpoint: where to send it (<see cref="Invoker"/>, <see cref="Authority"/>), how large an
/// upstream chunk may get before it must be flushed (<see cref="MaxUpstreamChunk"/>), an optional
/// pacer throttling those writes (<see cref="Pacer"/>), and a hook the caller can use to react to a
/// failed forward (<see cref="OnFailure"/>) -- e.g. T5 invalidating a stale builder link.
/// </summary>
public sealed class ForwardTarget
{
    private const int DefaultMaxUpstreamChunk = 32 * 1024;

    /// <summary>The already-connected invoker the call is relayed through (see <c>StreamHttp2Client.Create</c>).</summary>
    public required HttpMessageInvoker Invoker { get; init; }

    /// <summary>The authority (host[:port]) the outgoing request is addressed to.</summary>
    public required string Authority { get; init; }

    /// <summary>
    /// The largest chunk <see cref="DuplexRequestContent"/> reads from the client's request body
    /// before writing and flushing it upstream. Defaults to 32 KiB.
    /// </summary>
    public int MaxUpstreamChunk { get; init; } = DefaultMaxUpstreamChunk;

    /// <summary>When set, throttles upstream writes (T5: a token-bucket pacer for the builder link).</summary>
    public IUpstreamPacer? Pacer { get; init; }

    /// <summary>
    /// Invoked once per failed forward -- after the client-facing error response has been produced,
    /// or the response aborted if headers were already committed -- so the caller can react (e.g.
    /// T5 invalidating a stale builder link).
    /// </summary>
    public Action<Exception>? OnFailure { get; init; }
}
