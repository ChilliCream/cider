namespace Cider.Daemon.Tunnel;

/// <summary>
/// Joins two independent, unidirectional streams into one full-duplex <see cref="Stream"/>: reads
/// come from <paramref name="read"/>, writes go to <paramref name="write"/>. Used wherever a single
/// duplex stream is needed but the underlying transport only hands out two one-way halves — a child
/// process's stdio for <c>buildctl dial-stdio</c> (read its stdout, write its stdin) and a pair of
/// <see cref="System.IO.Pipelines.Pipe"/> halves in tests both need exactly this to become the one
/// <see cref="Stream"/> that <see cref="StreamHttp2Client"/> and <see cref="TunnelTransport"/> expect.
/// </summary>
public sealed class DuplexStream(Stream read, Stream write) : Stream
{
    private readonly Stream _read = read ?? throw new ArgumentNullException(nameof(read));
    private readonly Stream _write = write ?? throw new ArgumentNullException(nameof(write));

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("cider: a tunnel stream has no length");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("cider: a tunnel stream has no position");
        set => throw new NotSupportedException("cider: a tunnel stream has no position");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _read.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _read.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _read.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _write.Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _write.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _write.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Flush() => _write.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("cider: a tunnel stream cannot seek");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("cider: a tunnel stream cannot be resized");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _read.Dispose();
            _write.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _read.DisposeAsync().ConfigureAwait(false);
        await _write.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
