using System.Text;
using Cider.AppleContainer;
using Cider.AppleContainer.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>Small adapter helpers that do not touch the CLI.</summary>
public class RuntimeHelperTests
{
    [Theory]
    [InlineData(null, "docker.io")]
    [InlineData("", "docker.io")]
    [InlineData("https://index.docker.io/v1/", "docker.io")]
    [InlineData("index.docker.io", "docker.io")]
    [InlineData("registry-1.docker.io", "docker.io")]
    [InlineData("ghcr.io", "ghcr.io")]
    [InlineData("https://ghcr.io", "ghcr.io")]
    [InlineData("http://localhost:5000/v2/", "localhost:5000")]
    public void Registry_addresses_are_reduced_to_a_host(string? input, string expected) =>
        Assert.Equal(expected, AppleContainerRuntime.NormalizeRegistry(input));

    [Theory]
    [InlineData("SIGKILL", 9)]
    [InlineData("KILL", 9)]
    [InlineData("TERM", 15)]
    [InlineData("SIGHUP", 1)]
    [InlineData("SIGUSR1", 30)]
    [InlineData("9", 9)]
    [InlineData("nonsense", 15)]
    public void Signal_names_map_to_macos_numbers(string signal, int expected) =>
        Assert.Equal(expected, CliProcess.SignalNumber(signal));

    // ---- PtyBootFilterStream ------------------------------------------------

    /// <summary>Apple's boot banner: hide cursor, spinner glyph, status text, show cursor.</summary>
    private const string Banner = "\u001b[?25l\u001b[2K\r\u2800 [1/6] Fetching image\r\u2801 Starting container [0s]\r\u001b[?25h";

    /// <summary>
    /// The first bytes of a real <c>docker exec -i -t &lt;name&gt; sh</c> session against busybox:
    /// prompt, a cursor-position query, two redraws, the echoed command and its output. None of it
    /// is boot noise, so the filter has to hand every single byte through unchanged, no matter how
    /// the pty happens to chunk them.
    /// </summary>
    private const string ExecSession =
        "/ # \u001b[6n\r/ # \u001b[J\r/ # \u001b[Jtty\r\r\n/dev/pts/0\r\r\n/ # stty size\r\r\n24 100\r\r\n/ # exit\r\r\n\u001b[?25l\u001b[?25h";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task An_exec_session_passes_through_the_boot_filter_untouched(int chunkSize)
    {
        var expected = Encoding.UTF8.GetBytes(ExecSession);

        var actual = await ReadAllAsync(new PtyBootFilterStream(new ChunkedStream(expected, chunkSize), NullLogger.Instance));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task The_boot_banner_is_stripped_and_the_guest_output_survives(int chunkSize)
    {
        var input = Encoding.UTF8.GetBytes(Banner + "/ # \u001b[6nhello\r\n");

        var actual = await ReadAllAsync(new PtyBootFilterStream(new ChunkedStream(input, chunkSize), NullLogger.Instance));

        Assert.Equal("/ # \u001b[6nhello\r\n", Encoding.UTF8.GetString(actual));
    }

    /// <summary>A first read that only looks like the hide-cursor prefix must not swallow a byte.</summary>
    [Theory]
    [InlineData("\u001b[?2X still guest output")]
    [InlineData("\u001b")]
    [InlineData("\u001b[")]
    [InlineData("/")]
    [InlineData("")]
    public async Task A_near_miss_of_the_hide_cursor_prefix_is_never_swallowed(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);

        var actual = await ReadAllAsync(new PtyBootFilterStream(new ChunkedStream(input, 1), NullLogger.Instance));

        Assert.Equal(text, Encoding.UTF8.GetString(actual));
    }

    /// <summary>Once the filter is out of the way it must stay out of the way, banner or not.</summary>
    [Fact]
    public async Task A_later_hide_cursor_sequence_is_not_treated_as_a_banner()
    {
        var input = Encoding.UTF8.GetBytes("guest\r\n\u001b[?25lstill guest\u001b[?25h and more\r\n");

        var actual = await ReadAllAsync(new PtyBootFilterStream(new ChunkedStream(input, 2), NullLogger.Instance));

        Assert.Equal(input, actual);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var _ = stream;
        var buffer = new byte[64];
        var collected = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (read <= 0)
            {
                return collected.ToArray();
            }

            collected.Write(buffer, 0, read);
        }
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read, like a pty master does.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
            if (n <= 0)
            {
                return 0;
            }

            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
