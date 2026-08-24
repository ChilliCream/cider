using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cider.Dns;

/// <summary>
/// A DNS resource record. <see cref="RData"/> holds the already-decoded, self-contained RDATA bytes:
/// for name-bearing types (CNAME/PTR/NS) any compression pointers seen on the wire have already been
/// expanded, so RData never itself contains a pointer. Unrecognized types are carried opaquely.
/// </summary>
public sealed class DnsRecord
{
    public string Name { get; }
    public DnsRecordType Type { get; }
    public DnsClass Class { get; }
    public uint Ttl { get; }
    public byte[] RData { get; }

    public DnsRecord(string name, DnsRecordType type, DnsClass @class, uint ttl, byte[] rdata)
    {
        Name = name;
        Type = type;
        Class = @class;
        Ttl = ttl;
        RData = rdata;
    }

    public static DnsRecord CreateA(string name, IPAddress address, uint ttl = 300)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Address must be IPv4 for an A record.", nameof(address));
        return new DnsRecord(name, DnsRecordType.A, DnsClass.In, ttl, address.GetAddressBytes());
    }

    public static DnsRecord CreateAaaa(string name, IPAddress address, uint ttl = 300)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Address must be IPv6 for an AAAA record.", nameof(address));
        return new DnsRecord(name, DnsRecordType.Aaaa, DnsClass.In, ttl, address.GetAddressBytes());
    }

    public static DnsRecord CreateCname(string name, string target, uint ttl = 300) =>
        new(name, DnsRecordType.Cname, DnsClass.In, ttl, DnsWire.EncodeName(target));

    public static DnsRecord CreatePtr(string name, string target, uint ttl = 300) =>
        new(name, DnsRecordType.Ptr, DnsClass.In, ttl, DnsWire.EncodeName(target));

    public static DnsRecord CreateTxt(string name, string text, uint ttl = 300) =>
        new(name, DnsRecordType.Txt, DnsClass.In, ttl, EncodeTxtChunk(text));

    public static DnsRecord CreateTxt(string name, IReadOnlyList<string> strings, uint ttl = 300)
    {
        using var ms = new MemoryStream();
        foreach (var s in strings)
        {
            var chunk = EncodeTxtChunk(s);
            ms.Write(chunk, 0, chunk.Length);
        }
        return new DnsRecord(name, DnsRecordType.Txt, DnsClass.In, ttl, ms.ToArray());
    }

    /// <summary>For A/AAAA records, the address; otherwise null.</summary>
    public IPAddress? AsIPAddress() =>
        Type is DnsRecordType.A or DnsRecordType.Aaaa ? new IPAddress(RData) : null;

    /// <summary>For CNAME/PTR/NS records, the target domain name; otherwise null.</summary>
    public string? AsDomainName() =>
        Type is DnsRecordType.Cname or DnsRecordType.Ptr or DnsRecordType.Ns ? DnsWire.DecodeName(RData) : null;

    /// <summary>For TXT records, the decoded character-strings; otherwise empty.</summary>
    public IReadOnlyList<string> AsTxtStrings()
    {
        if (Type != DnsRecordType.Txt) return Array.Empty<string>();

        var list = new List<string>();
        int pos = 0;
        while (pos < RData.Length)
        {
            int len = RData[pos];
            pos++;
            if (pos + len > RData.Length) break;
            list.Add(Encoding.UTF8.GetString(RData, pos, len));
            pos += len;
        }
        return list;
    }

    private static byte[] EncodeTxtChunk(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 255)
            throw new ArgumentException("A single TXT character-string may be at most 255 bytes; split into multiple strings.", nameof(text));

        var result = new byte[bytes.Length + 1];
        result[0] = (byte)bytes.Length;
        Array.Copy(bytes, 0, result, 1, bytes.Length);
        return result;
    }

    internal byte[] EncodeRecord()
    {
        using var ms = new MemoryStream();
        DnsWire.WriteName(ms, Name);
        DnsWire.WriteUInt16(ms, (ushort)Type);
        DnsWire.WriteUInt16(ms, (ushort)Class);
        DnsWire.WriteUInt32(ms, Ttl);
        DnsWire.WriteUInt16(ms, (ushort)RData.Length);
        ms.Write(RData, 0, RData.Length);
        return ms.ToArray();
    }

    internal static DnsRecord Read(byte[] data, ref int pos)
    {
        var name = DnsWire.ReadName(data, ref pos);
        var type = (DnsRecordType)DnsWire.ReadUInt16(data, pos); pos += 2;
        var cls = (DnsClass)DnsWire.ReadUInt16(data, pos); pos += 2;
        var ttl = DnsWire.ReadUInt32(data, pos); pos += 4;
        var rdLength = DnsWire.ReadUInt16(data, pos); pos += 2;
        if (pos + rdLength > data.Length) throw new FormatException("DNS record RDATA runs past end of message.");

        byte[] rdata;
        if (type is DnsRecordType.Cname or DnsRecordType.Ptr or DnsRecordType.Ns)
        {
            // Name-bearing RDATA may itself use compression pointers into the whole message;
            // decode against the full buffer, then re-encode standalone (pointer-free) for storage.
            int namePos = pos;
            var target = DnsWire.ReadName(data, ref namePos);
            rdata = DnsWire.EncodeName(target);
        }
        else
        {
            rdata = new byte[rdLength];
            Array.Copy(data, pos, rdata, 0, rdLength);
        }

        pos += rdLength;
        return new DnsRecord(name, type, cls, ttl, rdata);
    }
}
