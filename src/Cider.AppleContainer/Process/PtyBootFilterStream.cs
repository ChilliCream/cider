using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Process;

/// <summary>
/// Wraps the pty master stream used for <c>container start -a</c>/<c>container exec -t</c> and
/// strips Apple's own boot-progress spinner before it reaches the client's terminal.
/// </summary>
/// <remarks>
/// On `container` 1.2.2, attaching a real TTY renders a braille spinner plus ANSI cursor-control
/// noise (hide cursor, redraw-in-place with <c>\r</c>/<c>ESC[K</c>, "Starting container [Ns]" or
/// numbered "[n/6] ..." status text) before the guest's own first byte — see
/// docs/apple-container-notes.md §5b. Apple always brackets that banner with the DECTCEM hide/show
/// cursor sequences (<c>ESC[?25l</c> ... <c>ESC[?25h</c>) as the very first bytes written to the
/// pty. Filtering only ever activates when the stream's first bytes are exactly the hide-cursor
/// sequence — nothing the guest process writes can arrive before the guest has finished booting,
/// so this can never consume real container output. Once the matching show-cursor sequence is
/// seen (or a byte budget is exhausted, or unrecognized text shows up), the filter permanently
/// switches to plain passthrough: no byte after the banner is ever inspected or altered again.
/// </remarks>
internal sealed class PtyBootFilterStream : Stream
{
    private const int MaxBufferedBytes = 16 * 1024;

