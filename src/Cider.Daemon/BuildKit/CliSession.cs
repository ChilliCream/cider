using System.Net.Http;
using Cider.Daemon.Tunnel;
using Grpc.Net.Client;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// One CLI session dialed through <c>POST /session</c>. Unlike <c>/grpc</c> — where BuildKit is the
/// gRPC client and the daemon is the server — the roles are reversed on this leg: the CLI runs an
/// HTTP/2 gRPC *server* on the hijacked connection (<c>(&amp;http2.Server{}).ServeConn</c>, buildkit
/// v0.26.2 session/grpc.go:24-31) and the daemon dials it as a client, exactly the way
/// <see cref="StreamHttp2Client"/> is used for <c>buildctl dial-stdio</c>. The session server idles
/// until the daemon calls one of its <see cref="Methods"/> (filesync, auth, secrets, ssh-forward,
/// upload) while building.
/// </summary>
public sealed class CliSession : IAsyncDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <summary>
    /// Wraps <paramref name="stream"/> (the hijacked connection, or a synthetic duplex stream for
    /// <see cref="CliSessionRegistry.RegisterFromStream"/>'s <c>Control.Session</c> bidi path) so
    /// that a read fault or EOF completes <see cref="Closed"/>, then dials an HTTP/2 client over it.
    /// </summary>
    public CliSession(string id, string? sharedKey, IEnumerable<string> methods, Stream stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(stream);

        Id = id;
        SharedKey = sharedKey ?? "";
        Methods = new HashSet<string>(methods.Select(static m => m.ToLowerInvariant()), StringComparer.Ordinal);

        _closable = new ClosableDuplexStream(stream, Close);
        (Channel, Invoker, _handler) = StreamHttp2Client.Create(_closable, "session");
    }

    private readonly ClosableDuplexStream _closable;

    /// <summary>The session id from <c>X-Docker-Expose-Session-Uuid</c>.</summary>
    public string Id { get; }

    /// <summary>The shared key from <c>X-Docker-Expose-Session-Sharedkey</c>.</summary>
    public string SharedKey { get; }

    /// <summary>Lower-cased, deduplicated <c>X-Docker-Expose-Session-Grpc-Method</c> values.</summary>
    public IReadOnlySet<string> Methods { get; }

    /// <summary>The gRPC channel dialed over the session stream — the daemon calls out through this.</summary>
    public GrpcChannel Channel { get; }

    /// <summary>The raw invoker backing <see cref="Channel"/>, for forwarding that bypasses generated stubs.</summary>
    public HttpMessageInvoker Invoker { get; }

    /// <summary>Completes when the underlying connection dies (read/write fault or EOF) or the session is unregistered.</summary>
    public Task Closed => _closedTcs.Task;

    /// <summary>Marks this session closed. Idempotent.</summary>
    internal void Close() => _closedTcs.TrySetResult();

    /// <summary>
    /// See <see cref="ForwardTarget.HeaderRewrite"/>: queues <paramref name="fields"/> to replace the
    /// next HEADERS frame this session's <see cref="Invoker"/> writes on the wire, working around
    /// <c>System.Net.Http.Headers.HttpHeaders</c> silently comma-joining repeated header values into
    /// one line (cider-ger.16, cider-ger.18). Awaits until any earlier call's own HEADERS frame has
    /// finished writing (the gate this returns releases) before queuing, so two forwards racing on the
    /// same session never see each other's fields.
    /// </summary>
    internal async Task<IAsyncDisposable> BeginHeaderRewriteAsync(
        IReadOnlyList<(string Name, string Value)> fields, CancellationToken cancellationToken) =>
        await _closable.BeginHeaderRewriteAsync(fields, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await Closed.ConfigureAwait(false);
            return;
        }

        Close();

        try
        {
            Invoker.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await Channel.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        try
        {
            _handler.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Decorates a duplex <see cref="Stream"/> so that the first EOF or fault observed on it — the
    /// only signal available once <see cref="StreamHttp2Client"/>'s HTTP/2 client machinery owns
    /// every read on the stream — invokes <paramref name="onClosed"/> exactly once, and so that every
    /// outgoing HEADERS frame can be replaced with a literal-encoded substitute queued via
    /// <see cref="BeginHeaderRewriteAsync"/> (see <see cref="ForwardTarget.HeaderRewrite"/>'s doc
    /// comment for why: unlike <see cref="LiteralHeadersRewriteStream"/>'s one-shot dial, this
    /// connection is shared across every call <see cref="GrpcForwarder"/> forwards through this
    /// session for the connection's whole life, so the substitution has to repeat per call rather than
    /// latch after the first frame).
    /// </summary>
    private sealed class ClosableDuplexStream(Stream inner, Action onClosed) : Stream
    {
        private const byte HeadersFrameType = 0x1;
        private const byte ContinuationFrameType = 0x9;
        private const byte EndHeadersFlag = 0x4;

        /// <summary>RFC 7540 §4.2's default <c>SETTINGS_MAX_FRAME_SIZE</c> -- see <see cref="LiteralHeadersRewriteStream"/>'s own doc comment.</summary>
        private const int MaxFrameSize = 16384;

        /// <summary>The fixed 24-byte h2c client connection preface (RFC 7540 §3.5) — passed through untouched.</summary>
        private const int PrefaceLength = 24;

        private int _signaled;
        private readonly SemaphoreSlim _headerGate = new(1, 1);
        private readonly List<byte> _pendingWrite = [];
        private IReadOnlyList<(string Name, string Value)>? _nextHeaderFields;
        private int _prefaceRemaining = PrefaceLength;

        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException("cider: a session stream has no length");

        public override long Position
        {
            get => throw new NotSupportedException("cider: a session stream has no position");
            set => throw new NotSupportedException("cider: a session stream has no position");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                var read = inner.Read(buffer, offset, count);
                // A zero-length request legitimately returns 0 without the stream being at EOF (the
                // .NET Stream contract: nothing was asked for, so nothing was read) -- count > 0 is
                // what makes a 0 actually mean EOF. System.Net.Http's own HTTP/2 client issues exactly
                // such a zero-length probe read as the very first read of Http2Connection.SetupAsync,
                // and treating it as EOF here used to fire Signal() (and so complete CliSession.Closed)
                // before the real client preface / SETTINGS exchange ever got a chance to run --
                // HijackInterceptor.SessionAsync would then see Closed and tear the transport pipes
                // down out from under the H2 handshake that was about to use them (cider-ger.18).
                if (read == 0 && count > 0)
                {
                    Signal();
                }

                return read;
            }
            catch
            {
                Signal();
                throw;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                // See the Read(byte[], int, int) override above: a 0-length request trivially reads 0
                // without that meaning EOF.
                if (read == 0 && !buffer.IsEmpty)
                {
                    Signal();
                }

                return read;
            }
            catch
            {
                Signal();
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await RewriteAndForwardAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Signal();
                throw;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>
        /// See <see cref="CliSession.BeginHeaderRewriteAsync"/>. Acquires <see cref="_headerGate"/> so
        /// only one caller's fields are ever pending at a time -- HTTP/2 itself already forbids a
        /// HEADERS/CONTINUATION sequence for one stream interleaving with another's (RFC 7540 §4.3),
        /// so once this stream has actually written the substitute frame the connection is free for
        /// the next caller regardless of how long that first call's body/response takes afterwards.
        /// </summary>
        public async Task<IAsyncDisposable> BeginHeaderRewriteAsync(
            IReadOnlyList<(string Name, string Value)> fields, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(fields);
            await _headerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _nextHeaderFields = fields;
            return new HeaderRewriteScope(_headerGate);
        }

        /// <summary>
        /// Buffers <paramref name="buffer"/> behind whatever <see cref="_pendingWrite"/> already holds
        /// and forwards every complete frame it can find -- the h2c client preface first, then frame
        /// by frame (a 9-byte frame header's 24-bit length field always says exactly how many payload
        /// bytes follow, so frame boundaries are unambiguous regardless of how
        /// <c>SocketsHttpHandler</c> chose to chunk its own writes). Every frame passes through
        /// untouched except HEADERS, which is replaced by a same-stream-id, same-flags frame built
        /// from whatever <see cref="_nextHeaderFields"/> the current <see cref="BeginHeaderRewriteAsync"/>
        /// caller queued.
        /// </summary>
        private async Task RewriteAndForwardAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var span = buffer;
            if (_prefaceRemaining > 0)
            {
                var take = Math.Min(_prefaceRemaining, span.Length);
                await inner.WriteAsync(span[..take], cancellationToken).ConfigureAwait(false);
                _prefaceRemaining -= take;
                span = span[take..];
            }

            if (!span.IsEmpty)
            {
                _pendingWrite.AddRange(span.Span.ToArray());
            }

            while (true)
            {
                if (_pendingWrite.Count < 9)
                {
                    return;
                }

                var length = (_pendingWrite[0] << 16) | (_pendingWrite[1] << 8) | _pendingWrite[2];
                var total = 9 + length;
                if (_pendingWrite.Count < total)
                {
                    return;
                }

                var type = _pendingWrite[3];
                if (type == HeadersFrameType)
                {
                    var flags = _pendingWrite[4];
                    if ((flags & EndHeadersFlag) == 0)
                    {
                        // See LiteralHeadersRewriteStream's own guard: a HEADERS frame that continues
                        // into one or more CONTINUATION frames is not something this class rewrites.
                        throw new InvalidOperationException(
                            "cider: a session forward's HEADERS frame did not set END_HEADERS -- a " +
                            "CONTINUATION frame would follow, which this rewrite does not support");
                    }

                    var streamId = ((_pendingWrite[5] & 0x7F) << 24) | (_pendingWrite[6] << 16) | (_pendingWrite[7] << 8) | _pendingWrite[8];
                    var fields = _nextHeaderFields ?? throw new InvalidOperationException(
                        "cider: a session forward wrote a HEADERS frame without first calling BeginHeaderRewriteAsync");
                    _nextHeaderFields = null;
                    await inner.WriteAsync(BuildHeadersFrame(streamId, flags, fields), cancellationToken).ConfigureAwait(false);
                }
                else if (type == ContinuationFrameType)
                {
                    throw new InvalidOperationException(
                        "cider: unexpected CONTINUATION frame on a session forward's HEADERS frame");
                }
                else
                {
                    await inner.WriteAsync(_pendingWrite.GetRange(0, total).ToArray(), cancellationToken).ConfigureAwait(false);
                }

                _pendingWrite.RemoveRange(0, total);
            }
        }

        private static byte[] BuildHeadersFrame(int streamId, byte flags, IReadOnlyList<(string Name, string Value)> fields)
        {
            var headerBlock = LiteralHeadersRewriteStream.EncodeLiteralFields(fields);
            if (headerBlock.Length > MaxFrameSize)
            {
                throw new InvalidOperationException(
                    $"cider: session forward header block is {headerBlock.Length} bytes, over the " +
                    $"{MaxFrameSize}-byte default SETTINGS_MAX_FRAME_SIZE -- cannot split a header " +
                    "block across HEADERS + CONTINUATION frames");
            }

            var frame = new byte[9 + headerBlock.Length];
            frame[0] = (byte)(headerBlock.Length >> 16);
            frame[1] = (byte)(headerBlock.Length >> 8);
            frame[2] = (byte)headerBlock.Length;
            frame[3] = HeadersFrameType;
            frame[4] = flags; // preserves whichever of END_STREAM/PRIORITY/PADDED the original frame set.
            frame[5] = (byte)((streamId >> 24) & 0x7F); // top bit reserved, must be 0.
            frame[6] = (byte)(streamId >> 16);
            frame[7] = (byte)(streamId >> 8);
            frame[8] = (byte)streamId;
            Buffer.BlockCopy(headerBlock, 0, frame, 9, headerBlock.Length);
            return frame;
        }

        private sealed class HeaderRewriteScope(SemaphoreSlim gate) : IAsyncDisposable
        {
            private int _released;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    gate.Release();
                }

                return ValueTask.CompletedTask;
            }
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("cider: a session stream cannot seek");

        public override void SetLength(long value) =>
            throw new NotSupportedException("cider: a session stream cannot be resized");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Signal();
                inner.Dispose();
                _headerGate.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Signal();
            await inner.DisposeAsync().ConfigureAwait(false);
            _headerGate.Dispose();
            GC.SuppressFinalize(this);
        }

        private void Signal()
        {
            if (Interlocked.Exchange(ref _signaled, 1) == 0)
            {
                onClosed();
            }
        }
    }
}
