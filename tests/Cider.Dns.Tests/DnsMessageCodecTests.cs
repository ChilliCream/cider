using System.Net;
using Cider.Dns;
using Xunit;

namespace Cider.Dns.Tests;

/// <summary>
/// Codec round-trip tests against hand-verified wire-format byte fixtures (computed independently
/// of <see cref="DnsMessage"/> itself, see scratchpad/gen_fixtures.py used to derive them) — a real
/// dig-style A query for example.com with an EDNS0 OPT record, and a response using name compression
/// (a CNAME whose RDATA is a compression pointer, plus an A record whose NAME is a compression pointer).
/// </summary>
public class DnsMessageCodecTests
{
    // dig example.com A +edns=0 style query: header, question, one EDNS0 OPT additional record.
    private static readonly byte[] ExampleComAQueryWithEdns =
    {
        0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
        0x07, 0x65, 0x78, 0x61, 0x6D, 0x70, 0x6C, 0x65, 0x03, 0x63, 0x6F, 0x6D,
        0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x29, 0x10, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    // Response for "www.example.com A": Answer1 is a CNAME (name compressed -> question,
    // rdata compressed -> "example.com" inside the question name), Answer2 is an A record
    // (name compressed -> same "example.com" offset) for 192.0.2.1.
    private static readonly byte[] CompressedNameResponse =
    {
        0xAB, 0xCD, 0x81, 0x80, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
        0x03, 0x77, 0x77, 0x77, 0x07, 0x65, 0x78, 0x61, 0x6D, 0x70, 0x6C, 0x65,
        0x03, 0x63, 0x6F, 0x6D, 0x00, 0x00, 0x01, 0x00, 0x01, 0xC0, 0x0C, 0x00,
        0x05, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x02, 0xC0, 0x10, 0xC0,
        0x10, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x04, 0xC0,
        0x00, 0x02, 0x01,
    };

    [Fact]
    public void Parse_AQueryWithEdns0_DecodesHeaderQuestionAndOpt()
    {
        var msg = DnsMessage.Parse(ExampleComAQueryWithEdns);

        Assert.Equal(0x1234, msg.Id);
        Assert.False(msg.IsResponse);
        Assert.Equal(DnsOpcode.Query, msg.Opcode);
        Assert.False(msg.Authoritative);
        Assert.False(msg.Truncated);
        Assert.True(msg.RecursionDesired);
        Assert.False(msg.RecursionAvailable);
        Assert.Equal(DnsRcode.NoError, msg.Rcode);

        Assert.Single(msg.Questions);
        Assert.Equal("example.com", msg.Questions[0].Name);
        Assert.Equal(DnsRecordType.A, msg.Questions[0].Type);
        Assert.Equal(DnsClass.In, msg.Questions[0].Class);

        Assert.Empty(msg.Answers);
        Assert.Empty(msg.Additionals); // OPT is extracted into Edns, not left as a normal additional record

        Assert.NotNull(msg.Edns);
        Assert.Equal(4096, msg.Edns!.UdpPayloadSize);
        Assert.Equal(0, msg.Edns.ExtendedRcode);
        Assert.Equal(0, msg.Edns.Version);
        Assert.Equal(0, msg.Edns.Flags);
    }

    [Fact]
    public void Serialize_AQueryWithEdns0_RoundTrips()
    {
        var msg = DnsMessage.Parse(ExampleComAQueryWithEdns);

        var bytes = msg.Serialize(); // no length cap -> nothing should be dropped
        var reparsed = DnsMessage.Parse(bytes);

        Assert.Equal(msg.Id, reparsed.Id);
        Assert.Equal(msg.RecursionDesired, reparsed.RecursionDesired);
        Assert.Single(reparsed.Questions);
        Assert.Equal("example.com", reparsed.Questions[0].Name);
        Assert.Equal(DnsRecordType.A, reparsed.Questions[0].Type);
        Assert.NotNull(reparsed.Edns);
        Assert.Equal(4096, reparsed.Edns!.UdpPayloadSize);
    }

    [Fact]
    public void Parse_CompressedNameResponse_DecodesNameAndRdataPointers()
    {
        var msg = DnsMessage.Parse(CompressedNameResponse);

        Assert.Equal(0xABCD, msg.Id);
        Assert.True(msg.IsResponse);
        Assert.True(msg.RecursionDesired);
        Assert.True(msg.RecursionAvailable);
        Assert.Equal(DnsRcode.NoError, msg.Rcode);

        Assert.Single(msg.Questions);
        Assert.Equal("www.example.com", msg.Questions[0].Name);

        Assert.Equal(2, msg.Answers.Count);

        var cname = msg.Answers[0];
        Assert.Equal("www.example.com", cname.Name); // decompressed via pointer into the question
        Assert.Equal(DnsRecordType.Cname, cname.Type);
        Assert.Equal(300u, cname.Ttl);
        Assert.Equal("example.com", cname.AsDomainName()); // decompressed via pointer inside RDATA

        var a = msg.Answers[1];
        Assert.Equal("example.com", a.Name); // decompressed via a second, independent pointer
        Assert.Equal(DnsRecordType.A, a.Type);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), a.AsIPAddress());
    }

    [Fact]
    public void Serialize_CompressedNameResponse_RoundTrips()
    {
        var msg = DnsMessage.Parse(CompressedNameResponse);

        var bytes = msg.Serialize();
        var reparsed = DnsMessage.Parse(bytes);

        Assert.Equal(2, reparsed.Answers.Count);
        Assert.Equal("www.example.com", reparsed.Answers[0].Name);
        Assert.Equal("example.com", reparsed.Answers[0].AsDomainName());
        Assert.Equal("example.com", reparsed.Answers[1].Name);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), reparsed.Answers[1].AsIPAddress());
    }

    [Fact]
    public void Serialize_WithUdpLimit_TruncatesAndSetsTcFlag()
    {
        var msg = new DnsMessage { Id = 1, IsResponse = true, RecursionAvailable = true };
        msg.Questions.Add(new DnsQuestion("big.example", DnsRecordType.Txt));
        for (int i = 0; i < 40; i++)
        {
            msg.Answers.Add(DnsRecord.CreateTxt("big.example", new string('a', 200)));
        }

        var bytes = msg.Serialize(512);
        Assert.True(bytes.Length <= 512);

        var reparsed = DnsMessage.Parse(bytes);
        Assert.True(reparsed.Truncated);
        Assert.True(reparsed.Answers.Count < 40);
    }

    [Fact]
    public void Serialize_WithoutLimit_NeverTruncates()
    {
        var msg = new DnsMessage { Id = 1, IsResponse = true, RecursionAvailable = true };
        msg.Questions.Add(new DnsQuestion("big.example", DnsRecordType.Txt));
        for (int i = 0; i < 40; i++)
        {
            msg.Answers.Add(DnsRecord.CreateTxt("big.example", new string('a', 200)));
        }

        var bytes = msg.Serialize(); // TCP path: no cap
        var reparsed = DnsMessage.Parse(bytes);

        Assert.False(reparsed.Truncated);
        Assert.Equal(40, reparsed.Answers.Count);
    }

    [Fact]
    public void ARecord_RoundTripsAddressAndTtlThroughAMessage()
    {
        var msg = new DnsMessage { Id = 7, IsResponse = true, RecursionAvailable = true };
        msg.Questions.Add(new DnsQuestion("host.example", DnsRecordType.A));
        msg.Answers.Add(DnsRecord.CreateA("host.example", IPAddress.Parse("10.1.2.3"), ttl: 60));

        var reparsed = DnsMessage.Parse(msg.Serialize());

        var decoded = Assert.Single(reparsed.Answers);
        Assert.Equal("host.example", decoded.Name);
        Assert.Equal(DnsRecordType.A, decoded.Type);
        Assert.Equal(60u, decoded.Ttl);
        Assert.Equal(IPAddress.Parse("10.1.2.3"), decoded.AsIPAddress());
    }

    [Fact]
    public void AaaaAndTxtRecords_RoundTripThroughAMessage()
    {
        var msg = new DnsMessage { Id = 9, IsResponse = true, RecursionAvailable = true };
        msg.Questions.Add(new DnsQuestion("host.example", DnsRecordType.Aaaa));
        msg.Answers.Add(DnsRecord.CreateAaaa("host.example", IPAddress.Parse("2001:db8::1"), ttl: 60));
        msg.Answers.Add(DnsRecord.CreateTxt("host.example", "hello world"));

        var reparsed = DnsMessage.Parse(msg.Serialize());

        Assert.Equal(2, reparsed.Answers.Count);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), reparsed.Answers[0].AsIPAddress());
        Assert.Equal(new[] { "hello world" }, reparsed.Answers[1].AsTxtStrings());
    }
}
