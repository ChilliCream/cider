using System.Text;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Wraps a freshly dialed, single-purpose HTTP/2 duplex connection so the one HEADERS frame written
/// on it is replaced, byte for byte, with a hand-encoded HPACK header block carrying
/// <see cref="_fields"/> instead of whatever <c>SocketsHttpHandler</c> would have written.
/// <para>
/// Why this exists (cider-ger.16): a real BuildKit session (session/manager.go) learns which
/// callback methods a session supports from <b>every</b> <c>X-Docker-Expose-Session-Grpc-Method</c>
/// header line the dialer sends — buildx's own dialer sends one line per method
/// (session/session.go:108). <see cref="SessionBridge"/> needs the same thing: it re-advertises the
/// CLI's methods plus its own (<c>FileSend/DiffCopy</c>, <c>Health/Check</c>) when it dials buildkitd
/// on the CLI's behalf. But <c>System.Net.Http.Headers.HttpHeaders</c> — what <c>Grpc.Net.Client</c>
/// and every other managed .NET HTTP/2 client sits on — silently joins every value added under one
/// header name into a single comma-separated wire line before it ever reaches HPACK encoding
/// (verified directly against a real Kestrel HTTP/2 listener: <c>Metadata</c> with the same key added
/// N times over <c>Grpc.Net.Client</c> arrives server-side as ONE header with N comma-joined values,
/// not N header lines — this is <c>HttpHeaders.GetHeaderString</c>'s unconditional
/// <c>string.Join(descriptor.Separator, multiValue)</c>, with no public opt-out). buildkitd's Go
/// server never re-splits that string (session/manager.go's <c>opts[headerSessionMethod]</c> loop,
/// and grpc-go's own header decode, both take exactly the values HPACK handed them), so the daemon's
/// bridged session ends up advertising one useless, unmatched string instead of N legitimate methods
/// — <c>session.Caller.Supports(...)</c> then fails for every one of them, including
/// <c>FileSync/DiffCopy</c>, and buildkitd answers <c>failed to read dockerfile: no local sources
/// enabled</c> before a build gets anywhere near the Dockerfile's contents.
/// </para>
/// <para>
/// There is no supported way to stop <c>System.Net.Http</c> from combining a header outside its own
/// small hardcoded exemption list (<c>Set-Cookie</c> and the like), so this bypasses it for exactly
/// the one frame that matters: the connection this wraps is dialed solely for one
/// <c>Control/Session</c> call (<see cref="SessionBridge.OpenAsync"/> gets a dedicated connection
/// precisely so this holds), so "the first HEADERS-type frame written on it" is unambiguous — no
/// HPACK *decoding* of what <c>SocketsHttpHandler</c> would have sent is needed, only enough framing
/// awareness to find that frame's boundaries and substitute its payload. Every other byte — the h2c
/// client preface, SETTINGS, WINDOW_UPDATE, and every DATA frame the actual <c>BytesMessage</c>
/// exchange produces for the lifetime of the session — passes through untouched, so flow control,
/// the response side's HPACK decoding, and everything else stays exactly what
/// <c>SocketsHttpHandler</c>/<c>Grpc.Net.Client</c> already gets right.
/// </para>
/// </summary>
internal sealed class LiteralHeadersRewriteStream : Stream
{
    /// <summary>The fixed 24-byte h2c client connection preface (RFC 7540 §3.5) — passed through untouched.</summary>
    private const int PrefaceLength = 24;

    private const byte HeadersFrameType = 0x1;
    private const byte ContinuationFrameType = 0x9;
    private const byte EndHeadersFlag = 0x4;

    /// <summary>
    /// RFC 7540 §4.2's default <c>SETTINGS_MAX_FRAME_SIZE</c> — the largest single frame payload a
    /// peer must accept without having first been told (via its own SETTINGS) that a larger one is
    /// welcome. This stream never negotiates that up, so <see cref="_headerBlock"/> must fit inside
    /// one frame or <see cref="BuildHeadersFrame"/> would have to split it across HEADERS +
    /// CONTINUATION — unimplemented here (see the class doc comment); go over this and construction
    /// fails loudly instead of silently emitting a frame the peer is entitled to reject with
    /// FRAME_SIZE_ERROR.
    /// </summary>
    private const int MaxFrameSize = 16384;

