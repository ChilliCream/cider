using System.Reflection;
using System.Text;
using Cider.Daemon.BuildKit;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// Dedicated coverage for <see cref="LiteralHeadersRewriteStream"/>'s hand-rolled HTTP/2 framing and
/// HPACK encoding (cider-ger.16): nothing exercised this class directly before, despite it being the
/// entire fix for buildkitd's "no local sources enabled" -- these tests pin the exact HPACK bytes it
/// produces (including the multi-octet integer continuation branch), prove frame substitution survives
/// arbitrary chunking of the underlying writes, and prove the two failure modes it cannot handle
/// (a header block needing CONTINUATION, either because <see cref="LiteralHeadersRewriteStream"/>'s own
/// block is too big or because the frame it is replacing already needed one) throw loudly instead of
/// corrupting the connection.
/// </summary>
public sealed class LiteralHeadersRewriteStreamTests
{
    private const byte HeadersFrameType = 0x1;
    private const byte SettingsFrameType = 0x4;
    private const byte DataFrameType = 0x0;
    private const byte ContinuationFrameType = 0x9;
    private const byte EndHeadersFlag = 0x4;

    private static readonly byte[] Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    // ---- Encoding ---------------------------------------------------------

    [Fact]
    public void EncodeLiteralFields_produces_exact_expected_HPACK_bytes_for_repeated_keys()
    {
        var fields = new List<(string Name, string Value)>
        {
            (":method", "POST"),
            (":path", "/grpc.Control/Session"),
            ("x-docker-expose-session-grpc-method", "moby.filesync.v1.filesync/diffcopy"),
            ("x-docker-expose-session-grpc-method", "moby.filesync.v1.filesend/diffcopy"),
            ("x-docker-expose-session-grpc-method", "grpc.health.v1.health/check"),
        };

        var actual = InvokeEncodeLiteralFields(fields);
        var expected = HandEncodeLiteralNewName(fields);

        Assert.Equal(expected, actual);

        // Every field is "Literal Header Field without Indexing -- New Name" (indicator byte 0x00),
        // never an indexed or indexing representation -- decoding it back below is what actually
        // proves this, but pin the raw indicator bytes too since that is the property that makes
        // repeated x-docker-expose-session-grpc-method entries independent lines rather than table
        // slots colliding.
        var decoded = DecodeLiteralNewNameFields(actual);
        Assert.Equal(fields, decoded);
    }

    [Fact]
    public void EncodeLiteralFields_round_trips_through_a_hand_written_HPACK_decoder()
    {
        var fields = new List<(string Name, string Value)>
        {
            (":method", "POST"),
            (":scheme", "http"),
            (":authority", "buildkit"),
            (":path", "/moby.buildkit.v1.Control/Session"),
            ("content-type", "application/grpc"),
            ("te", "trailers"),
            ("x-docker-expose-session-uuid", "cli-session-1"),
            ("x-docker-expose-session-sharedkey", "shared-key"),
            ("x-docker-expose-session-grpc-method", "moby.filesync.v1.filesync/diffcopy"),
            ("x-docker-expose-session-grpc-method", "moby.filesync.v1.filesend/diffcopy"),
            ("x-docker-expose-session-grpc-method", "grpc.health.v1.health/check"),
        };

        var block = InvokeEncodeLiteralFields(fields);
        var decoded = DecodeLiteralNewNameFields(block);

        Assert.Equal(fields, decoded);
    }

    [Fact]
    public void WriteHpackInteger_multi_octet_continuation_branch_round_trips_a_long_value()
    {
        // 127 is prefixMax for a 7-bit-prefixed string length -- anything >= that forces the
        // continuation-byte form (RFC 7541 5.1), which every field length in production use today
        // (method names, uuids, the shared key) is short enough to never reach. Force it here.
        var longValue = new string('a', 400);
        var fields = new List<(string Name, string Value)> { ("x-docker-expose-session-sharedkey", longValue) };

        var block = InvokeEncodeLiteralFields(fields);
        var decoded = DecodeLiteralNewNameFields(block);

        Assert.Equal(fields, decoded);

        // Pin the continuation-byte shape directly too: prefix byte 0x7F (127, H=0), then the base-128
        // continuation bytes for (400 - 127) = 273 = 0x02*128 + 0x11 -> low byte has the continuation
        // bit set, terminal byte does not.
        // Layout: [0x00][name-len][name bytes][0x7F][cont1][cont2]['a'*400]
        var nameLen = "x-docker-expose-session-sharedkey".Length;
        var valueLenFieldStart = 1 + 1 + nameLen; // indicator + name-length byte + name bytes
        Assert.Equal(0x7F, block[valueLenFieldStart]);
        var remaining = longValue.Length - 127; // 273
        Assert.Equal((byte)((remaining % 128) + 128), block[valueLenFieldStart + 1]);
        Assert.Equal((byte)(remaining / 128), block[valueLenFieldStart + 2]);
        Assert.Equal(0, block[valueLenFieldStart + 2] & 0x80); // terminal continuation byte, MSB clear
    }

