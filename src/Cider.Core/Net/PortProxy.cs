using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Net;

/// <summary>One live host-side publication of a container port.</summary>
/// <param name="ContainerId">Docker id of the container the publication belongs to.</param>
/// <param name="Proto"><c>tcp</c> or <c>udp</c>.</param>
/// <param name="HostIp">The address actually bound on the host.</param>
/// <param name="HostPort">The host port actually bound.</param>
/// <param name="ContainerIp">
/// The container's VM address traffic is forwarded to, or <c>null</c> when the listener is already
/// bound but the address is not known yet (cider-ede.18: TCP only — see <see cref="TcpPortForwarder"/>).
/// </param>
/// <param name="ContainerPort">The container port traffic is forwarded to.</param>
public sealed record PublishedPort(
    string ContainerId,
    string Proto,
    IPAddress HostIp,
    int HostPort,
    IPAddress? ContainerIp,
    int ContainerPort)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{HostIp}:{HostPort} -> {(ContainerIp is null ? "(pending)" : $"{ContainerIp}:{ContainerPort}")}/{Proto}");
}

/// <summary>A publication's lifetime: disposing it closes the listener and every live connection.</summary>
public sealed class PublishedPortHandle : IDisposable
{
    private readonly IDisposable? _resource;

    /// <summary>Creates a handle over <paramref name="resource"/>, the bound listener released on dispose.</summary>
    public PublishedPortHandle(PublishedPort port, IDisposable? resource)
    {
        Port = port;
        _resource = resource;
    }

    /// <summary>What this handle publishes; replaced in place once a pending address resolves.</summary>
    public PublishedPort Port { get; private set; }

    /// <summary>The forwarder backing this publication, when it is one (every real publication is).</summary>
    internal IPortForwarder? Forwarder => _resource as IPortForwarder;

    /// <summary>
    /// Supplies the container's address: retargets the forwarder and replaces <see cref="Port"/>
    /// with a copy carrying it. Re-callable when the address changes (cider-bum) — a restarted
    /// container comes back on a new VM address, and the publication must follow it. Callers hold
    /// the owning manager's lock.
    /// </summary>
    internal void Resolve(IPAddress containerIp)
    {
        Forwarder?.ResolveTarget(containerIp);
        Port = Port with { ContainerIp = containerIp };
    }

    /// <inheritdoc />
    public void Dispose() => _resource?.Dispose();
}

/// <summary>
/// The seam <see cref="Services.ContainerManager"/> publishes container ports through. In
/// <c>proxy</c> mode this is <see cref="PortProxyManager"/>; in <c>apple</c> mode it is
/// <see cref="NullPortPublisher"/> and the runtime gets the <c>-p</c> flags instead.
/// </summary>
public interface IPortPublisher : IDisposable
{
    /// <summary><c>true</c> when the daemon carries published-port traffic itself.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Binds <paramref name="hostIp"/>:<paramref name="hostPort"/> and forwards it into the container.
    /// <paramref name="containerIp"/> may be <c>null</c> for a <c>tcp</c> mapping — the listener binds
    /// and accepts right away, holding each connection (bounded) until <see cref="ResolveAddress"/>
    /// supplies it (cider-ede.18); a <c>udp</c> mapping has no such mode and requires it up front.
    /// </summary>
    Task<PublishedPortHandle> PublishAsync(
        string containerId,
        string proto,
        IPAddress hostIp,
        int hostPort,
        IPAddress? containerIp,
        int containerPort,
        CancellationToken ct);

    /// <summary>Closes every publication of one container. Unknown ids are ignored.</summary>
    void Unpublish(string containerId);

    /// <summary><c>true</c> when this container currently has at least one live publication.</summary>
    bool IsPublished(string containerId);

    /// <summary><c>true</c> when this container has a live publication at that exact endpoint.</summary>
    bool IsPublished(string containerId, string proto, IPAddress hostIp, int hostPort);

