using System.Text.Json.Serialization;
using Cider.AppleContainer.Xpc.Models;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// The source-generated contracts for the apiserver's XPC wire JSON (docs/spikes/xpc/02-apiserver-xpc-protocol.md
/// §2). Deliberately stricter than <c>Cli.AppleJsonContext</c>: case-sensitive property matching and
/// camelCase names only (no <c>Web</c> defaults, no number-from-string coercion) — the wire is the
/// exact output of a Swift synthesized/custom <c>Codable</c>, not a tolerant CLI display parse.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ContainerConfiguration))]
[JsonSerializable(typeof(ProcessConfiguration))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(UserId))]
[JsonSerializable(typeof(UserRaw))]
[JsonSerializable(typeof(Rlimit))]
[JsonSerializable(typeof(Resources))]
[JsonSerializable(typeof(DnsConfiguration))]
[JsonSerializable(typeof(AttachmentConfiguration))]
[JsonSerializable(typeof(AttachmentOptions))]
[JsonSerializable(typeof(PublishPort))]
[JsonSerializable(typeof(PublishSocket))]
[JsonSerializable(typeof(Filesystem))]
[JsonSerializable(typeof(FsType))]
[JsonSerializable(typeof(BlockFs))]
[JsonSerializable(typeof(VolumeFs))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(SingleKeyCase))]
[JsonSerializable(typeof(ImageDescription))]
[JsonSerializable(typeof(Descriptor))]
[JsonSerializable(typeof(Platform))]
[JsonSerializable(typeof(ContainerSnapshot))]
[JsonSerializable(typeof(Attachment))]
[JsonSerializable(typeof(RuntimeStatus))]
[JsonSerializable(typeof(ContainerListFilters))]
[JsonSerializable(typeof(ContainerStopOptions))]
[JsonSerializable(typeof(ContainerCreateOptions))]
[JsonSerializable(typeof(ContainerStats))]
[JsonSerializable(typeof(Kernel))]
[JsonSerializable(typeof(SystemPlatform))]
[JsonSerializable(typeof(CommandLine))]
[JsonSerializable(typeof(NetworkConfiguration))]
[JsonSerializable(typeof(NetworkResource))]
[JsonSerializable(typeof(NetworkStatus))]
[JsonSerializable(typeof(VolumeConfiguration))]
[JsonSerializable(typeof(VolumeResource))]
[JsonSerializable(typeof(DiskUsageStats))]
[JsonSerializable(typeof(ResourceUsage))]
// Reply/request shapes that are JSON arrays or bare collections at the top level.
[JsonSerializable(typeof(List<ContainerSnapshot>))]
[JsonSerializable(typeof(List<NetworkResource>))]
[JsonSerializable(typeof(List<VolumeConfiguration>))]
[JsonSerializable(typeof(List<ImageDescription>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class XpcJsonContext : JsonSerializerContext;
