using System.Net;
using Cider.Core.Net;
using Xunit;

namespace Cider.Tests.Net;

public class NameRegistryTests
{
    [Fact]
    public void Registered_names_resolve_per_network_and_case_insensitively()
    {
        var registry = new NameRegistry();
        registry.Register("bridge", "c1", ["Web", "web.local"], IPAddress.Parse("192.168.64.5"));

        Assert.True(registry.TryResolve("bridge", "web", out var ip));
        Assert.Equal("192.168.64.5", ip!.ToString());
        Assert.True(registry.TryResolve("BRIDGE", "WEB.LOCAL", out _));
        Assert.False(registry.TryResolve("other", "web", out _));
        Assert.True(registry.TryResolveAny("web", out _));
    }

    [Fact]
    public void Unregister_drops_every_name_of_one_container()
    {
        var registry = new NameRegistry();
        registry.Register("bridge", "c1", ["web", "api"], IPAddress.Parse("192.168.64.5"));
        registry.Register("bridge", "c2", ["db"], IPAddress.Parse("192.168.64.6"));

        registry.Unregister("c1");

        Assert.False(registry.TryResolve("bridge", "web", out _));
        Assert.False(registry.TryResolve("bridge", "api", out _));
        Assert.True(registry.TryResolve("bridge", "db", out _));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Snapshot_lists_every_registration()
    {
        var registry = new NameRegistry();
        registry.Register("bridge", "c1", ["web"], IPAddress.Parse("192.168.64.5"));
        registry.Register("proj", "c1", ["web"], IPAddress.Parse("192.168.65.5"));

        var snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, entry => entry.Network == "proj" && entry.Name == "web");
    }

    [Fact]
    public void Blank_names_are_ignored()
    {
        var registry = new NameRegistry();
        registry.Register("bridge", "c1", ["", "  ", "web"], IPAddress.Parse("192.168.64.5"));

        Assert.Equal(1, registry.Count);
    }
}
