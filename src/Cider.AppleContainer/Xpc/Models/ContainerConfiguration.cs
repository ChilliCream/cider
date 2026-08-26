using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Xpc.Models;

// Wire models for the apiserver's Swift `Codable` JSON blobs (docs/spikes/xpc/02-apiserver-xpc-protocol.md
// §2.0-2.2). These mirror `ContainerConfiguration.swift` field-for-field, NOT the CLI's
// `--format json` display rendering (that's Cli/Models/ContainerModels.cs — ISO dates, different
// optionality). Every property name must convert to its verbatim Swift key under
// JsonNamingPolicy.CamelCase (XpcJsonContext); see XpcJson.cs for the strict, case-sensitive options.

/// <summary>
/// `ContainerConfiguration` (`ContainerConfiguration.swift:20-158`). Has a custom `init(from:)` that
/// only requires <see cref="Id"/>, <see cref="Image"/> and <see cref="InitProcess"/> (§2.0 rule 11) —
/// every other member below carries the Swift decoder's default, applied here as the C# default too.
/// NOTE: the defaulted members below use a plain <c>set</c> accessor, not <c>init</c> — with
/// System.Text.Json source generation, an `init`-only property whose JSON key is absent is left at
/// <c>default(T)</c> instead of running its declared field-initializer default (a source-gen-only
/// quirk; reflection-based serialization does not have it). <c>set</c> does not have that quirk, so
/// it is what makes an omitted key actually decode to the Swift default instead of `null`/`0`/`false`.
/// See <see cref="Resources"/> and <see cref="PublishPort"/> below for the same fix, and
/// <c>VolumeConfiguration</c> in VolumeModels.cs.
/// </summary>
internal sealed class ContainerConfiguration
{
    public required string Id { get; init; }

    public required ImageDescription Image { get; init; }

    public required ProcessConfiguration InitProcess { get; init; }

    public List<Filesystem> Mounts { get; set; } = [];

    public List<PublishPort> PublishedPorts { get; set; } = [];

    public List<PublishSocket> PublishedSockets { get; set; } = [];

    public Dictionary<string, string> Labels { get; set; } = [];

    public Dictionary<string, string> Sysctls { get; set; } = [];

    public List<AttachmentConfiguration> Networks { get; set; } = [];

    public DnsConfiguration? Dns { get; init; }

    public bool Rosetta { get; set; }

    public Platform Platform { get; set; } = Platform.Current;

    public Resources Resources { get; set; } = new();

    public string RuntimeHandler { get; set; } = "container-runtime-linux";

    public bool Virtualization { get; set; }

    public bool Ssh { get; set; }

    public bool ReadOnly { get; set; }

    public bool UseInit { get; set; }

    public List<string> CapAdd { get; set; } = [];

    public List<string> CapDrop { get; set; } = [];

    public ulong? ShmSize { get; init; }

    public string? StopSignal { get; init; }

    /// <summary>New in 1.2.x (§7); absent on an older apiserver.</summary>
    public List<string>? MaskedPaths { get; init; }

    public List<string>? ReadonlyPaths { get; init; }

    /// <summary>Epoch 1970 (Unix zero) if absent on decode, per the Swift custom `init(from:)`.</summary>
    [JsonConverter(typeof(AppleReferenceDateConverter))]
    public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Reproduces the `container` CLI's own defaults for a plain create
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2: <c>Resources</c> 4 CPU / 1 GiB /
    /// cpuOverhead 1; <c>runtimeHandler "container-runtime-linux"</c>; <c>platform .current</c>;
    /// <c>dns.nameservers ["1.1.1.1"]</c>, §2.2's <c>DNSConfiguration</c> default).
    /// </summary>
    public static ContainerConfiguration Defaults(string id, ImageDescription image, ProcessConfiguration initProcess) =>
        new()
        {
            Id = id,
            Image = image,
            InitProcess = initProcess,
            Resources = new Resources { Cpus = 4, MemoryInBytes = 1024UL * 1024 * 1024, CpuOverhead = 1 },
            RuntimeHandler = "container-runtime-linux",
            Platform = Platform.Current,
            Dns = new DnsConfiguration { Nameservers = ["1.1.1.1"], SearchDomains = [], Options = [] },
        };
}

/// <summary>`ProcessConfiguration` (`ProcessConfiguration.swift:17-72`) — synthesized Codable, all 8
/// fields required on decode (§2.0 rule 11).</summary>
internal sealed class ProcessConfiguration
{
    public required string Executable { get; init; }

