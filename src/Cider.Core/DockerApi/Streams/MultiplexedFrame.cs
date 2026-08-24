using System.Buffers.Binary;

namespace Cider.Core.DockerApi.Streams;

/// <summary>The stream ids Docker uses in the first byte of a multiplexed frame header.</summary>
public enum StdStream
{
    Stdin = 0,
    Stdout = 1,
    Stderr = 2,
}

/// <summary>
/// Docker's stdcopy framing for non-TTY attach/logs/exec output: an 8-byte header
/// <c>[fd, 0, 0, 0, len(big-endian uint32)]</c> followed by the payload.
/// </summary>
public static class MultiplexedFrame
{
    /// <summary>Size of the frame header in bytes.</summary>
    public const int HeaderSize = 8;

    /// <summary>Writes the 8-byte frame header for <paramref name="payloadLength"/> into <paramref name="destination"/>.</summary>
    public static void WriteHeader(Span<byte> destination, StdStream stream, int payloadLength)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"header buffer must be at least {HeaderSize} bytes", nameof(destination));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        destination[0] = (byte)stream;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..HeaderSize], (uint)payloadLength);
    }

    /// <summary>Returns header + payload as one buffer.</summary>
    public static byte[] Encode(StdStream stream, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        WriteHeader(frame, stream, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Writes one frame to <paramref name="destination"/> and flushes it.</summary>
    public static async Task WriteAsync(Stream destination, StdStream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var header = new byte[HeaderSize];
        WriteHeader(header, stream, payload.Length);
        await destination.WriteAsync(header, ct);
        if (!payload.IsEmpty)
        {
            await destination.WriteAsync(payload, ct);
        }

        await destination.FlushAsync(ct);
    }
}
