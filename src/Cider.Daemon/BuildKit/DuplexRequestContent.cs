using System.Buffers;
using System.Net;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// An <see cref="HttpContent"/> that streams an already-open request body
/// (<c>HttpContext.Request.Body</c>) straight onto the outgoing connection instead of buffering it.
/// <see cref="SerializeToStreamAsync(Stream,TransportContext?,CancellationToken)"/> copies it in
/// chunks no larger than <paramref name="maxChunk"/> bytes, flushing after every chunk so a gRPC
/// frame is never held back waiting for a bigger buffer to fill, and -- when
/// <paramref name="pacer"/> is set -- awaits <see cref="IUpstreamPacer.AcquireAsync"/> for each
/// chunk's byte count immediately before writing it, so a fast client cannot outrun a slow target
/// (see <see cref="IUpstreamPacer"/>).
/// <para>
/// Completes when <paramref name="requestBody"/> reaches EOF -- i.e. when the client half-closes --
/// which lets the request side of a duplex gRPC stream end independently of the response side still
/// being read. This type never disposes <paramref name="requestBody"/>: it does not own it.
/// </para>
/// <see cref="TryComputeLength"/> always reports the length as unknown, so the outgoing request
/// never carries a <c>Content-Length</c> -- a gRPC message's full size is not known ahead of time.
/// </summary>
public sealed class DuplexRequestContent(Stream requestBody, int maxChunk, IUpstreamPacer? pacer) : HttpContent
{
    private readonly Stream _requestBody = requestBody ?? throw new ArgumentNullException(nameof(requestBody));

    private readonly int _maxChunk = maxChunk > 0
        ? maxChunk
        : throw new ArgumentOutOfRangeException(nameof(maxChunk), maxChunk, "cider: maxChunk must be positive");

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    /// <summary>
    /// The cancellation-aware overload the HTTP/2 send pipeline actually calls; the 2-arg override
    /// above only exists because <see cref="HttpContent"/> declares it <see langword="abstract"/>.
    /// </summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(_maxChunk);
        try
        {
            int read;
            while ((read = await _requestBody.ReadAsync(buffer.AsMemory(0, _maxChunk), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (pacer is not null)
                {
                    await pacer.AcquireAsync(read, cancellationToken).ConfigureAwait(false);
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
