using Microsoft.Win32.SafeHandles;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Reads a plain text log file (task cider-ede.9: <c>containerLogs</c>'s <c>stdio.log</c> fd,
/// docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.10) with the three behaviours <c>docker logs</c>
/// needs and a bare read loop does not give for free:
/// <list type="bullet">
/// <item><description><b>tail</b> — seeks to the start of the last N lines up front, reading
/// backwards from the end in 64 KiB blocks (<see cref="FindTailStart"/>).</description></item>
/// <item><description><b>follow</b> — when the reader catches up to the current end of file, polls
/// for growth every <see cref="_pollInterval"/> (100 ms by default) instead of returning EOF.</description></item>
/// <item><description><b>truncation</b> — the runtime <c>O_TRUNC</c>s this file on every container
/// restart (docs/spikes/xpc/03-limitations-audit-1.3.md "Logs merged" row); a length shorter than the
/// current read position is treated as a restart, not corruption, and resets to offset 0.</description></item>
/// </list>
/// <see cref="Stop"/> lets a caller end an in-progress follow early even though the underlying file
/// never itself signals "no more data is coming" — <see cref="XpcContainerRuntime"/>'s stop-watcher
/// uses it once it observes the container is no longer running, since real dockerd's log driver gets
/// an EOF from the writer on process exit and Apple's merged file does not.
/// Uses <see cref="RandomAccess"/> (positioned pread/pwrite) directly against the fd rather than
/// wrapping it in a <see cref="FileStream"/>, since this stream manages its own read position
/// independent of what the OS file offset would otherwise track.
/// </summary>
internal sealed class FollowingFileStream : Stream
{
    private const int TailBlockSize = 64 * 1024;

    private readonly SafeFileHandle _handle;
    private readonly bool _follow;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stopFollowing = new();
    private long _position;
    private bool _disposed;

    /// <param name="handle">The already-duplicated fd (<c>xpc_array_dup_fd</c>'s result) — this
    /// instance owns it and closes it on <see cref="Dispose(bool)"/>.</param>
    /// <param name="follow">When <c>true</c>, a read that reaches the current end of file polls for
    /// growth instead of returning 0.</param>
    /// <param name="tail">When set and greater than 0, the initial read position starts at the first
    /// byte of the last <paramref name="tail"/> lines instead of the start of the file.</param>
    /// <param name="pollInterval">Overridable for tests; defaults to 100 ms (task fix direction).</param>
    public FollowingFileStream(SafeFileHandle handle, bool follow, int? tail, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(handle);

        _handle = handle;
        _follow = follow;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _position = tail is > 0 ? FindTailStart(handle, tail.Value) : 0;
    }

    /// <summary>
    /// Ends an in-progress follow the next time this stream would otherwise poll for growth. Safe to
    /// call from any thread, any number of times, before or after the stream is disposed.
    /// </summary>
    public void Stop()
    {
        try
        {
            _stopFollowing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override bool CanRead => true;

    public override bool CanWrite => false;

    public override bool CanSeek => false;

    public override long Length => RandomAccess.GetLength(_handle);

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long length;
            try
            {
                length = RandomAccess.GetLength(_handle);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return 0;
            }

            // The runtime O_TRUNCs stdio.log on every container start — a shrink means a restart,
            // not corruption; resync to the new file's start rather than seeking past its end forever.
            if (length < _position)
            {
                _position = 0;
                continue;
            }

            if (_position < length)
            {
                int read;
                try
                {
                    read = await RandomAccess.ReadAsync(_handle, buffer, _position, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return 0;
                }

                if (read > 0)
                {
                    _position += read;
                    return read;
                }

                // GetLength said there was more but the read came back empty — raced a concurrent
                // truncate between the two calls; loop and re-check rather than reporting a false EOF.
                continue;
            }

            if (!_follow || _stopFollowing.IsCancellationRequested)
            {
                return 0;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopFollowing.Token);
                await Task.Delay(_pollInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopFollowing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Stop() fired mid-wait: caught up to EOF and no longer following is exactly "the
                // stream has ended", not a cancellation the caller needs to see.
                return 0;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _stopFollowing.Cancel();
                _stopFollowing.Dispose();
                _handle.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Reads backwards from the end of the file in <see cref="TailBlockSize"/> blocks, counting
    /// <c>'\n'</c> bytes, to find the start of the last <paramref name="lines"/> lines — matching
    /// <c>tail -n N</c>. A file ending in <c>'\n'</c> needs <paramref name="lines"/> + 1 newlines
    /// counted from the end (the trailing one terminates the last line itself); a file whose last
    /// line has no trailing newline needs only <paramref name="lines"/> (that unterminated final line
    /// still counts as one, but contributes no newline of its own to count). Fewer newlines than that
    /// in the whole file → offset 0 (the whole file is the tail).
    /// </summary>
    private static long FindTailStart(SafeFileHandle handle, int lines)
    {
        long length;
        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return 0;
        }

        if (length == 0)
        {
            return 0;
        }

        Span<byte> lastByte = stackalloc byte[1];
        RandomAccess.Read(handle, lastByte, length - 1);
        var needed = lastByte[0] == (byte)'\n' ? lines + 1 : lines;

        long position = length;
        var buffer = new byte[TailBlockSize];
        var newlines = 0;

        while (position > 0)
        {
            var blockSize = (int)Math.Min(TailBlockSize, position);
            position -= blockSize;
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, blockSize), position);

            for (var i = read - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                newlines++;
                if (newlines >= needed)
                {
                    return position + i + 1;
                }
            }
        }

        return 0;
    }
}