    public required List<string> Arguments { get; init; }

    public required List<string> Environment { get; init; }

    public required string WorkingDirectory { get; init; }

    public required bool Terminal { get; init; }

    public required User User { get; init; }

    public required List<uint> SupplementalGroups { get; init; }

    public required List<Rlimit> Rlimits { get; init; }
}

/// <summary>Swift enum with associated values → single-key-object form (§2.0 rule 3):
/// <c>{"id":{"uid":0,"gid":0}}</c> or <c>{"raw":{"userString":"65532:65532"}}</c>.</summary>
[JsonConverter(typeof(SingleKeyUnionConverter<User>))]
internal sealed class User
{
    public UserId? Id { get; init; }

    public UserRaw? Raw { get; init; }

    public static User OfId(int uid, int gid) => new() { Id = new UserId { Uid = uid, Gid = gid } };

    public static User OfRaw(string userString) => new() { Raw = new UserRaw { UserString = userString } };
}

internal sealed class UserId
{
    public required int Uid { get; init; }

    public required int Gid { get; init; }
}

internal sealed class UserRaw
{
    public required string UserString { get; init; }
}

/// <summary>`Rlimit` (`ProcessConfiguration.swift:28-38`).</summary>
internal sealed class Rlimit
{
    public required string Limit { get; init; }

    public required ulong Soft { get; init; }

    public required ulong Hard { get; init; }
}

/// <summary>`ContainerConfiguration.Resources` (`ContainerConfiguration.swift:132-147`) — all optional
/// on decode, each with the Swift-side default reproduced here (§2.0 rule 11, §2.2). <c>set</c>, not
/// <c>init</c> — see the note on <see cref="ContainerConfiguration"/> above.</summary>
internal sealed class Resources
{
    public int Cpus { get; set; } = 4;

    public ulong MemoryInBytes { get; set; } = 1024UL * 1024 * 1024;

    public ulong? Storage { get; init; }

    public int CpuOverhead { get; set; } = 1;
}

/// <summary>`DNSConfiguration` (`ContainerConfiguration.swift:111-130`) — synthesized Codable:
/// <see cref="Nameservers"/>, <see cref="SearchDomains"/> and <see cref="Options"/> are required on
/// decode (§2.0 rule 11); <see cref="Domain"/> is omitted from the wire when null (rule 10).</summary>
internal sealed class DnsConfiguration
{
    public required List<string> Nameservers { get; init; }

    public string? Domain { get; init; }

    public required List<string> SearchDomains { get; init; }

    public required List<string> Options { get; init; }
}

/// <summary>`AttachmentConfiguration` (`AttachmentConfiguration.swift:19-42`) — synthesized Codable,
/// <see cref="Network"/> required.</summary>
internal sealed class AttachmentConfiguration
{
    public required string Network { get; init; }

    public required AttachmentOptions Options { get; init; }
}

/// <summary>Nested `options` object of <see cref="AttachmentConfiguration"/> — synthesized Codable,
/// <see cref="Hostname"/> required (§2.0 rule 11).</summary>
internal sealed class AttachmentOptions
{
    public required string Hostname { get; init; }

    public string? MacAddress { get; init; }

    public uint? Mtu { get; init; }
}

/// <summary>`PublishPort` (`PublishPort.swift:37-81`) — custom `init(from:)`; <see cref="Count"/>
/// defaults to 1 when absent (§2.2), everything else the CLI always supplies. <c>Count</c> uses
/// <c>set</c>, not <c>init</c> — see the note on <see cref="ContainerConfiguration"/> above.</summary>
internal sealed class PublishPort
{
    /// <summary>`IPAddress` on the wire — a bare string (§2.0 rule 7, e.g. <c>"0.0.0.0"</c>).</summary>
    public required string HostAddress { get; init; }

    public required ushort HostPort { get; init; }

    public required ushort ContainerPort { get; init; }

    /// <summary><c>PublishProtocol</c> — a plain string raw-value enum (§2.0 rule 4): <c>"tcp"</c> or
    /// <c>"udp"</c>.</summary>
    public required string Proto { get; init; }

    public ushort Count { get; set; } = 1;
}

/// <summary>`PublishSocket` (`PublishSocket.swift:21-129`) — `FilePath` fields encode as plain
/// strings (§2.0 rule 8).</summary>
internal sealed class PublishSocket
{
    public required string ContainerPath { get; init; }