    // ---- Framing ------------------------------------------------------------

    [Fact]
    public async Task DrainPendingFramesAsync_substitutes_only_the_first_HEADERS_frame_byte_by_byte()
    {
        await AssertFramingSurvivesChunking(chunkSize: 1);
    }

    [Fact]
    public async Task DrainPendingFramesAsync_substitutes_correctly_when_split_mid_frame_header()
    {
        // Every chunk boundary lands inside some frame's 9-byte header across the whole sequence.
        await AssertFramingSurvivesChunking(customSplitPoints: [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3]);
    }

    [Fact]
    public async Task DrainPendingFramesAsync_substitutes_correctly_when_split_mid_payload()
    {
        await AssertFramingSurvivesChunking(customSplitPoints: [17, 41, 9, 200]);
    }

    [Fact]
    public async Task DrainPendingFramesAsync_forwards_the_whole_sequence_when_written_as_one_chunk()
    {
        await AssertFramingSurvivesChunking(customSplitPoints: [int.MaxValue]);
    }

    private static async Task AssertFramingSurvivesChunking(int? chunkSize = null, int[]? customSplitPoints = null)
    {
        var fields = new List<(string Name, string Value)>
        {
            (":method", "POST"),
            (":path", "/moby.buildkit.v1.Control/Session"),
            ("x-docker-expose-session-grpc-method", "moby.filesync.v1.filesync/diffcopy"),
        };
        var expectedHeaderBlock = InvokeEncodeLiteralFields(fields);

        // Whatever SocketsHttpHandler would have written for this one request: a real client HEADERS
        // frame (arbitrary payload -- it must be replaced verbatim, so its actual content is
        // irrelevant), followed by a SETTINGS frame and a DATA frame that must pass through untouched.
        var originalHeadersPayload = Encoding.ASCII.GetBytes("this-payload-must-never-reach-the-wire");
        var originalHeadersFrame = BuildFrame(HeadersFrameType, EndHeadersFlag, 1, originalHeadersPayload);
        var settingsFrame = BuildFrame(SettingsFrameType, 0, 0, [0, 0, 0, 0, 0, 0]);
        var dataFrame = BuildFrame(DataFrameType, 0, 1, Encoding.ASCII.GetBytes("bytesmessage-payload"));

        var fullInput = Preface.Concat(settingsFrame).Concat(originalHeadersFrame).Concat(dataFrame).ToArray();

        var expectedSubstitutedHeadersFrame = BuildFrame(HeadersFrameType, EndHeadersFlag, 1, expectedHeaderBlock);
        var expectedOutput = Preface.Concat(settingsFrame).Concat(expectedSubstitutedHeadersFrame).Concat(dataFrame).ToArray();

        var inner = new MemoryStream();
        await using var stream = new LiteralHeadersRewriteStream(inner, fields);

        foreach (var chunk in Chunk(fullInput, chunkSize, customSplitPoints))
        {
            await stream.WriteAsync(chunk, CancellationToken.None);
        }

        Assert.Equal(expectedOutput, inner.ToArray());
    }

    private static IEnumerable<byte[]> Chunk(byte[] data, int? chunkSize, int[]? customSplitPoints)
    {
        if (chunkSize is { } size)
        {
            for (var i = 0; i < data.Length; i += size)
            {
                yield return data[i..Math.Min(i + size, data.Length)];
            }

            yield break;
        }

        var pos = 0;
        foreach (var splitPoint in customSplitPoints!)
        {
            if (pos >= data.Length)
            {
                yield break;
            }

            var take = Math.Min(splitPoint, data.Length - pos);
            yield return data[pos..(pos + take)];
            pos += take;
        }

        if (pos < data.Length)
        {
            yield return data[pos..];
        }
    }

    // ---- Guards ---------------------------------------------------------

    [Fact]
    public void Constructor_throws_when_header_block_exceeds_default_SETTINGS_MAX_FRAME_SIZE()
    {
        var fields = new List<(string Name, string Value)> { ("x-huge", new string('a', 20_000)) };

        var ex = Assert.Throws<InvalidOperationException>(() => new LiteralHeadersRewriteStream(new MemoryStream(), fields));
        Assert.Contains("16384", ex.Message);
    }

