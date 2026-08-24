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
/// <param name="ContainerIp">The container's VM address traffic is forwarded to.</param>
/// <param name="ContainerPort">The container port traffic is forwarded to.</param>
public sealed record PublishedPort(
    string ContainerId,
    string Proto,
    IPAddress HostIp,
    int HostPort,
    IPAddress ContainerIp,
    int ContainerPort)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{HostIp}:{HostPort} -> {ContainerIp}:{ContainerPort}/{Proto}");
}

/// <summary>A publication's lifetime: disposing it closes the listener and every live connection.</summary>
/// <param name="port">What this handle publishes.</param>
/// <param name="resource">The bound listener, released on dispose.</param>
public sealed class PublishedPortHandle(PublishedPort port, IDisposable? resource) : IDisposable
{
    /// <summary>What this handle publishes.</summary>
    public PublishedPort Port { get; } = port;

    /// <inheritdoc />
    public void Dispose() => resource?.Dispose();
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

    /// <summary>Binds <paramref name="hostIp"/>:<paramref name="hostPort"/> and forwards it into the container.</summary>
    Task<PublishedPortHandle> PublishAsync(
        string containerId,
        string proto,
        IPAddress hostIp,
        int hostPort,
        IPAddress containerIp,
        int containerPort,
        CancellationToken ct);

    /// <summary>Closes every publication of one container. Unknown ids are ignored.</summary>
    void Unpublish(string containerId);

    /// <summary><c>true</c> when this container currently has at least one live publication.</summary>
    bool IsPublished(string containerId);

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
        IPAddress containerIp,
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
        IPAddress containerIp,
        int containerPort,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentNullException.ThrowIfNull(hostIp);
        ArgumentNullException.ThrowIfNull(containerIp);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var isUdp = string.Equals(proto, "udp", StringComparison.OrdinalIgnoreCase);
        var host = new IPEndPoint(hostIp, hostPort);
        var target = new IPEndPoint(containerIp, containerPort);

        IPortForwarder forwarder;
        try
        {
            forwarder = isUdp
                ? new UdpPortForwarder(host, target, _logger)
                : new TcpPortForwarder(host, target, _logger);
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
