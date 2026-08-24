using System.Text;
using Cider.Core.DockerApi.Streams;
using Xunit;

namespace Cider.Tests.DockerApi.Streams;

public class MultiplexedFrameTests
{
    [Fact]
    public void Encodes_stdout_with_an_eight_byte_big_endian_header()
    {
        var frame = MultiplexedFrame.Encode(StdStream.Stdout, "hi"u8);

        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 2, (byte)'h', (byte)'i'], frame);
    }

    [Fact]
    public void Encodes_stderr_and_lengths_above_255()
    {
        var payload = new byte[300];
        var frame = MultiplexedFrame.Encode(StdStream.Stderr, payload);

        Assert.Equal(MultiplexedFrame.HeaderSize + 300, frame.Length);
        Assert.Equal((byte)2, frame[0]);
        Assert.Equal((byte)0, frame[1]);
        Assert.Equal((byte)0, frame[2]);
        Assert.Equal((byte)0, frame[3]);
        Assert.Equal((byte)0, frame[4]);
        Assert.Equal((byte)0, frame[5]);
        Assert.Equal((byte)1, frame[6]);   // 300 = 0x0000012C
        Assert.Equal((byte)0x2C, frame[7]);
    }

    [Fact]
    public void Encodes_an_empty_payload()
    {
        var frame = MultiplexedFrame.Encode(StdStream.Stdout, ReadOnlySpan<byte>.Empty);

        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 0], frame);
    }

    [Fact]
    public void Stream_ids_match_docker()
    {
        Assert.Equal(0, (int)StdStream.Stdin);
        Assert.Equal(1, (int)StdStream.Stdout);
        Assert.Equal(2, (int)StdStream.Stderr);
    }

    [Fact]
    public async Task WriteAsync_emits_header_then_payload()
    {
        using var buffer = new MemoryStream();

        await MultiplexedFrame.WriteAsync(buffer, StdStream.Stdout, Encoding.UTF8.GetBytes("hello\n"));
        await MultiplexedFrame.WriteAsync(buffer, StdStream.Stderr, Encoding.UTF8.GetBytes("err"));

        var bytes = buffer.ToArray();
        Assert.Equal(8 + 6 + 8 + 3, bytes.Length);
        Assert.Equal((byte)1, bytes[0]);
        Assert.Equal((byte)6, bytes[7]);
        Assert.Equal("hello\n", Encoding.UTF8.GetString(bytes, 8, 6));
        Assert.Equal((byte)2, bytes[14]);
        Assert.Equal((byte)3, bytes[21]);
        Assert.Equal("err", Encoding.UTF8.GetString(bytes, 22, 3));
    }
}
