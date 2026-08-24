using System.Net;
using Cider.Core.Configuration;
using Cider.Core.Events;
using Cider.Core.Net;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Daemon.Dns;
using Cider.Dns;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Daemon;

public sealed class DaemonDnsResolverTests
{
    private static readonly IPEndPoint Client = new(IPAddress.Parse("192.168.64.53"), 40000);

    private static async Task<(DaemonDnsResolver Resolver, NameRegistry Names)> CreateAsync(string searchDomain = "")
    {
        var options = new CiderOptions
        {
            DataDir = Path.Combine(Path.GetTempPath(), "cider-dns-tests", Guid.NewGuid().ToString("n")[..8]),
            DnsSearchDomain = searchDomain,
        };
        options.EnsureDirectories();

        var runtime = new FakeContainerRuntime();
        var networks = new NetworkManager(runtime, new InMemoryRecordStore<NetworkRecord>(), new EventBus(), NullLogger<NetworkManager>.Instance);
        await networks.EnsureDefaultAsync(CancellationToken.None);

        var names = new NameRegistry();
        return (new DaemonDnsResolver(names, networks, options, NullLogger<DaemonDnsResolver>.Instance), names);
    }

    [Fact]
    public async Task Answers_A_for_a_registered_container_name()
    {
        var (resolver, names) = await CreateAsync();
        names.Register("bridge", "cid", ["web"], IPAddress.Parse("192.168.64.5"));

        var answer = await resolver.ResolveAsync(new DnsQuestion("web.", DnsRecordType.A), Client, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        var record = Assert.Single(answer.Answers);
        Assert.Equal(IPAddress.Parse("192.168.64.5"), record.AsIPAddress());
    }

    [Fact]
    public async Task Matches_names_case_insensitively()
    {
        var (resolver, names) = await CreateAsync();
        names.Register("bridge", "cid", ["Web"], IPAddress.Parse("192.168.64.6"));

        var answer = await resolver.ResolveAsync(new DnsQuestion("WEB", DnsRecordType.A), Client, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Single(answer.Answers);
    }

    [Fact]
    public async Task Answers_NoData_for_AAAA_of_a_known_name()
    {
        var (resolver, names) = await CreateAsync();
        names.Register("bridge", "cid", ["web"], IPAddress.Parse("192.168.64.5"));

        var answer = await resolver.ResolveAsync(new DnsQuestion("web", DnsRecordType.Aaaa), Client, CancellationToken.None);

        Assert.NotNull(answer);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Empty(answer.Answers);
    }

    [Fact]
    public async Task Resolves_the_host_gateway_names()
    {
        var (resolver, _) = await CreateAsync();

        foreach (var name in new[] { "host.docker.internal", "gateway.docker.internal", "host.containers.internal" })
        {
            var answer = await resolver.ResolveAsync(new DnsQuestion(name, DnsRecordType.A), Client, CancellationToken.None);

            Assert.NotNull(answer);
            var record = Assert.Single(answer.Answers);
            Assert.Equal(IPAddress.Parse("192.168.64.1"), record.AsIPAddress());
        }
    }

    [Fact]
    public async Task Answers_the_search_domain_form()
    {
        var (resolver, names) = await CreateAsync("cider.internal");
        names.Register("bridge", "cid", ["web"], IPAddress.Parse("192.168.64.7"));

        var answer = await resolver.ResolveAsync(
            new DnsQuestion("web.cider.internal.", DnsRecordType.A), Client, CancellationToken.None);

        Assert.NotNull(answer);
        var record = Assert.Single(answer.Answers);
        Assert.Equal(IPAddress.Parse("192.168.64.7"), record.AsIPAddress());
    }

    [Fact]
    public async Task Declines_unknown_names_so_they_are_forwarded()
    {
        var (resolver, _) = await CreateAsync();

        Assert.Null(await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), Client, CancellationToken.None));
    }

    [Fact]
    public async Task Declines_types_it_does_not_serve()
    {
        var (resolver, names) = await CreateAsync();
        names.Register("bridge", "cid", ["web"], IPAddress.Parse("192.168.64.5"));

        Assert.Null(await resolver.ResolveAsync(new DnsQuestion("web", DnsRecordType.Txt), Client, CancellationToken.None));
    }
}