    /// <summary>
    /// <c>true</c> when at least one live publication of this container is not currently targeting
    /// <paramref name="containerIp"/>: it was bound with the address still unknown, or (TCP only,
    /// cider-bum) it targets a different — stale — address and can be retargeted. A UDP relay
    /// cannot be retargeted in place, so one holding a different address does not count.
    /// </summary>
    bool NeedsAddress(string containerId, IPAddress containerIp);

    /// <summary>
    /// Supplies the container's address to every publication of it that is not targeting it yet —
    /// the ones bound without one (unblocking whatever connections were waiting on it) and, for TCP
    /// (cider-bum), the ones still targeting a previous boot's address. A no-op for a container with
    /// no publications, or none needing the address.
    /// </summary>
    void ResolveAddress(string containerId, IPAddress containerIp);

    /// <summary>Every live publication, for diagnostics and tests.</summary>
    IReadOnlyList<PublishedPort> Snapshot();
}

/// <summary>The <c>apple</c>-mode publisher: it never publishes anything.</summary>
public sealed class NullPortPublisher : IPortPublisher
{
    /// <summary>The shared instance.</summary>
    public static NullPortPublisher Instance { get; } = new();

    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public Task<PublishedPortHandle> PublishAsync(
        string containerId,
        string proto,
        IPAddress hostIp,
        int hostPort,
        IPAddress? containerIp,
        int containerPort,
        CancellationToken ct) =>
        throw new InvalidOperationException("cider: port publishing is handled by Apple container in this mode");

    /// <inheritdoc />
    public void Unpublish(string containerId)
    {
    }

    /// <inheritdoc />
    public bool IsPublished(string containerId) => false;

    /// <inheritdoc />
    public bool IsPublished(string containerId, string proto, IPAddress hostIp, int hostPort) => false;

    /// <inheritdoc />
    public bool NeedsAddress(string containerId, IPAddress containerIp) => false;

    /// <inheritdoc />
    public void ResolveAddress(string containerId, IPAddress containerIp)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<PublishedPort> Snapshot() => [];

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Publishes container ports from inside the daemon process: a TCP listener (or UDP relay) bound on
/// the host endpoint that forwards to the container's VM address. Apple <c>container</c> 1.2.2's own
/// <c>-p</c> forwarder cannot dial the guest on macOS 26 (see <c>tests/Cider.E2E.Tests/REPORT.md</c>),
/// while an ordinary host process — such as this one — can, so the daemon does the forwarding itself.
/// Publications are owned per container id and released with <see cref="Unpublish"/>.
/// </summary>
public sealed class PortProxyManager : IPortPublisher
{
    private readonly ILogger<PortProxyManager> _logger;
    private readonly ConcurrentDictionary<string, List<PublishedPortHandle>> _byContainer =
        new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    public PortProxyManager(ILogger<PortProxyManager> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool Enabled => true;

    /// <inheritdoc />
    public Task<PublishedPortHandle> PublishAsync(
        string containerId,
        string proto,
        IPAddress hostIp,
        int hostPort,
        IPAddress? containerIp,
        int containerPort,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentNullException.ThrowIfNull(hostIp);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var isUdp = string.Equals(proto, "udp", StringComparison.OrdinalIgnoreCase);
        if (isUdp)
        {
            // UdpPortForwarder has no accept-and-hold mode: a datagram with nowhere to go cannot wait
            // the way a TCP SYN can, so the caller (ContainerManager.EnsurePublishedPortsAsync) never
            // asks for one of these before the address is known.
            ArgumentNullException.ThrowIfNull(containerIp);
        }

        var host = new IPEndPoint(hostIp, hostPort);

        IPortForwarder forwarder;
        try
        {
            forwarder = isUdp
                ? new UdpPortForwarder(host, new IPEndPoint(containerIp!, containerPort), _logger)
                : new TcpPortForwarder(host, containerId, containerIp, containerPort, _logger);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(
                ex,
                "could not publish {HostIp}:{HostPort} for container {Container}: {Reason}",
                hostIp,
                hostPort,
                containerId,
                ex.Message);
            throw;
        }

        // A requested port of 0 is resolved by the OS; report what was actually bound.
        var descriptor = new PublishedPort(
            containerId,
            isUdp ? "udp" : "tcp",
            hostIp,
            forwarder.HostEndPoint.Port,
            containerIp,
            containerPort);

        var handle = new PublishedPortHandle(descriptor, forwarder);
        var handles = _byContainer.GetOrAdd(containerId, static _ => []);
        lock (handles)
        {
            handles.Add(handle);
        }

        _logger.LogDebug("published {Publication} for container {Container}", descriptor, containerId);
        return Task.FromResult(handle);
    }

    /// <inheritdoc />
    public void Unpublish(string containerId)
    {
        if (string.IsNullOrEmpty(containerId) || !_byContainer.TryRemove(containerId, out var handles))
        {
            return;
        }

        lock (handles)
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
                _logger.LogDebug("unpublished {Publication} for container {Container}", handle.Port, containerId);
            }

            handles.Clear();
        }
    }

