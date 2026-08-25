using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `ContainerSnapshot` (`ContainerSnapshot.swift:20-46`) — the `containerList` reply element
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2, §8.2). `id`/`platform` are computed on the
/// Swift side and never appear on the wire. Synthesized Codable: <see cref="Configuration"/>,
/// <see cref="Status"/> and <see cref="Networks"/> are required (§2.0 rule 11).
/// </summary>
internal sealed class ContainerSnapshot
{
    public required ContainerConfiguration Configuration { get; init; }

    public required RuntimeStatus Status { get; init; }

    public required List<Attachment> Networks { get; init; }

    [JsonConverter(typeof(AppleReferenceDateConverter))]
    public DateTimeOffset? StartedDate { get; init; }
}

/// <summary>
/// `Attachment` (`Attachment.swift:19-95`), an entry in <see cref="ContainerSnapshot.Networks"/>.
/// Has a custom `init(from:)` that tolerates missing keys (§2.0 rule 11) and also accepts the legacy
/// `address`/`gateway` keys as aliases for <see cref="Ipv4Address"/>/<see cref="Ipv4Gateway"/>
/// (`Attachment.swift:66-75`) — <see cref="AttachmentConverter"/> implements both.
/// </summary>
[JsonConverter(typeof(AttachmentConverter))]
internal sealed class Attachment
{
    public required string Network { get; init; }

    public required string Hostname { get; init; }

    /// <summary><c>CIDRv4</c> — a bare string (§2.0 rule 7), e.g. <c>"192.168.64.2/24"</c>.</summary>
    public string? Ipv4Address { get; init; }

    /// <summary><c>IPv4Address</c> — a bare string, e.g. <c>"192.168.64.1"</c>.</summary>
    public string? Ipv4Gateway { get; init; }

    public string? Ipv6Address { get; init; }

    public string? MacAddress { get; init; }

    public uint? Mtu { get; init; }

    public string? Variant { get; init; }
}

/// <summary>`RuntimeStatus` — a `String` raw-value enum, so it's a plain string on the wire (§2.0
/// rule 4): <c>"stopped"|"running"|"stopping"|"unknown"</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeStatus>))]
internal enum RuntimeStatus
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("stopped")]
    Stopped,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("stopping")]
    Stopping,
}
