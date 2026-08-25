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

        var closable = new ClosableDuplexStream(stream, Close);
        (Channel, Invoker, _handler) = StreamHttp2Client.Create(closable, "session");
    }

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
    /// every read on the stream — invokes <paramref name="onClosed"/> exactly once.
    /// </summary>
    private sealed class ClosableDuplexStream(Stream inner, Action onClosed) : Stream
    {
        private int _signaled;

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
                if (read == 0)
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
                if (read == 0)
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

        public override void Write(byte[] buffer, int offset, int count)
        {
            try
            {
                inner.Write(buffer, offset, count);
            }
            catch
            {
                Signal();
                throw;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Signal();
                throw;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Signal();
            await inner.DisposeAsync().ConfigureAwait(false);
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
