using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `NetworkConfiguration` (`NetworkConfiguration.swift:21-125`) — has a custom `init(from:)` that
/// tolerates missing keys and accepts <c>id</c> as an alias for <see cref="Name"/> and <c>subnet</c>
/// as an alias for <see cref="Ipv4Subnet"/> (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2) —
/// <see cref="NetworkConfigurationConverter"/> implements both on decode; encode always writes the
/// canonical keys, matching what the Swift encoder emits.
/// </summary>
[JsonConverter(typeof(NetworkConfigurationConverter))]
internal sealed class NetworkConfiguration
{
    /// <summary>Must match <c>^[a-z0-9](?:[a-z0-9._-]{0,61}[a-z0-9])?$</c> (`NetworkResource.swift:36-39`).</summary>
    public required string Name { get; init; }

    [JsonConverter(typeof(AppleReferenceDateConverter))]
    public DateTimeOffset CreationDate { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary><c>NetworkMode</c> — a plain string raw-value enum (§2.0 rule 4): <c>"nat"</c> or
    /// <c>"hostOnly"</c>.</summary>
    public required string Mode { get; init; }

    public string? Ipv4Subnet { get; init; }

    public string? Ipv6Subnet { get; init; }

    public Dictionary<string, string> Labels { get; init; } = [];

    public string? Plugin { get; init; }

    public Dictionary<string, string> Options { get; init; } = [];
}

/// <summary>`NetworkResource` (`NetworkResource.swift:20-68`) — encodes <c>{id, configuration,
/// status}</c>, but the decoder ignores <c>id</c> (`:63-67`); this model has no `Id` property so
/// System.Text.Json naturally drops it on both sides.</summary>
internal sealed class NetworkResource
{
    public required NetworkConfiguration Configuration { get; init; }

    public required NetworkStatus Status { get; init; }
}

/// <summary>`NetworkStatus` (`NetworkStatus.swift:19-35`) — <see cref="Ipv4Subnet"/> and
/// <see cref="Ipv4Gateway"/> required, <see cref="Ipv6Subnet"/> optional.</summary>
internal sealed class NetworkStatus
{
    public required string Ipv4Subnet { get; init; }

    public required string Ipv4Gateway { get; init; }

    public string? Ipv6Subnet { get; init; }
}
