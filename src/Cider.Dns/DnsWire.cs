using System.Text;

namespace Cider.Dns;

/// <summary>
/// Low-level helpers for reading/writing the DNS wire format (RFC 1035 §4.1):
/// big-endian primitives and domain names (including compression-pointer decoding).
/// Internal — consumers use <see cref="DnsMessage"/> and <see cref="DnsRecord"/>.
/// </summary>
internal static class DnsWire
{
    private const int MaxLabelLength = 63;
    private const int MaxPointerHops = 128;

    public static ushort ReadUInt16(byte[] data, int pos)
    {
        if (pos + 2 > data.Length) throw new FormatException("DNS message truncated reading a 16-bit field.");
        return (ushort)((data[pos] << 8) | data[pos + 1]);
    }

    public static uint ReadUInt32(byte[] data, int pos)
    {
        if (pos + 4 > data.Length) throw new FormatException("DNS message truncated reading a 32-bit field.");
        return ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];
    }

    public static void WriteUInt16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    public static void WriteUInt32(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    /// <summary>
    /// Reads a domain name starting at <paramref name="pos"/>, following compression pointers
    /// as needed. On return, <paramref name="pos"/> points just past the name as it appeared in
    /// the stream at the original position (i.e. past the pointer, not past the jumped-to data).
    /// </summary>
    public static string ReadName(byte[] data, ref int pos)
    {
        var labels = new List<string>();
        int cursor = pos;
        bool jumped = false;
        int hops = 0;

        while (true)
        {
            if (cursor >= data.Length) throw new FormatException("DNS name runs past end of message.");
            byte lengthByte = data[cursor];

            if (lengthByte == 0)
            {
                cursor += 1;
                if (!jumped) pos = cursor;
                break;
            }

            if ((lengthByte & 0xC0) == 0xC0)
            {
                if (++hops > MaxPointerHops) throw new FormatException("DNS name compression pointer loop detected.");
                if (cursor + 1 >= data.Length) throw new FormatException("DNS name compression pointer truncated.");
                int target = ((lengthByte & 0x3F) << 8) | data[cursor + 1];
                if (!jumped)
                {
                    pos = cursor + 2;
                    jumped = true;
                }
                if (target >= cursor) throw new FormatException("DNS name compression pointer does not point backward.");
                cursor = target;
                continue;
            }

            if ((lengthByte & 0xC0) != 0) throw new FormatException("Reserved DNS label length bits set.");
            if (lengthByte > MaxLabelLength) throw new FormatException("DNS label exceeds 63 bytes.");
            if (cursor + 1 + lengthByte > data.Length) throw new FormatException("DNS label runs past end of message.");

            labels.Add(Encoding.ASCII.GetString(data, cursor + 1, lengthByte));
            cursor += 1 + lengthByte;

            if (++hops > MaxPointerHops * 4) throw new FormatException("DNS name has too many labels.");
        }

        return labels.Count == 0 ? string.Empty : string.Join('.', labels);
    }

    /// <summary>Writes a domain name as plain (uncompressed) labels terminated by a zero byte.</summary>
    public static void WriteName(Stream s, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            s.WriteByte(0);
            return;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0) continue; // tolerate a trailing dot
            if (label.Length > MaxLabelLength) throw new ArgumentException($"DNS label '{label}' exceeds 63 bytes.", nameof(name));
            s.WriteByte((byte)label.Length);
            var bytes = Encoding.ASCII.GetBytes(label);
            s.Write(bytes, 0, bytes.Length);
        }

        s.WriteByte(0);
    }

    /// <summary>Encodes a name to a standalone, self-contained (pointer-free) byte sequence.</summary>
    public static byte[] EncodeName(string name)
    {
        using var ms = new MemoryStream();
        WriteName(ms, name);
        return ms.ToArray();
    }

    /// <summary>Decodes a standalone, self-contained (pointer-free) name previously produced by <see cref="EncodeName"/>.</summary>
    public static string DecodeName(byte[] data)
    {
        int pos = 0;
        return ReadName(data, ref pos);
    }
}
