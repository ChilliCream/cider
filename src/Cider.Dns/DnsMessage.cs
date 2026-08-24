namespace Cider.Dns;

/// <summary>
/// A DNS message: header, questions, and answer/authority/additional resource-record sections
/// (RFC 1035 §4). EDNS0 OPT pseudo-records (RFC 6891) are parsed out of the additional section
/// into <see cref="Edns"/> rather than appearing in <see cref="Additionals"/>.
/// </summary>
public sealed class DnsMessage
{
    private const int DefaultUdpSize = 512;

    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public DnsOpcode Opcode { get; set; } = DnsOpcode.Query;
    public bool Authoritative { get; set; }
    public bool Truncated { get; set; }
    public bool RecursionDesired { get; set; }
    public bool RecursionAvailable { get; set; }
    public DnsRcode Rcode { get; set; } = DnsRcode.NoError;

    public List<DnsQuestion> Questions { get; } = new();
    public List<DnsRecord> Answers { get; } = new();
    public List<DnsRecord> Authorities { get; } = new();
    public List<DnsRecord> Additionals { get; } = new();

    /// <summary>Non-null when the message carried an EDNS0 OPT pseudo-record.</summary>
    public EdnsOptions? Edns { get; set; }

    /// <summary>Parses a complete DNS message from wire-format bytes. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static DnsMessage Parse(byte[] data) => ParseCore(data);

    /// <summary>Parses a complete DNS message from wire-format bytes. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static DnsMessage Parse(ReadOnlySpan<byte> data) => ParseCore(data.ToArray());

    private static DnsMessage ParseCore(byte[] data)
    {
        if (data.Length < 12) throw new FormatException("DNS message shorter than the 12-byte header.");

        ushort id = DnsWire.ReadUInt16(data, 0);
        ushort flags = DnsWire.ReadUInt16(data, 2);
        ushort qdCount = DnsWire.ReadUInt16(data, 4);
        ushort anCount = DnsWire.ReadUInt16(data, 6);
        ushort nsCount = DnsWire.ReadUInt16(data, 8);
        ushort arCount = DnsWire.ReadUInt16(data, 10);

        var msg = new DnsMessage
        {
            Id = id,
            IsResponse = (flags & 0x8000) != 0,
            Opcode = (DnsOpcode)((flags >> 11) & 0xF),
            Authoritative = (flags & 0x0400) != 0,
            Truncated = (flags & 0x0200) != 0,
            RecursionDesired = (flags & 0x0100) != 0,
            RecursionAvailable = (flags & 0x0080) != 0,
            Rcode = (DnsRcode)(flags & 0xF),
        };

        int pos = 12;

        for (int i = 0; i < qdCount; i++)
        {
            var name = DnsWire.ReadName(data, ref pos);
            var type = (DnsRecordType)DnsWire.ReadUInt16(data, pos); pos += 2;
            var cls = (DnsClass)DnsWire.ReadUInt16(data, pos); pos += 2;
            msg.Questions.Add(new DnsQuestion(name, type, cls));
        }

        for (int i = 0; i < anCount; i++) msg.Answers.Add(DnsRecord.Read(data, ref pos));
        for (int i = 0; i < nsCount; i++) msg.Authorities.Add(DnsRecord.Read(data, ref pos));

        for (int i = 0; i < arCount; i++)
        {
            var record = DnsRecord.Read(data, ref pos);
            if (record.Type == DnsRecordType.Opt)
            {
                msg.Edns = new EdnsOptions(
                    UdpPayloadSize: (ushort)record.Class,
                    ExtendedRcode: (byte)(record.Ttl >> 24),
                    Version: (byte)(record.Ttl >> 16),
                    Flags: (ushort)(record.Ttl & 0xFFFF));
            }
            else
            {
                msg.Additionals.Add(record);
            }
        }

        return msg;
    }

    /// <summary>
    /// Serializes this message to wire format. When <paramref name="maxLength"/> is null (the TCP path),
    /// the full message is written regardless of size. When given (the UDP path), records are added until
    /// the next one would exceed <paramref name="maxLength"/>; if any section had to be cut short, the TC
    /// (truncated) flag is set and remaining records/sections are omitted.
    /// </summary>
    public byte[] Serialize(int? maxLength = null)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[12], 0, 12); // header placeholder, patched below

        foreach (var q in Questions)
        {
            DnsWire.WriteName(ms, q.Name);
            DnsWire.WriteUInt16(ms, (ushort)q.Type);
            DnsWire.WriteUInt16(ms, (ushort)q.Class);
        }

        bool truncated = false;
        int answersWritten = WriteSection(ms, Answers, maxLength, ref truncated);
        int authoritiesWritten = truncated ? 0 : WriteSection(ms, Authorities, maxLength, ref truncated);
        int additionalsWritten = truncated ? 0 : WriteSection(ms, Additionals, maxLength, ref truncated);

        if (!truncated && Edns is { } edns)
        {
            var optBytes = EncodeOpt(edns);
            if (maxLength is int max && ms.Length + optBytes.Length > max)
            {
                truncated = true;
            }
            else
            {
                ms.Write(optBytes, 0, optBytes.Length);
                additionalsWritten += 1;
            }
        }

        var buffer = ms.ToArray();

        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        flags |= (ushort)(((byte)Opcode & 0xF) << 11);
        if (Authoritative) flags |= 0x0400;
        if (truncated) flags |= 0x0200;
        if (RecursionDesired) flags |= 0x0100;
        if (RecursionAvailable) flags |= 0x0080;
        flags |= (ushort)((byte)Rcode & 0xF);

        WriteUInt16At(buffer, 0, Id);
        WriteUInt16At(buffer, 2, flags);
        WriteUInt16At(buffer, 4, (ushort)Questions.Count);
        WriteUInt16At(buffer, 6, (ushort)answersWritten);
        WriteUInt16At(buffer, 8, (ushort)authoritiesWritten);
        WriteUInt16At(buffer, 10, (ushort)additionalsWritten);

        return buffer;
    }

    private static int WriteSection(MemoryStream ms, List<DnsRecord> records, int? maxLength, ref bool truncated)
    {
        int written = 0;
        foreach (var record in records)
        {
            var encoded = record.EncodeRecord();
            if (maxLength is int max && ms.Length + encoded.Length > max)
            {
                truncated = true;
                return written;
            }
            ms.Write(encoded, 0, encoded.Length);
            written++;
        }
        return written;
    }

    private static byte[] EncodeOpt(EdnsOptions edns)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0); // root name
        DnsWire.WriteUInt16(ms, (ushort)DnsRecordType.Opt);
        DnsWire.WriteUInt16(ms, (ushort)edns.UdpPayloadSize);
        uint ttl = ((uint)edns.ExtendedRcode << 24) | ((uint)edns.Version << 16) | edns.Flags;
        DnsWire.WriteUInt32(ms, ttl);
        DnsWire.WriteUInt16(ms, 0); // RDLENGTH: no options carried
        return ms.ToArray();
    }

    private static void WriteUInt16At(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }
}
