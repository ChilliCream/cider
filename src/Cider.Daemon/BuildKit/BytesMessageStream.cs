using Google.Protobuf;
using Grpc.Core;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Wraps the two halves of a <c>Control/Session</c> bidi call (<see cref="AsyncDuplexStreamingCall{TRequest,TResponse}"/>
/// over <see cref="BytesMessage"/>) as a plain duplex <see cref="Stream"/> — the shape
/// <see cref="Tunnel.TunnelTransport.ServeAsync(Stream,Tunnel.TunnelKind,string?,IDictionary{string,string[]}?,CancellationToken)"/>
/// needs to hand this raw byte tunnel to Kestrel's HTTP/2 engine, exactly the way
/// <c>session/grpchijack/dial.go</c> treats the same <c>BytesMessage</c> framing on BuildKit's own
/// side. Reads pull bytes out of <see cref="BytesMessage.Data"/> as response messages arrive
/// (buffering the remainder of a message across calls that ask for fewer bytes than it holds);
/// writes chunk the outgoing buffer into pieces no larger than <see cref="MaxWriteChunk"/>, pacing
/// each chunk through an optional <see cref="IUpstreamPacer"/> (<see cref="BuilderLink.Target"/>'s
/// pacer, so this reuses the same throttle a forwarded upload does) and sending — never buffering —
/// immediately, so <see cref="FlushAsync(CancellationToken)"/> has nothing left to do.
/// </summary>
public sealed class BytesMessageStream : Stream
{
    private const int MaxWriteChunk = 32 * 1024;

    private readonly IAsyncStreamReader<BytesMessage> _reader;
    private readonly IClientStreamWriter<BytesMessage> _writer;
    private readonly IUpstreamPacer? _pacer;
    private ReadOnlyMemory<byte> _pending;
    private bool _readerDone;
    private int _disposed;

    public BytesMessageStream(
        IAsyncStreamReader<BytesMessage> reader,
        IClientStreamWriter<BytesMessage> writer,
        IUpstreamPacer? pacer = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _pacer = pacer;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("cider: a session bridge stream has no length");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("cider: a session bridge stream has no position");
        set => throw new NotSupportedException("cider: a session bridge stream has no position");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty)
        {
            if (_readerDone)
            {
                return 0;
            }

            if (!await _reader.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                _readerDone = true;
                return 0;
            }

            _pending = _reader.Current.Data.Memory;

            // A zero-length BytesMessage carries no bytes but is not EOF either; keep pulling until
            // one actually has data or the stream really ends.
            if (_pending.IsEmpty)
            {
                return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
        }

        var n = Math.Min(buffer.Length, _pending.Length);
        _pending.Span[..n].CopyTo(buffer.Span);
        _pending = _pending[n..];
        return n;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        do
        {
            var chunkLength = Math.Min(MaxWriteChunk, buffer.Length - offset);
            var chunk = buffer.Slice(offset, chunkLength);

            if (_pacer is not null)
            {
                await _pacer.AcquireAsync(chunk.Length, cancellationToken).ConfigureAwait(false);
            }

            await _writer.WriteAsync(new BytesMessage { Data = ByteString.CopyFrom(chunk.Span) }).ConfigureAwait(false);
            offset += chunkLength;
        }
        while (offset < buffer.Length);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Every write above is sent immediately (no client-side buffering), so there is nothing to flush.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("cider: a session bridge stream cannot seek");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("cider: a session bridge stream cannot be resized");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsyncCore().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Half-closes the request side (<c>CompleteAsync</c>), then drains whatever is left of the
    /// response side so the underlying <see cref="AsyncDuplexStreamingCall{TRequest,TResponse}"/>
    /// reaches a clean terminal state rather than being abandoned mid-stream.
    /// </summary>
    private async Task DisposeAsyncCore()
    {
        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or IOException or ObjectDisposedException)
        {
        }

        try
        {
            while (!_readerDone && await _reader.MoveNext(CancellationToken.None).ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
