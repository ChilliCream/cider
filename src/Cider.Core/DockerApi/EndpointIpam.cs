using System.Net;
using System.Net.Sockets;
using Cider.Core.DockerApi.Models;

namespace Cider.Core.DockerApi;

/// <summary>
/// Validation for the static addresses a client may put in
/// <c>NetworkingConfig.EndpointsConfig[net].IPAMConfig</c> on create, or in the same field on
/// <c>POST /networks/{id}/connect</c>.
/// </summary>
/// <remarks>
/// <para>
/// Apple <c>container</c> has no equivalent of <c>docker run --ip</c>: <c>ArgBuilder</c> emits only
/// <c>--network &lt;name&gt;</c>, and reconciliation later overwrites <c>EndpointSettings.IPAddress</c>
/// with whatever address the runtime chose. So a requested address is never honoured. Until this
/// check existed the request was simply dropped: <c>create</c> answered 201 for an address outside
/// the network's subnet, and even for a string that is not an address at all, and the client was left
/// believing it had pinned an IP.
/// </para>
/// <para>
/// What is checkable is checked here, in dockerd's wording (API 1.47 == moby v27, so the v25+ forms
/// from <c>moby/api/types/network/endpoint.go</c>): a malformed address, and an address outside the
/// network's subnet. An address that is well-formed and inside the subnet is still not honoured —
/// that is a separate capability gap, documented in the README, not something to fail create over,
/// since dockerd would accept it too.
/// </para>
/// </remarks>
public static class EndpointIpam
{
    /// <summary>
    /// Throws a 400 in dockerd's wording when <paramref name="settings"/> asks for a static address
    /// that is malformed, or that <paramref name="subnet"/> (CIDR, or <c>null</c> when the network
    /// has none) does not contain.
    /// </summary>
    public static void Validate(string network, EndpointSettings? settings, string? subnet)
    {
        var requested = settings?.IPAMConfig;
        if (requested is null)
        {
            return;
        }

        Check(network, requested.IPv4Address, AddressFamily.InterNetwork, "invalid IPv4 address", subnet);
        Check(network, requested.IPv6Address, AddressFamily.InterNetworkV6, "invalid IPv6 address", subnet: null);

        foreach (var linkLocal in requested.LinkLocalIPs ?? [])
        {
            if (!IPAddress.TryParse(linkLocal, out _))
            {
                throw DockerErrors.InvalidEndpointSettings(network, $"invalid link-local IP address: {linkLocal}");
            }
        }
    }

    private static void Check(string network, string? address, AddressFamily family, string malformed, string? subnet)
    {
        if (string.IsNullOrEmpty(address))
        {
            return;
        }

        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != family)
        {
            throw DockerErrors.InvalidEndpointSettings(network, $"{malformed}: {address}");
        }

        // No subnet to compare against: dockerd defers the containment check in that case too, so a
        // well-formed address is accepted here.
        if (subnet is null || !IPNetwork.TryParse(subnet, out var range) || range.Contains(parsed))
        {
            return;
        }

        throw DockerErrors.InvalidEndpointSettings(
            network,
            $"no configured subnet or ip-range contain the IP address {address}");
    }
}
