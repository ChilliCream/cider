using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `VolumeConfiguration` (`VolumeConfiguration.swift:19-56` + Codable `:57-84`).
/// <see cref="Name"/>, <see cref="Driver"/>, <see cref="Format"/> and <see cref="Source"/> are
/// required; name must match <c>^[A-Za-z0-9][A-Za-z0-9_.-]*$</c>, ≤255 chars (`:120-124`)
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2). The anonymous-volume marker is the label
/// <c>com.apple.container.resource.anonymous</c> (`:87`).
/// </summary>
internal sealed class VolumeConfiguration
{
    public required string Name { get; init; }

    public required string Driver { get; init; }

    public required string Format { get; init; }

    public required string Source { get; init; }

    [JsonConverter(typeof(AppleReferenceDateConverter))]
    public DateTimeOffset CreationDate { get; init; } = DateTimeOffset.UnixEpoch;

    public Dictionary<string, string> Labels { get; init; } = [];

    public Dictionary<string, string> Options { get; init; } = [];

    public ulong? SizeInBytes { get; init; }
}

/// <summary>`VolumeResource` (`VolumeResource.swift:19-73`) — encodes <c>{id, configuration}</c>,
/// decoder ignores <c>id</c>; this model has no `Id` property so it round-trips without one.</summary>
internal sealed class VolumeResource
{
    public required VolumeConfiguration Configuration { get; init; }
}