    /// <inheritdoc />
    public bool IsPublished(string containerId)
    {
        if (string.IsNullOrEmpty(containerId) || !_byContainer.TryGetValue(containerId, out var handles))
        {
            return false;
        }

        lock (handles)
        {
            return handles.Count > 0;
        }
    }

    /// <inheritdoc />
    public bool IsPublished(string containerId, string proto, IPAddress hostIp, int hostPort)
    {
        if (string.IsNullOrEmpty(containerId) || !_byContainer.TryGetValue(containerId, out var handles))
        {
            return false;
        }

        lock (handles)
        {
            return handles.Exists(handle =>
                string.Equals(handle.Port.Proto, proto, StringComparison.OrdinalIgnoreCase) &&
                handle.Port.HostPort == hostPort &&
                handle.Port.HostIp.Equals(hostIp));
        }
    }

    /// <inheritdoc />
    public bool NeedsAddress(string containerId, IPAddress containerIp)
    {
        ArgumentNullException.ThrowIfNull(containerIp);
        if (string.IsNullOrEmpty(containerId) || !_byContainer.TryGetValue(containerId, out var handles))
        {
            return false;
        }

        lock (handles)
        {
            return handles.Exists(handle => NeedsAddress(handle, containerIp));
        }
    }

    /// <inheritdoc />
    public void ResolveAddress(string containerId, IPAddress containerIp)
    {
        ArgumentNullException.ThrowIfNull(containerIp);
        if (string.IsNullOrEmpty(containerId) || !_byContainer.TryGetValue(containerId, out var handles))
        {
            return;
        }

        lock (handles)
        {
            foreach (var handle in handles)
            {
                if (!NeedsAddress(handle, containerIp))
                {
                    continue;
                }

                var stale = handle.Port.ContainerIp;
                handle.Resolve(containerIp);
                if (stale is null)
                {
                    _logger.LogDebug("resolved {Publication} for container {Container}", handle.Port, containerId);
                }
                else
                {
                    // Info, not debug: this is the corrective path for cider-bum — the record carried
                    // a previous boot's address and the forwarder was dialing a dead target.
                    _logger.LogInformation(
                        "retargeted {Publication} for container {Container} (was {StaleIp})",
                        handle.Port,
                        containerId,
                        stale);
                }
            }
        }
    }

    /// <summary>
    /// Whether one publication still needs <paramref name="containerIp"/>: pending (bound without an
    /// address), or a TCP publication targeting a different, stale one. A UDP relay holding an
    /// address cannot be retargeted in place, so it never counts (cider-bum leaves UDP alone).
    /// </summary>
    private static bool NeedsAddress(PublishedPortHandle handle, IPAddress containerIp) =>
        handle.Port.ContainerIp is null ||
        (!handle.Port.ContainerIp.Equals(containerIp) &&
            !string.Equals(handle.Port.Proto, "udp", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IReadOnlyList<PublishedPort> Snapshot()
    {
        var result = new List<PublishedPort>();
        foreach (var handles in _byContainer.Values)
        {
            lock (handles)
            {
                result.AddRange(handles.Select(handle => handle.Port));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        foreach (var containerId in _byContainer.Keys)
        {
            Unpublish(containerId);
        }
    }
}
