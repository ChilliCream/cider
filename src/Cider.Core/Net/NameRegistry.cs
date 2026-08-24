using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Cider.Core.Net;

/// <summary>
/// The name → address table the daemon's DNS server answers from: every running container
/// registers its name, hostname, network aliases and compose service label per network.
/// Thread-safe; names are matched case-insensitively (DNS is).
/// </summary>
public sealed class NameRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];

    /// <summary>Registers every name of one container on one network.</summary>
    public void Register(string network, string containerId, IEnumerable<string> names, IPAddress ip)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);
        ArgumentException.ThrowIfNullOrEmpty(containerId);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(ip);

        lock (_gate)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                _entries[new Key(network.ToLowerInvariant(), name.Trim().ToLowerInvariant())] = new Entry(containerId, ip);
            }
        }
    }

    /// <summary>Drops every name registered for one container.</summary>
    public void Unregister(string containerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerId);

        lock (_gate)
        {
            var stale = _entries
                .Where(pair => string.Equals(pair.Value.ContainerId, containerId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in stale)
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Resolves a name on one network.</summary>
    public bool TryResolve(string network, string name, [NotNullWhen(true)] out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrEmpty(network) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(new Key(network.ToLowerInvariant(), name.ToLowerInvariant()), out var entry))
            {
                ip = entry.Ip;
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves a name on any network (first match wins).</summary>
    public bool TryResolveAny(string name, [NotNullWhen(true)] out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var lowered = name.ToLowerInvariant();
        lock (_gate)
        {
            foreach (var (key, entry) in _entries)
            {
                if (string.Equals(key.Name, lowered, StringComparison.Ordinal))
                {
                    ip = entry.Ip;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Every registration, for diagnostics and tests.</summary>
    public IReadOnlyList<(string Network, string Name, IPAddress Ip)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Select(pair => (pair.Key.Network, pair.Key.Name, pair.Value.Ip))];
        }
    }

    /// <summary>Number of registered names.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private readonly record struct Key(string Network, string Name);

    private readonly record struct Entry(string ContainerId, IPAddress Ip);
}
