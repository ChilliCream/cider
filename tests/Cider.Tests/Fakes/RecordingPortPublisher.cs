using System.Collections.Concurrent;
using System.Net;
using Cider.Core.Net;

namespace Cider.Tests.Fakes;

/// <summary>
/// An <see cref="IPortPublisher"/> that records what the manager asked it to publish instead of
/// binding real sockets, so container tests can assert the proxy-mode plumbing without touching the
/// host's network. <see cref="PortProxyManager"/> itself is covered by its own tests.
/// </summary>
public sealed class RecordingPortPublisher(bool enabled = true) : IPortPublisher
{
    private readonly ConcurrentDictionary<string, List<PublishedPortHandle>> _byContainer = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Enabled { get; } = enabled;

    /// <summary>Container ids that were unpublished, in order (a container may appear more than once).</summary>
    public List<string> Unpublished { get; } = [];

    /// <summary>Everything ever published, including publications that were closed again.</summary>
    public List<PublishedPort> Published { get; } = [];

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
        var port = new PublishedPort(containerId, proto, hostIp, hostPort, containerIp, containerPort);
        var handle = new PublishedPortHandle(port, null);

        lock (Published)
        {
            Published.Add(port);
        }

        var live = _byContainer.GetOrAdd(containerId, static _ => []);
        lock (live)
        {
            live.Add(handle);
        }

        return Task.FromResult(handle);
    }

    /// <inheritdoc />
    public void Unpublish(string containerId)
    {
        lock (Unpublished)
        {
            Unpublished.Add(containerId);
        }

        _byContainer.TryRemove(containerId, out _);
    }

    /// <inheritdoc />
    public bool IsPublished(string containerId) =>
        _byContainer.TryGetValue(containerId, out var live) && live.Count > 0;

    /// <inheritdoc />
    public bool IsPublished(string containerId, string proto, IPAddress hostIp, int hostPort)
    {
        if (!_byContainer.TryGetValue(containerId, out var live))
        {
            return false;
        }

        lock (live)
        {
            return live.Exists(handle =>
                string.Equals(handle.Port.Proto, proto, StringComparison.OrdinalIgnoreCase) &&
                handle.Port.HostPort == hostPort &&
                handle.Port.HostIp.Equals(hostIp));
        }
    }

    /// <inheritdoc />
    public bool NeedsAddress(string containerId)
    {
        if (!_byContainer.TryGetValue(containerId, out var live))
        {
            return false;
        }

        lock (live)
        {
            return live.Exists(handle => handle.Port.ContainerIp is null);
        }
    }

    /// <inheritdoc />
    public void ResolveAddress(string containerId, IPAddress containerIp)
    {
        if (!_byContainer.TryGetValue(containerId, out var live))
        {
            return;
        }

        lock (live)
        {
            for (var i = 0; i < live.Count; i++)
            {
                var handle = live[i];
                if (handle.Port.ContainerIp is not null)
                {
                    continue;
                }

                handle.Port = handle.Port with { ContainerIp = containerIp };
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PublishedPort> Snapshot()
    {
        var result = new List<PublishedPort>();
        foreach (var live in _byContainer.Values)
        {
            lock (live)
            {
                result.AddRange(live.Select(handle => handle.Port));
            }
        }

        return result;
    }

    /// <summary>Every publication currently live for one container.</summary>
    public IReadOnlyList<PublishedPort> LiveFor(string containerId)
    {
        if (!_byContainer.TryGetValue(containerId, out var live))
        {
            return [];
        }

        lock (live)
        {
            return live.Select(handle => handle.Port).ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _byContainer.Clear();
}
