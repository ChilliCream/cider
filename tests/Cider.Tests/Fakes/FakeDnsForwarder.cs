using System.Net;
using Cider.Core.Net;

namespace Cider.Tests.Fakes;

/// <summary>An <see cref="IDnsForwarderService"/> that hands out a fixed address (or none).</summary>
public sealed class FakeDnsForwarder : IDnsForwarderService
{
    /// <summary>The address handed to containers; <c>null</c> means "no forwarder available".</summary>
    public IPAddress? Address { get; set; } = IPAddress.Parse("192.168.64.53");

    /// <summary>The networks the daemon asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>The networks whose forwarder was released, in order.</summary>
    public List<string> Released { get; } = [];

    /// <inheritdoc />
    public Task<IPAddress?> EnsureAsync(string dockerNetworkName, CancellationToken ct)
    {
        lock (Requested)
        {
            Requested.Add(dockerNetworkName);
        }

        return Task.FromResult(Address);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always reports a forwarder was torn down (<c>true</c>) — tests that need the "nothing was
    /// there to release" outcome swap the network's forwarder for <see
    /// cref="NullDnsForwarderService"/> instead of adding tracking state here.
    /// </remarks>
    public Task<bool> ReleaseAsync(string dockerNetworkName, CancellationToken ct)
    {
        lock (Released)
        {
            Released.Add(dockerNetworkName);
        }

        return Task.FromResult(true);
    }
}
