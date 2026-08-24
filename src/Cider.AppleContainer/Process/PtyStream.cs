using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Process;

/// <summary>
/// A pty master stream. Once the last slave fd closes, Darwin either returns EOF or fails the read
/// with <c>EIO</c>, so those failures are surfaced as a clean end of stream — which is why
/// <see cref="CliProcess"/> keeps a slave fd of its own until the child is gone and the master has
/// been drained: an end of stream must mean "the child is finished", never "it has not opened its
/// pty yet". Every read, write and end of stream is logged with its byte count, so a truncated
/// session can be pinned to a layer from the daemon log alone.
/// </summary>
internal sealed class PtyStream : Stream
{
    private readonly Stream _inner;
    private readonly ILogger _logger;
    private readonly string _tty;
    private long _bytesRead;
    private long _bytesWritten;
    private bool _ended;

    public PtyStream(Stream inner, ILogger logger, string tty)
    {
        _inner = inner;
        _logger = logger;
        _tty = tty;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        try
        {
            _inner.Flush();
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            // The pty is gone; nothing left to flush.
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return Count(_inner.Read(buffer));
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            return EndOfStream(ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return Count(await _inner.ReadAsync(buffer, ct));
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            return EndOfStream(ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _inner.Write(buffer);
            Wrote(buffer.Length);
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            Dropped(buffer.Length, ex);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            await _inner.WriteAsync(buffer, ct);
            Wrote(buffer.Length);
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            Dropped(buffer.Length, ex);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            catch (Exception ex) when (IsClosed(ex))
            {
                // Already closed.
            }
        }

        base.Dispose(disposing);
    }

    private static bool IsClosed(Exception ex) => ex is IOException or ObjectDisposedException;

    private int Count(int read)
    {
        if (read > 0)
        {
            _bytesRead += read;
            _logger.LogTrace("pty {Tty} read {Count} bytes ({Total} total)", _tty, read, _bytesRead);
        }
        else
        {
            _ended = true;
            _logger.LogDebug("pty {Tty} reached end of stream after {Total} bytes", _tty, _bytesRead);
        }

        return read;
    }

    private int EndOfStream(Exception ex)
    {
        if (!_ended)
        {
            _ended = true;
            _logger.LogDebug("pty {Tty} read ended after {Total} bytes: {Error}", _tty, _bytesRead, ex.Message);
        }

        return 0;
    }

    private void Wrote(int count)
    {
        _bytesWritten += count;
        _logger.LogTrace("pty {Tty} wrote {Count} bytes ({Total} total)", _tty, count, _bytesWritten);
    }

    private void Dropped(int count, Exception ex) =>
        _logger.LogDebug("pty {Tty} dropped {Count} stdin bytes: {Error}", _tty, count, ex.Message);
}
