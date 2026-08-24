using System.Net;

namespace Cider.Core.Net;

/// <summary>
/// Supplies the in-network DNS address containers should use. Port 53 on the host is occupied by
/// macOS/vmnet once Apple's container network is up, so the daemon listens on a high port and a
/// small forwarder container inside the network relays to it; this seam hands out its address.
/// </summary>
public interface IDnsForwarderService
{
    /// <summary>
    /// Makes sure a forwarder is reachable on <paramref name="dockerNetworkName"/> and returns its
    /// address, or <c>null</c> when no forwarder is available (containers then get no <c>--dns</c>).
    /// </summary>
    Task<IPAddress?> EnsureAsync(string dockerNetworkName, CancellationToken ct);

    /// <summary>
    /// Tears the forwarder for <paramref name="dockerNetworkName"/> down. A forwarder container is
    /// attached to the network it serves, so the network cannot be removed while it is still there
    /// ("has active endpoints"); the network manager calls this first.
    /// </summary>
    Task ReleaseAsync(string dockerNetworkName, CancellationToken ct);
}

/// <summary>An <see cref="IDnsForwarderService"/> that never provides a forwarder (DNS disabled, tests).</summary>
public sealed class NullDnsForwarderService : IDnsForwarderService
{
    /// <summary>The shared instance; the type carries no state.</summary>
    public static readonly NullDnsForwarderService Instance = new();

    /// <inheritdoc />
    public Task<IPAddress?> EnsureAsync(string dockerNetworkName, CancellationToken ct) =>
        Task.FromResult<IPAddress?>(null);

    /// <inheritdoc />
    public Task ReleaseAsync(string dockerNetworkName, CancellationToken ct) => Task.CompletedTask;
}
