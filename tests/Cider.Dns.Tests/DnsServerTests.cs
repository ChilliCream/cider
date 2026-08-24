using System.Net;
using System.Net.Sockets;
using Cider.Dns;
using Xunit;

namespace Cider.Dns.Tests;

/// <summary>
/// Integration tests running real <see cref="DnsServer"/> instances on 127.0.0.1:0 (an OS-assigned
/// random port, read back from <see cref="DnsServer.LocalEndPoint"/>). Nothing here ever talks to
/// the real internet: "upstream" is a second in-process <see cref="DnsServer"/>.
/// </summary>
public class DnsServerTests
{
    private static DnsMessage BuildQuery(string name, DnsRecordType type, bool edns = false)
    {
        var msg = new DnsMessage
        {
            Id = (ushort)Random.Shared.Next(1, ushort.MaxValue),
            RecursionDesired = true,
        };
        msg.Questions.Add(new DnsQuestion(name, type));
        if (edns) msg.Edns = new EdnsOptions(4096);
        return msg;
    }

    private static async Task<DnsServer> StartServerAsync(IDnsResolver resolver, IReadOnlyList<IPEndPoint>? upstreams = null)
    {
        var server = new DnsServer(new IPEndPoint(IPAddress.Loopback, 0), resolver, upstreams ?? Array.Empty<IPEndPoint>());
        await server.StartAsync(CancellationToken.None);
        return server;
    }

    [Fact]
    public async Task StaticResolver_AnswersKnownNameOverUdp()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("web.adtest.local", DnsRecordType.A));

        Assert.Equal(DnsRcode.NoError, response.Rcode);
        var record = Assert.Single(response.Answers);
        Assert.Equal(DnsRecordType.A, record.Type);
        Assert.Equal(IPAddress.Parse("192.168.64.10"), record.AsIPAddress());

        await server.StopAsync();
    }

    [Fact]
    public async Task StaticResolver_AaaaForAOnlyName_ReturnsNoDataNotNxDomain()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("web.adtest.local", DnsRecordType.Aaaa));

        // Known name, wrong family: NOERROR/0 answers. NXDOMAIN here would break musl/Go dual-stack lookups.
        Assert.Equal(DnsRcode.NoError, response.Rcode);
        Assert.Empty(response.Answers);

        await server.StopAsync();
    }

    [Fact]
    public async Task UnknownName_IsForwardedToUpstreamAndRelayed()
    {
        var upstreamResolver = new StaticResolver().Add("upstream-only.example", IPAddress.Parse("203.0.113.5"));
        await using var upstream = await StartServerAsync(upstreamResolver);

        var localResolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(localResolver, new[] { upstream.LocalEndPoint });

        var query = BuildQuery("upstream-only.example", DnsRecordType.A);
        var response = await DnsClient.QueryAsync(server.LocalEndPoint, query);

        Assert.Equal(query.Id, response.Id); // relayed response id matches the original client's query id
        Assert.Equal(DnsRcode.NoError, response.Rcode);
        var record = Assert.Single(response.Answers);
        Assert.Equal(IPAddress.Parse("203.0.113.5"), record.AsIPAddress());

        await server.StopAsync();
        await upstream.StopAsync();
    }

    [Fact]
    public async Task UnknownName_WithNoUpstreamsReachable_AnswersServFail()
    {
        // Port 1 on loopback should have nothing listening for UDP DNS traffic.
        var deadUpstream = new IPEndPoint(IPAddress.Loopback, 1);
        var localResolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(localResolver, new[] { deadUpstream });

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("nowhere.example", DnsRecordType.A));

        Assert.Equal(DnsRcode.ServFail, response.Rcode);

        await server.StopAsync();
    }

    [Fact]
    public async Task TcpPath_AnswersKnownName()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        var query = BuildQuery("web.adtest.local", DnsRecordType.A);
        var queryBytes = query.Serialize();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(server.LocalEndPoint.Address, server.LocalEndPoint.Port);
        var stream = tcpClient.GetStream();

        var lengthPrefix = new byte[] { (byte)(queryBytes.Length >> 8), (byte)queryBytes.Length };
        await stream.WriteAsync(lengthPrefix);
        await stream.WriteAsync(queryBytes);

        var responseLengthBuffer = new byte[2];
        await ReadExactAsync(stream, responseLengthBuffer, 2);
        int responseLength = (responseLengthBuffer[0] << 8) | responseLengthBuffer[1];
        var responseBuffer = new byte[responseLength];
        await ReadExactAsync(stream, responseBuffer, responseLength);

        var response = DnsMessage.Parse(responseBuffer);
        Assert.Equal(DnsRcode.NoError, response.Rcode);
        var record = Assert.Single(response.Answers);
        Assert.Equal(IPAddress.Parse("192.168.64.10"), record.AsIPAddress());

        await server.StopAsync();
    }

    [Fact]
    public async Task Edns0Opt_IsEchoedWithDefaultPayloadSize()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("web.adtest.local", DnsRecordType.A, edns: true));

        Assert.NotNull(response.Edns);
        Assert.Equal(EdnsOptions.DefaultResponderPayloadSize, response.Edns!.UdpPayloadSize);

        await server.StopAsync();
    }

    [Fact]
    public async Task NoEdns0InQuery_MeansNoEdns0InResponse()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("web.adtest.local", DnsRecordType.A, edns: false));

        Assert.Null(response.Edns);

        await server.StopAsync();
    }

    [Fact]
    public async Task MalformedUdpPacket_IsIgnoredWithoutCrashingServer()
    {
        var resolver = new StaticResolver().Add("web.adtest.local", IPAddress.Parse("192.168.64.10"));
        await using var server = await StartServerAsync(resolver);

        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            var garbage = new byte[] { 0x01, 0x02, 0x03 }; // shorter than a DNS header
            await socket.SendToAsync(garbage, SocketFlags.None, server.LocalEndPoint);
        }

        // Give the malformed packet a moment to be processed (and dropped), then confirm the
        // server is still alive and answering normally.
        await Task.Delay(100);

        var response = await DnsClient.QueryAsync(server.LocalEndPoint, BuildQuery("web.adtest.local", DnsRecordType.A));
        Assert.Equal(DnsRcode.NoError, response.Rcode);
        Assert.Single(response.Answers);

        await server.StopAsync();
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) throw new IOException("Connection closed before expected bytes were read.");
            offset += read;
        }
    }
}