    private static readonly byte[] HideCursorBytes = Encoding.ASCII.GetBytes("[?25l");
    private static readonly byte[] ShowCursorBytes = Encoding.ASCII.GetBytes("[?25h");
    private static readonly Regex StepPrefix = new(@"^\[\d+/\d+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Stream _inner;
    private readonly ILogger _logger;
    private State _state = State.AwaitingFirstBytes;
    private byte[] _pending = [];
    private int _pendingLength;
    private long _dropped;
    private bool _logged;

    private enum State { AwaitingFirstBytes, Filtering, Passthrough }

    public PtyBootFilterStream(Stream inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
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

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            if (_state == State.Passthrough)
            {
                if (_pendingLength > 0)
                {
                    return DrainPending(buffer);
                }

                return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            }

            // Still deciding, or actively filtering: pull more bytes and re-scan what we have.
            var scratch = new byte[4096];
            var read = await _inner.ReadAsync(scratch, ct).ConfigureAwait(false);
            if (read == 0)
            {
                // Clean end of stream mid-banner: nothing left to filter, flush what remains.
                _state = State.Passthrough;
                if (_pendingLength == 0)
                {
                    return 0;
                }

                continue;
            }

            AppendPending(scratch.AsSpan(0, read));
            Scan();
            LogTransition();

            if (_state == State.Passthrough && _pendingLength > 0)
            {
                continue; // drain the leftover through the passthrough branch above
            }

            // Everything buffered so far was recognized boot noise (or we're still waiting to
            // decide) — loop around and read more instead of returning an empty/zero read.
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.WriteAsync(buffer, offset, count, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---- filtering ------------------------------------------------------

    /// <summary>Logs, once, how many bytes the banner filter swallowed before it gave up filtering.</summary>
    private void LogTransition()
    {
        if (_logged || _state != State.Passthrough)
        {
            return;
        }

        _logged = true;
        _logger.LogDebug("pty boot filter switched to passthrough after dropping {Dropped} banner bytes", _dropped);
    }

    private void AppendPending(ReadOnlySpan<byte> data)
    {
        if (_pendingLength + data.Length > _pending.Length)
        {
            var next = new byte[Math.Max(_pending.Length * 2, _pendingLength + data.Length)];
            _pending.AsSpan(0, _pendingLength).CopyTo(next);
            _pending = next;
        }

        data.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += data.Length;
    }

    private int DrainPending(Memory<byte> buffer)
    {
        var n = Math.Min(_pendingLength, buffer.Length);
        _pending.AsSpan(0, n).CopyTo(buffer.Span);
        RemoveFromFront(n);
        return n;
    }

    private void RemoveFromFront(int count)
    {
        if (_state == State.Filtering || _state == State.AwaitingFirstBytes)
        {
            _dropped += Math.Min(count, _pendingLength);
        }

        if (count >= _pendingLength)
        {
            _pendingLength = 0;
            return;
        }

        Array.Copy(_pending, count, _pending, 0, _pendingLength - count);
        _pendingLength -= count;
    }

    /// <summary>Re-examines <see cref="_pending"/> from the front, stripping every complete piece
    /// of recognized boot noise in place. Leaves any incomplete trailing token buffered for the
    /// next read to complete.</summary>
    private void Scan()
    {
        if (_state == State.AwaitingFirstBytes)
        {
            if (_pendingLength < HideCursorBytes.Length)
            {
                // Not enough bytes yet to tell; bail only once what we have can no longer be a
                // prefix of the hide-cursor sequence.
                if (!HideCursorBytes.AsSpan(0, _pendingLength).SequenceEqual(_pending.AsSpan(0, _pendingLength)))
                {
                    _state = State.Passthrough;
                }

                return;
            }

            if (!_pending.AsSpan(0, HideCursorBytes.Length).SequenceEqual(HideCursorBytes))
            {
                _state = State.Passthrough;
                return;
            }

            RemoveFromFront(HideCursorBytes.Length);
            _state = State.Filtering;
        }

        if (_state != State.Filtering)
        {
            return;
        }

        while (true)
        {
            var span = _pending.AsSpan(0, _pendingLength);
            if (span.Length == 0)
            {
                return;
            }

            if (span.Length >= ShowCursorBytes.Length && span[..ShowCursorBytes.Length].SequenceEqual(ShowCursorBytes))
            {
                RemoveFromFront(ShowCursorBytes.Length);
                _state = State.Passthrough;
                return;
            }

            if (span[0] == 0x1B)
            {
                var len = TryConsumeCsi(span);
                if (len == 0)
                {
                    break; // incomplete escape sequence: wait for more bytes
                }

                RemoveFromFront(len);
                continue;
            }

            if (span[0] == (byte)'\r')
            {
                RemoveFromFront(1);
                continue;
            }

            // Braille spinner glyph, UTF-8 E2 A0..A3 80..BF == U+2800..U+28FF.
            if (span[0] == 0xE2)
            {
                if (span.Length < 3)
                {
                    break; // wait for the rest of the code point
                }

                if (span[1] is >= 0xA0 and <= 0xA3 && span[2] is >= 0x80 and <= 0xBF)
                {
                    RemoveFromFront(3);
                    continue;
                }

                // Some other UTF-8 sequence starting with E2: not banner noise.
                _state = State.Passthrough;
                return;
            }

            var textEnd = FindNextTokenStart(span);
            if (textEnd < 0)
            {
                if (_pendingLength < MaxBufferedBytes)
                {
                    break; // wait for the boundary that tells us where this text run ends
                }

                _state = State.Passthrough; // safety valve: nothing recognizable is coming
                return;
            }

            var text = Encoding.ASCII.GetString(span[..textEnd]);
            if (!IsBootStatusText(text))
            {
                _state = State.Passthrough; // unrecognized text: stop being clever, pass it through
                return;
            }

            RemoveFromFront(textEnd);
        }

        if (_pendingLength > MaxBufferedBytes)
        {
            _state = State.Passthrough;
        }
    }

    private static bool IsBootStatusText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0
            || trimmed.Contains("Starting container", StringComparison.Ordinal)
            || StepPrefix.IsMatch(trimmed);
    }

    private static int FindNextTokenStart(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is 0x1B or (byte)'\r' or 0xE2)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Consumes one full CSI sequence (<c>ESC [ params intermediates final</c>) from the
    /// front of <paramref name="span"/>; returns its byte length, or 0 if it is not complete yet.</summary>
    private static int TryConsumeCsi(ReadOnlySpan<byte> span)
    {
        if (span.Length < 2)
        {
            return 0;
        }

        if (span[1] != (byte)'[')
        {
            // A bare/unknown ESC: this banner never emits one, but consume just the ESC so a
            // stray byte can never wedge the scan.
            return 1;
        }

        for (var i = 2; i < span.Length; i++)
        {
            if (span[i] is >= 0x40 and <= 0x7E)
            {
                return i + 1;
            }
        }

        return 0;
    }
}