    [Fact]
    public async Task DrainPendingFramesAsync_throws_when_original_HEADERS_frame_lacks_END_HEADERS()
    {
        var fields = new List<(string Name, string Value)> { (":method", "POST") };
        var originalHeadersFrame = BuildFrame(HeadersFrameType, 0, 1, Encoding.ASCII.GetBytes("split-across-continuation"));
        var input = Preface.Concat(originalHeadersFrame).ToArray();

        await using var stream = new LiteralHeadersRewriteStream(new MemoryStream(), fields);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(input, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DrainPendingFramesAsync_throws_on_a_CONTINUATION_frame_before_rewrite()
    {
        var fields = new List<(string Name, string Value)> { (":method", "POST") };
        var continuationFrame = BuildFrame(ContinuationFrameType, EndHeadersFlag, 1, Encoding.ASCII.GetBytes("cont"));
        var input = Preface.Concat(continuationFrame).ToArray();

        await using var stream = new LiteralHeadersRewriteStream(new MemoryStream(), fields);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.WriteAsync(input, CancellationToken.None).AsTask());
    }

    // ---- Helpers ----------------------------------------------------------

    private static byte[] BuildFrame(byte type, byte flags, int streamId, byte[] payload)
    {
        var frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        frame[5] = (byte)((streamId >> 24) & 0x7F);
        frame[6] = (byte)(streamId >> 16);
        frame[7] = (byte)(streamId >> 8);
        frame[8] = (byte)streamId;
        Buffer.BlockCopy(payload, 0, frame, 9, payload.Length);
        return frame;
    }

    private static byte[] InvokeEncodeLiteralFields(IReadOnlyList<(string Name, string Value)> fields)
    {
        var method = typeof(LiteralHeadersRewriteStream).GetMethod(
            "EncodeLiteralFields", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("cider: LiteralHeadersRewriteStream.EncodeLiteralFields not found -- test needs updating");
        return (byte[])method.Invoke(null, [fields])!;
    }

    /// <summary>
    /// An independent (from production code) hand encoder for exactly the representation
    /// <c>EncodeLiteralFields</c> is documented to produce -- RFC 7541 §6.2.2 "Literal Header Field
    /// without Indexing -- New Name" for every field, un-Huffman-coded string literals throughout.
    /// </summary>
    private static byte[] HandEncodeLiteralNewName(IReadOnlyList<(string Name, string Value)> fields)
    {
        using var buffer = new MemoryStream();
        foreach (var (name, value) in fields)
        {
            buffer.WriteByte(0x00);
            HandWriteHpackString(buffer, name);
            HandWriteHpackString(buffer, value);
        }

        return buffer.ToArray();
    }

    private static void HandWriteHpackString(Stream destination, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        HandWriteHpackInteger(destination, bytes.Length);
        destination.Write(bytes, 0, bytes.Length);
    }

    private static void HandWriteHpackInteger(Stream destination, int value)
    {
        const int prefixMax = 127;
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

    /// <summary>
    /// A minimal HPACK decoder for exactly the "Literal Header Field without Indexing -- New Name"
    /// representation this class emits (RFC 7541 §6.2.2 / §5.1 / §5.2): no dynamic table, no Huffman
    /// (this class never sets H=1), just enough to prove the bytes <c>EncodeLiteralFields</c> writes
    /// decode back to the exact field list that went in -- the faithful stand-in the class doc comment
    /// notes real Kestrel HPACK decoding already confirms at the integration level (SessionBridgeTests).
    /// </summary>
    private static List<(string Name, string Value)> DecodeLiteralNewNameFields(byte[] block)
    {
        var result = new List<(string, string)>();
        var pos = 0;
        while (pos < block.Length)
        {
            var indicator = block[pos];
            Assert.Equal(0x00, indicator); // "Literal Header Field without Indexing -- New Name"
            pos++;

            var name = ReadHpackString(block, ref pos);
            var value = ReadHpackString(block, ref pos);
            result.Add((name, value));
        }

        return result;
    }

    private static string ReadHpackString(byte[] block, ref int pos)
    {
        var first = block[pos];
        Assert.Equal(0, first & 0x80); // H (Huffman) flag must be clear -- this class never Huffman-codes.
        var length = ReadHpackInteger(block, ref pos, prefixBits: 7);
        var text = Encoding.ASCII.GetString(block, pos, length);
        pos += length;
        return text;
    }

    private static int ReadHpackInteger(byte[] block, ref int pos, int prefixBits)
    {
        var prefixMax = (1 << prefixBits) - 1;
        var value = block[pos] & prefixMax;
        pos++;
        if (value < prefixMax)
        {
            return value;
        }

        var m = 0;
        byte b;
        do
        {
            b = block[pos++];
            value += (b & 0x7F) << m;
            m += 7;
        }
        while ((b & 0x80) != 0);

        return value;
    }
}