    private readonly Stream _inner;
    private readonly byte[] _headerBlock;
    private readonly List<byte> _pending = [];
    private int _prefaceRemaining = PrefaceLength;
    private bool _headersRewritten;

    /// <summary>
    /// <paramref name="fields"/> is the complete header block for the one request this connection will
    /// ever carry — pseudo-headers (<c>:method</c>, <c>:scheme</c>, <c>:authority</c>, <c>:path</c>)
    /// first, in order, exactly as HTTP/2 requires (RFC 7540 §8.1.2.1), then every regular header,
    /// including every repeated <c>x-docker-expose-session-grpc-method</c> entry.
    /// </summary>
    public LiteralHeadersRewriteStream(Stream inner, IReadOnlyList<(string Name, string Value)> fields)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(fields);
        _headerBlock = EncodeLiteralFields(fields);
        if (_headerBlock.Length > MaxFrameSize)
        {
            throw new InvalidOperationException(
                $"cider: session dial header block is {_headerBlock.Length} bytes, over the " +
                $"{MaxFrameSize}-byte default SETTINGS_MAX_FRAME_SIZE -- LiteralHeadersRewriteStream " +
                "cannot split a header block across HEADERS + CONTINUATION frames");
        }
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("cider: a rewritten session dial stream has no length");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("cider: a rewritten session dial stream has no position");
        set => throw new NotSupportedException("cider: a rewritten session dial stream has no position");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Once the one frame this exists to rewrite has been handled, every later write (every DATA
        // frame the real BytesMessage exchange produces, for the rest of this connection's life) is
        // forwarded straight through with no buffering or inspection at all.
        if (_headersRewritten)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        var span = buffer;
        if (_prefaceRemaining > 0)
        {
            var take = Math.Min(_prefaceRemaining, span.Length);
            await _inner.WriteAsync(span[..take], cancellationToken).ConfigureAwait(false);
            _prefaceRemaining -= take;
            span = span[take..];
        }

        if (!span.IsEmpty)
        {
            _pending.AddRange(span.Span.ToArray());
        }

        await DrainPendingFramesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes as many complete HTTP/2 frames as <see cref="_pending"/> currently holds (a 9-byte
    /// frame header's 24-bit length field always says exactly how many payload bytes follow, so frame
    /// boundaries are unambiguous regardless of how <c>SocketsHttpHandler</c> chose to chunk its own
    /// writes) — forwarding every one of them untouched except the first of type HEADERS, which is
    /// replaced by a same-stream-id frame built from <see cref="_headerBlock"/>. Once that happens,
    /// whatever remains buffered is flushed raw and <see cref="_headersRewritten"/> latches so every
    /// later write skips this buffering path entirely.
    /// </summary>
    private async Task DrainPendingFramesAsync(CancellationToken cancellationToken)
    {
        while (!_headersRewritten)
        {
            if (_pending.Count < 9)
            {
                return;
            }

            var length = (_pending[0] << 16) | (_pending[1] << 8) | _pending[2];
            var total = 9 + length;
            if (_pending.Count < total)
            {
                return;
            }

            var type = _pending[3];
            if (type == HeadersFrameType)
            {
                var flags = _pending[4];
                if ((flags & EndHeadersFlag) == 0)
                {
                    // SocketsHttpHandler split its own header block across HEADERS + one or more
                    // CONTINUATION frames -- this stream only ever substitutes the one HEADERS frame,
                    // so latching _headersRewritten now and flushing the rest raw would forward the
                    // original (un-substituted) CONTINUATION payload right behind our replacement
                    // HEADERS frame, corrupting the connection's HPACK state for both sides silently.
                    // Fail loudly instead.
                    throw new InvalidOperationException(
                        "cider: session dial's HEADERS frame did not set END_HEADERS -- a CONTINUATION " +
                        "frame would follow, which LiteralHeadersRewriteStream does not support");
                }

                var streamId = ((_pending[5] & 0x7F) << 24) | (_pending[6] << 16) | (_pending[7] << 8) | _pending[8];
                await _inner.WriteAsync(BuildHeadersFrame(streamId), cancellationToken).ConfigureAwait(false);
                _headersRewritten = true;
            }
            else if (type == ContinuationFrameType)
            {
                // A CONTINUATION frame arriving before any HEADERS frame has been seen would mean
                // this connection's first request already needed one, which BuildHeadersFrame's own
                // MaxFrameSize guard should have prevented for the header block this stream itself
                // writes -- but SocketsHttpHandler is the one framing the original request, so guard
                // this too rather than forwarding a frame type this class was never designed to handle.
                throw new InvalidOperationException(
                    "cider: unexpected CONTINUATION frame before this connection's HEADERS frame was rewritten");
            }
            else
            {
                await _inner.WriteAsync(_pending.GetRange(0, total).ToArray(), cancellationToken).ConfigureAwait(false);
            }

            _pending.RemoveRange(0, total);
        }

        if (_pending.Count > 0)
        {
            await _inner.WriteAsync(_pending.ToArray(), cancellationToken).ConfigureAwait(false);
            _pending.Clear();
        }
    }

    private byte[] BuildHeadersFrame(int streamId)
    {
        var frame = new byte[9 + _headerBlock.Length];
        frame[0] = (byte)(_headerBlock.Length >> 16);
        frame[1] = (byte)(_headerBlock.Length >> 8);
        frame[2] = (byte)_headerBlock.Length;
        frame[3] = HeadersFrameType;
        frame[4] = EndHeadersFlag; // END_HEADERS; neither END_STREAM (a request body follows) nor PADDED/PRIORITY.
        frame[5] = (byte)((streamId >> 24) & 0x7F); // top bit reserved, must be 0.
        frame[6] = (byte)(streamId >> 16);
        frame[7] = (byte)(streamId >> 8);
        frame[8] = (byte)streamId;
        Buffer.BlockCopy(_headerBlock, 0, frame, 9, _headerBlock.Length);
        return frame;
    }

    /// <summary>
    /// Encodes every field as HPACK "Literal Header Field without Indexing — New Name" (RFC 7541
    /// §6.2.2, indicator byte <c>0x00</c>): correct for pseudo-headers as much as regular ones (a
    /// decoder must accept a literal-new-name representation for any header), needs no dynamic table
    /// bookkeeping, and — since this repeats the one field buildkitd's session manager needs many
    /// copies of — never accidentally re-indexes a later value onto an earlier one's table slot the
    /// way an indexing representation could.
    /// <para>
    /// Internal rather than private solely so <c>LiteralHeadersRewriteStreamTests</c> can call it
    /// directly instead of reaching it through reflection (<c>InternalsVisibleTo("Cider.Tests")</c>,
    /// see <c>Install/AssemblyInfo.cs</c>) — it carries no public contract of its own.
    /// </para>
    /// </summary>
    internal static byte[] EncodeLiteralFields(IReadOnlyList<(string Name, string Value)> fields)
    {
        using var block = new MemoryStream();
        foreach (var (name, value) in fields)
        {
            block.WriteByte(0x00);
            WriteHpackString(block, name);
            WriteHpackString(block, value);
        }

        return block.ToArray();
    }

    /// <summary>An HPACK string literal: an un-Huffman-coded (H=0) length-prefixed ASCII byte string.</summary>
    private static void WriteHpackString(Stream destination, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteHpackInteger(destination, bytes.Length);
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// HPACK's variable-length integer encoding (RFC 7541 §5.1) with a 7-bit prefix — the eighth bit
    /// of a string-literal length octet is the Huffman flag, always 0 here, so 7 bits of prefix are
    /// all that is available before the continuation-byte form kicks in.
    /// </summary>
    private static void WriteHpackInteger(Stream destination, int value)
    {
        const int prefixMax = (1 << 7) - 1;
        if (value < prefixMax)
        {
            destination.WriteByte((byte)value);
            return;
        }

        destination.WriteByte((byte)prefixMax);
        value -= prefixMax;
        while (value >= 128)
        {
            destination.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        destination.WriteByte((byte)value);
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("cider: a rewritten session dial stream cannot seek");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("cider: a rewritten session dial stream cannot be resized");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
