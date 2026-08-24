using System.Net;
using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

public sealed partial class FakeContainerRuntime
{
    private const string DefaultNetworkId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly List<RuntimeNetwork> _networks =
    [
        new RuntimeNetwork
        {
            Name = "default",
            Id = DefaultNetworkId,
            Mode = "nat",
            Subnet = "192.168.64.0/24",
            Gateway = "192.168.64.1",
            Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        },
    ];

    public Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct)
    {
        Record("ListNetworksAsync");
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<RuntimeNetwork>>(_networks.ToList());
        }
    }

    public Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct)
    {
        Record($"InspectNetworkAsync:{name}");
        lock (_sync)
        {
            return Task.FromResult(_networks.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal)));
        }
    }

    /// <summary>When set, every <see cref="CreateNetworkAsync"/> fails with it — the seam for
    /// driving manager behaviour on a misbehaving runtime.</summary>
    public RuntimeException? CreateNetworkFailure { get; set; }

    public Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)
    {
        Record($"CreateNetworkAsync:{spec.Name}");
        if (CreateNetworkFailure is { } failure)
        {
            throw failure;
        }

        RequireAppleNetworkName(spec.Name);
        foreach (var key in spec.Labels.Keys)
        {
            RequireAppleLabelKey(key);
        }

        lock (_sync)
        {
            if (_networks.Any(n => string.Equals(n.Name, spec.Name, StringComparison.Ordinal)))
            {
                throw RuntimeException.Conflict($"network {spec.Name} already exists");
            }

            _networks.Add(new RuntimeNetwork
            {
                Name = spec.Name,
                Id = FixedDigest("network:" + spec.Name)["sha256:".Length..],
                Mode = "nat",
                Subnet = spec.Subnet,
                Gateway = DeriveGateway(spec.Subnet),
                Internal = spec.Internal,
                Labels = spec.Labels,
                Created = DateTimeOffset.UtcNow,
            });
        }

        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        Record($"RemoveNetworkAsync:{name}");
        lock (_sync)
        {
            var network = _networks.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal))
                ?? throw RuntimeException.NotFound($"network {name} not found");

            if (_containers.Any(c => c.Networks.Any(a => string.Equals(a.Network, name, StringComparison.Ordinal))))
            {
                throw RuntimeException.Conflict($"network {name} has active endpoints");
            }

            _networks.Remove(network);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Apple <c>container network create</c> refuses a name outside <c>[a-z0-9_-]</c> and one that
    /// starts or ends with <c>-</c> (probed against 1.2.2 for), where the Docker
    /// Engine API happily accepts <c>Aspire-Session-Network-…-</c>.
    /// </summary>
    private static void RequireAppleNetworkName(string name)
    {
        var valid = name.Length > 0
            && name[0] != '-'
            && name[^1] != '-'
            && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
        if (!valid)
        {
            throw RuntimeException.InvalidArgument($"invalid network name: {name}");
        }
    }

    /// <summary>
    /// Apple <c>network create --label</c> — and only that one, <c>container create</c> and
    /// <c>volume create</c> do not validate — accepts label keys of <c>[a-z0-9.-]</c> only, which is
    /// what Aspire/DCP's <c>com.microsoft.developer.usvc-dev.creatorProcessId</c> trips over.
    /// </summary>
    private static void RequireAppleLabelKey(string key)
    {
        if (key.Length == 0 || !key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
        {
            throw RuntimeException.InvalidArgument(
                "LabelError(code: ContainerResource.AppErrorCode(rawValue: \"invalid_label_key_content\"), "
                + $"metadata: [\"key\": \"{key}\"])");
        }
    }

    private static string? DeriveGateway(string? subnet)
    {
        if (subnet is null)
        {
            return null;
        }

        var parts = subnet.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        bytes[^1] = 1;
        return new IPAddress(bytes).ToString();
    }
}
