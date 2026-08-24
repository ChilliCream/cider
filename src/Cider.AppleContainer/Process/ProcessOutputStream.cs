using SysProcess = System.Diagnostics.Process;

namespace Cider.AppleContainer.Process;

/// <summary>
/// The stdout of a child process, exposed as a stream that kills the child when it is disposed.
/// <c>container logs -f</c> never terminates by itself (docs/apple-container-notes.md §3),
/// so the consumer's disposal is the only way out.
/// </summary>
internal sealed class ProcessOutputStream : Stream
{
    private readonly SysProcess _process;
    private readonly Stream _stdout;
    private bool _disposed;

    public ProcessOutputStream(SysProcess process)
    {
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _stdout.Read(buffer);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return await _stdout.ReadAsync(buffer, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Cli.ContainerCli.KillQuietly(_process);

            try
            {
                _stdout.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already closed.
            }

            _process.Dispose();
        }

        base.Dispose(disposing);
    }
}
