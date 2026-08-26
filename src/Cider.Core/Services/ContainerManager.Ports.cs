using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    /// <summary>
    /// <c>true</c> when the daemon carries published-port traffic itself
    /// (<c>PortPublishing=proxy</c>), so the engine must not be given <c>-p</c> at all.
    /// </summary>
    private bool ProxyPublishing => _publisher.Enabled;

    /// <summary>
    /// Binds the host side of every port mapping of a running container. In <c>proxy</c> mode a TCP
    /// listener is bound the moment the container starts, whether or not its VM address is known yet
    /// (<see cref="Net.TcpPortForwarder"/> holds accepted connections until it is — cider-ede.18, so a
    /// client racing to connect during the VM boot gets queued instead of refused); a UDP mapping has
    /// no such accept-and-hold mode, so it still waits for the address before it is bound at all, same
    /// as before. Idempotent and cheap once every mapping the record declares is bound and — address
    /// permitting — resolved, so the post-start path, the network refresh, the poller and reconcile can
    /// all call it on every tick.
    /// </summary>
    internal async Task EnsurePublishedPortsAsync(ContainerRecord record, CancellationToken ct)
    {
        if (!ProxyPublishing || record.Ports.Count == 0 || !record.State.Running)
        {
            return;
        }

        var haveAddress = TryGetContainerAddress(record, out var containerIp);

        // The steady-state case on every tick once a container has settled: everything this record
        // declares is already bound, and — if the address is known — every listener already has it.
        if (_publisher.IsPublished(record.Id) && (!haveAddress || !_publisher.NeedsAddress(record.Id)))
        {
            return;
        }

        await _publishGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: start, the poller and the network refresh all race here.
            if (!record.State.Running)
            {
                return;
            }

            if (haveAddress && _publisher.NeedsAddress(record.Id))
            {
                _publisher.ResolveAddress(record.Id, containerIp);
            }

            foreach (var (key, bindings) in record.Ports)
            {
                var (containerPort, proto) = SplitPortKey(key);
                var isUdp = string.Equals(proto, "udp", StringComparison.OrdinalIgnoreCase);
                if (isUdp && !haveAddress)
                {
                    continue;
                }

                foreach (var binding in bindings)
                {
                    if (!int.TryParse(binding.HostPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort))
                    {
                        continue;
                    }

                    foreach (var hostIp in HostAddressesFor(binding.HostIp))
                    {
                        if (_publisher.IsPublished(record.Id, proto, hostIp, hostPort))
                        {
                            continue;
                        }

                        try
                        {
                            await _publisher.PublishAsync(
                                record.Id, proto, hostIp, hostPort, haveAddress ? containerIp : null, containerPort, ct);
                        }
                        catch (SocketException ex)
                        {
                            // The port was verified free at create time; losing it now (or having no
                            // IPv6 stack at all) must not take the container down with it.
                            _logger.LogWarning(
                                ex,
                                "could not publish {HostIp}:{HostPort} -> {ContainerIp}:{ContainerPort}/{Proto} for container {Container}",
                                hostIp,
                                hostPort,
                                containerIp,
                                containerPort,
                                proto,
                                record.Id);
                        }
                    }
                }
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>Closes every publication of a container (die, stop, remove).</summary>
    internal void UnpublishPorts(string containerId)
    {
        if (ProxyPublishing)
        {
            _publisher.Unpublish(containerId);
        }
    }

    /// <summary>Every host address one binding covers: an empty or wildcard host IP means both families, as in Docker.</summary>
    private static IEnumerable<IPAddress> HostAddressesFor(string? hostIp)
    {
        if (string.IsNullOrEmpty(hostIp) || string.Equals(hostIp, "0.0.0.0", StringComparison.Ordinal))
        {
            return [IPAddress.Any, IPAddress.IPv6Any];
        }

        return IPAddress.TryParse(hostIp, out var parsed) ? [parsed] : [IPAddress.Any];
    }

    /// <summary>The container's first known VM address, if the runtime has reported one yet.</summary>
    private static bool TryGetContainerAddress(ContainerRecord record, out IPAddress address)
    {
        foreach (var endpoint in record.Networks.Values)
        {
            if (!string.IsNullOrEmpty(endpoint.IPAddress) && IPAddress.TryParse(endpoint.IPAddress, out var parsed))
            {
                address = parsed;
                return true;
            }
        }

        address = IPAddress.None;
        return false;
    }
}
