using System.Net;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.Net;
using Cider.Core.Services;
using Cider.Dns;

namespace Cider.Daemon.Dns;

/// <summary>
/// Answers container names for the daemon's DNS server (CONTRACTS §I). Queries arrive from the
/// per-network CoreDNS forwarder, so the client address identifies the network whose
/// <see cref="NameRegistry"/> entries apply; anything unknown is declined (null) and the server
/// forwards it upstream.
/// </summary>
public sealed class DaemonDnsResolver : IDnsResolver
{
    /// <summary>The names every Docker container expects to resolve to the host's gateway address.</summary>
    private static readonly string[] HostNames =
    [
        "host.docker.internal",
        "gateway.docker.internal",
        "host.containers.internal",
    ];

    private static readonly TimeSpan NetworkCacheTtl = TimeSpan.FromSeconds(10);

    private readonly NameRegistry _names;
    private readonly NetworkManager _networks;
    private readonly CiderOptions _options;
    private readonly ILogger<DaemonDnsResolver> _logger;
    private readonly SemaphoreSlim _networkGate = new(1, 1);

    private IReadOnlyList<NetworkView> _cachedNetworks = [];
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    /// <summary>Creates the resolver.</summary>
    public DaemonDnsResolver(NameRegistry names, NetworkManager networks, CiderOptions options, ILogger<DaemonDnsResolver> logger)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _networks = networks ?? throw new ArgumentNullException(nameof(networks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<DnsAnswer?> ResolveAsync(DnsQuestion question, IPEndPoint client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (question.Class != DnsClass.In || question.Type is not (DnsRecordType.A or DnsRecordType.Aaaa))
        {
            return null;
        }

        var name = Normalize(question.Name);
        if (name.Length == 0)
        {
            return null;
        }

        var network = await NetworkForAsync(client, ct);

        if (IsHostName(name))
        {
            var gateway = await GatewayForAsync(network, ct);
            if (gateway is null)
            {
                return null;
            }

            return question.Type == DnsRecordType.A
                ? DnsAnswer.Of(DnsRecord.CreateA(question.Name, gateway))
                : DnsAnswer.NoData();
        }

        foreach (var candidate in Candidates(name))
        {
            if (!TryLookup(network, candidate, out var address))
            {
                continue;
            }

            return question.Type == DnsRecordType.A
                ? DnsAnswer.Of(DnsRecord.CreateA(question.Name, address))
                : DnsAnswer.NoData();
        }

        return null;
    }

    private IEnumerable<string> Candidates(string name)
    {
        yield return name;

        var domain = _options.DnsSearchDomain;
        if (!string.IsNullOrEmpty(domain))
        {
            var suffix = "." + domain.Trim('.').ToLowerInvariant();
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                yield return name[..^suffix.Length];
            }
        }
    }

    private bool TryLookup(string? network, string name, out IPAddress address)
    {
        if (network is not null && _names.TryResolve(network, name, out var scoped))
        {
            address = scoped;
            return true;
        }

        if (_names.TryResolveAny(name, out var any))
        {
            address = any;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static bool IsHostName(string name) =>
        HostNames.Contains(name, StringComparer.Ordinal);

    private static string Normalize(string name) =>
        name.TrimEnd('.').ToLowerInvariant();

    private async Task<string?> NetworkForAsync(IPEndPoint? client, CancellationToken ct)
    {
        if (client is null)
        {
            return null;
        }

        foreach (var view in await NetworksAsync(ct))
        {
            if (view.Subnet is { } subnet && subnet.Contains(client.Address))
            {
                return view.Name;
            }
        }

        return null;
    }

    private async Task<IPAddress?> GatewayForAsync(string? network, CancellationToken ct)
    {
        foreach (var view in await NetworksAsync(ct))
        {
            if (network is not null && string.Equals(view.Name, network, StringComparison.OrdinalIgnoreCase))
            {
                return view.Gateway;
            }
        }

        foreach (var view in await NetworksAsync(ct))
        {
            if (string.Equals(view.Name, "bridge", StringComparison.Ordinal) && view.Gateway is not null)
            {
                return view.Gateway;
            }
        }

        return (await NetworksAsync(ct)).FirstOrDefault(v => v.Gateway is not null)?.Gateway;
    }

    private async Task<IReadOnlyList<NetworkView>> NetworksAsync(CancellationToken ct)
    {
        if (_cacheExpiry > DateTimeOffset.UtcNow)
        {
            return _cachedNetworks;
        }

        await _networkGate.WaitAsync(ct);
        try
        {
            if (_cacheExpiry > DateTimeOffset.UtcNow)
            {
                return _cachedNetworks;
            }

            var views = new List<NetworkView>();
            try
            {
                foreach (var resource in await _networks.ListAsync(Filters.Empty, ct))
                {
                    if (resource.Name is "host" or "none")
                    {
                        continue;
                    }

                    var runtime = await _networks.GetRuntimeNetworkAsync(resource.Name, ct);
                    if (runtime is null)
                    {
                        continue;
                    }

                    IPNetwork? subnet = null;
                    if (!string.IsNullOrEmpty(runtime.Subnet) && IPNetwork.TryParse(runtime.Subnet, out var parsed))
                    {
                        subnet = parsed;
                    }

                    IPAddress? gateway = null;
                    if (!string.IsNullOrEmpty(runtime.Gateway) && IPAddress.TryParse(runtime.Gateway, out var gw))
                    {
                        gateway = gw;
                    }

                    views.Add(new NetworkView(resource.Name, subnet, gateway));
                }
            }
            catch (Exception ex) when (ex is DockerApiException or Core.Runtime.RuntimeException)
            {
                _logger.LogDebug(ex, "could not enumerate networks for DNS");
            }

            _cachedNetworks = views;
            _cacheExpiry = DateTimeOffset.UtcNow.Add(NetworkCacheTtl);
            return _cachedNetworks;
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private sealed record NetworkView(string Name, IPNetwork? Subnet, IPAddress? Gateway);
}
