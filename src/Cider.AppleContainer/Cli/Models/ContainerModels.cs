using System.Text.Json.Serialization;

namespace Cider.AppleContainer.Cli.Models;

// Shapes of `container ls -a --format json` / `container inspect <id>` on Apple container 1.2.2
// (docs/apple-container-notes.md §3, §6, §13). Both return an array of this object.
// Every member is optional: the CLI omits empty/unset fields.

internal sealed class AppleContainerJson
{
    public AppleContainerConfiguration? Configuration { get; set; }

    public string? Id { get; set; }

    public AppleContainerStatus? Status { get; set; }
}

internal sealed class AppleContainerConfiguration
{
    public string? Id { get; set; }

    public AppleImageReference? Image { get; set; }

    public AppleInitProcess? InitProcess { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public List<AppleMount>? Mounts { get; set; }

    public List<AppleNetworkRequest>? Networks { get; set; }

    public AplePlatform? Platform { get; set; }

    public List<ApplePublishedPort>? PublishedPorts { get; set; }

    public List<ApplePublishedSocket>? PublishedSockets { get; set; }

    public AppleResources? Resources { get; set; }

    public AppleDnsConfiguration? Dns { get; set; }

    public List<string>? CapAdd { get; set; }

    public List<string>? CapDrop { get; set; }

    public bool? ReadOnly { get; set; }

    public bool? Rosetta { get; set; }

    public bool? Ssh { get; set; }

    public bool? UseInit { get; set; }

    public bool? Virtualization { get; set; }

    public string? RuntimeHandler { get; set; }

    public Dictionary<string, string>? Sysctls { get; set; }

    public DateTimeOffset? CreationDate { get; set; }
}

internal sealed class AppleContainerStatus
{
    /// <summary><c>running</c> / <c>stopped</c> / <c>stopping</c>. Apple has no <c>created</c> state.</summary>
    public string? State { get; set; }

    public List<AppleNetworkAttachment>? Networks { get; set; }

    public DateTimeOffset? StartedDate { get; set; }
}

internal sealed class AppleImageReference
{
    public string? Reference { get; set; }

    public AppleDescriptor? Descriptor { get; set; }
}

internal sealed class AppleDescriptor
{
    public string? Digest { get; set; }

    public string? MediaType { get; set; }

    public long? Size { get; set; }
}

internal sealed class AppleInitProcess
{
    public string? Executable { get; set; }

    public List<string>? Arguments { get; set; }

    public List<string>? Environment { get; set; }

    public string? WorkingDirectory { get; set; }

    public bool? Terminal { get; set; }

    public AppleUser? User { get; set; }

    public List<AppleRlimit>? Rlimits { get; set; }

    public List<string>? SupplementalGroups { get; set; }
}

/// <summary>Discriminated: <c>{"id":{…}}</c> when resolved, <c>{"raw":{"userString":…}}</c> when <c>-u</c> was a string.</summary>
internal sealed class AppleUser
{
    public AppleUserId? Id { get; set; }

    public AppleUserRaw? Raw { get; set; }
}

internal sealed class AppleUserId
{
    public int? Uid { get; set; }

    public int? Gid { get; set; }
}

internal sealed class AppleUserRaw
{
    public string? UserString { get; set; }
}

internal sealed class AppleRlimit
{
    public string? Limit { get; set; }

    public long? Soft { get; set; }

    public long? Hard { get; set; }
}

internal sealed class AppleMount
{
    public string? Destination { get; set; }

    public string? Source { get; set; }

    public List<string>? Options { get; set; }

    public AppleMountType? Type { get; set; }
}

/// <summary>Discriminated by the single present key: <c>volume</c> / <c>virtiofs</c> (bind) / <c>tmpfs</c>.</summary>
internal sealed class AppleMountType
{
    public AppleVolumeMount? Volume { get; set; }

    public AppleEmptyObject? Virtiofs { get; set; }

    public AppleEmptyObject? Tmpfs { get; set; }
}

internal sealed class AppleVolumeMount
{
    public string? Name { get; set; }

    public string? Format { get; set; }
}

/// <summary>Placeholder for the CLI's empty discriminator objects (<c>{}</c>).</summary>
internal sealed class AppleEmptyObject;

internal sealed class AppleNetworkRequest
{
    public string? Network { get; set; }

    public AppleNetworkRequestOptions? Options { get; set; }
}

internal sealed class AppleNetworkRequestOptions
{
    public string? Hostname { get; set; }

    public int? Mtu { get; set; }

    public string? Mac { get; set; }
}

internal sealed class AppleNetworkAttachment
{
    public string? Network { get; set; }

    public string? Hostname { get; set; }

    /// <summary>Carries a CIDR suffix (<c>192.168.64.20/24</c>) that must be stripped.</summary>
    public string? Ipv4Address { get; set; }

    public string? Ipv4Gateway { get; set; }

    public string? Ipv6Address { get; set; }

    public string? MacAddress { get; set; }

    public int? Mtu { get; set; }

    public string? Variant { get; set; }
}

internal sealed class AplePlatform
{
    public string? Os { get; set; }

    public string? Architecture { get; set; }

    public string? Variant { get; set; }
}

internal sealed class ApplePublishedPort
{
    public int? ContainerPort { get; set; }

    public int? HostPort { get; set; }

    public string? HostAddress { get; set; }

    public string? Proto { get; set; }

    public int? Count { get; set; }
}

internal sealed class ApplePublishedSocket
{
    public string? HostPath { get; set; }

    public string? ContainerPath { get; set; }
}

internal sealed class AppleResources
{
    public double? Cpus { get; set; }

    public long? MemoryInBytes { get; set; }

    public double? CpuOverhead { get; set; }
}

internal sealed class AppleDnsConfiguration
{
    public List<string>? Nameservers { get; set; }

    public List<string>? Options { get; set; }

    public List<string>? SearchDomains { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }
}