    public required string HostPath { get; init; }

    public int? Permissions { get; init; }
}

/// <summary>`Filesystem` (`Filesystem.swift:28-157`) — synthesized Codable, all 4 fields required on
/// decode (§2.0 rule 11).</summary>
internal sealed class Filesystem
{
    public required FsType Type { get; init; }

    public required string Source { get; init; }

    public required string Destination { get; init; }

    /// <summary>A plain string array; <c>"ro"</c> marks read-only (`Filesystem.swift:22-26`).</summary>
    public required List<string> Options { get; init; }
}

/// <summary>`Filesystem.FSType` (`Filesystem.swift:41-51`) — single-key-object union (§2.0 rule 3):
/// <c>{"virtiofs":{}}</c>, <c>{"tmpfs":{}}</c>,
/// <c>{"block":{"format":"ext4","cache":{"on":{}},"sync":{"fsync":{}}}}</c>,
/// <c>{"volume":{"name":…,"format":…,"cache":{…},"sync":{…}}}</c>.</summary>
[JsonConverter(typeof(SingleKeyUnionConverter<FsType>))]
internal sealed class FsType
{
    public EmptyPayload? Virtiofs { get; init; }

    public EmptyPayload? Tmpfs { get; init; }

    public BlockFs? Block { get; init; }

    public VolumeFs? Volume { get; init; }

    public static FsType OfVirtiofs() => new() { Virtiofs = EmptyPayload.Instance };

    public static FsType OfTmpfs() => new() { Tmpfs = EmptyPayload.Instance };

    public static FsType OfBlock(BlockFs block) => new() { Block = block };

    public static FsType OfVolume(VolumeFs volume) => new() { Volume = volume };
}

/// <summary>The <c>block</c> case's payload. <see cref="Cache"/>/<see cref="Sync"/> are themselves
/// single-key-object enums (confirmed samples: <c>{"on":{}}</c>, <c>{"fsync":{}}</c>) whose full case
/// set the spike did not enumerate — <see cref="SingleKeyCase"/> preserves whatever key name the
/// daemon sends instead of guessing at the rest.</summary>
internal sealed class BlockFs
{
    public required string Format { get; init; }

    public required SingleKeyCase Cache { get; init; }

    public required SingleKeyCase Sync { get; init; }
}

/// <summary>The <c>volume</c> case's payload — same shape as <see cref="BlockFs"/> plus a volume
/// <see cref="Name"/>.</summary>
internal sealed class VolumeFs
{
    public required string Name { get; init; }

    public required string Format { get; init; }

    public required SingleKeyCase Cache { get; init; }

    public required SingleKeyCase Sync { get; init; }
}

/// <summary>Placeholder for a payload-free union case — serializes as <c>{}</c>, never <c>""</c>
/// (§2.0 rule 3).</summary>
internal sealed class EmptyPayload
{
    public static readonly EmptyPayload Instance = new();
}

/// <summary>`ImageDescription` (`Image/ImageDescription.swift:20-31`) — synthesized Codable, both
/// fields required.</summary>
internal sealed class ImageDescription
{
    public required string Reference { get; init; }

    public required Descriptor Descriptor { get; init; }
}

/// <summary>`ContainerizationOCI.Descriptor` — live sample carries `mediaType`/`digest`/`size`
/// required, plus optional `urls`/`annotations`/`platform` (§2.2).</summary>
internal sealed class Descriptor
{
    public required string MediaType { get; init; }

    public required string Digest { get; init; }

    public required long Size { get; init; }

    public List<string>? Urls { get; init; }

    public Dictionary<string, string>? Annotations { get; init; }

    public Platform? Platform { get; init; }
}

/// <summary>`ContainerizationOCI.Platform` — `os`/`architecture` required, `variant` optional (seen
/// live as <c>"v8"</c> on arm64, docs/spikes/xpc-probe/out-list.txt).</summary>
internal sealed class Platform
{
    public required string Os { get; init; }

    public required string Architecture { get; init; }

    public string? Variant { get; init; }

    /// <summary>The host's own platform, linux-side — what a freshly-composed
    /// <see cref="ContainerConfiguration"/> defaults to (§2.2, mirrors
    /// `ClientKernel.swift:100-111`'s arm64→linuxArm / amd64→linuxAmd mapping).</summary>
    public static Platform Current => new()
    {
        Os = "linux",
        Architecture = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64",
    };
}
