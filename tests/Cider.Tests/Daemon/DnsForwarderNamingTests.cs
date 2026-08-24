using Cider.Daemon.Dns;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>The engine-side id a DNS forwarder is created under has to be one Apple accepts.</summary>
public sealed class DnsForwarderNamingTests
{
    /// <summary>The session network Aspire's DCP creates: 42 characters, and the forwarder name grew past 63.</summary>
    private const string AspireNetwork = "aspire-session-network-pscxmqmq-e2e-aspire-";

    [Theory]
    [InlineData("bridge")]
    [InlineData(AspireNetwork)]
    [InlineData("a-really-long-compose-project-name_default-with-more-words-on-the-end")]
    public void ForwarderName_StaysInsideApplesContainerIdLimit(string network)
    {
        var name = DnsForwarderService.ForwarderName(network, DnsForwarderService.DataDirHash("/tmp/cider-e2e-1g8"));

        // Apple `container create --name` refuses anything longer with
        // "container ID … is not a valid container ID", and the network then has no DNS at all.
        Assert.True(name.Length <= 63, $"the forwarder id is {name.Length} characters: {name}");
        Assert.StartsWith("cider-dns-", name, StringComparison.Ordinal);
        Assert.EndsWith(DnsForwarderService.DataDirHash("/tmp/cider-e2e-1g8"), name, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwarderName_IsStableAndPerDataDir()
    {
        var first = DnsForwarderService.ForwarderName(AspireNetwork, DnsForwarderService.DataDirHash("/tmp/one"));
        var second = DnsForwarderService.ForwarderName(AspireNetwork, DnsForwarderService.DataDirHash("/tmp/two"));

        Assert.Equal(first, DnsForwarderService.ForwarderName(AspireNetwork, DnsForwarderService.DataDirHash("/tmp/one")));
        Assert.NotEqual(first, second);
    }
}
