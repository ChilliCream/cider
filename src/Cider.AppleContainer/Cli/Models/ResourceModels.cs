namespace Cider.AppleContainer.Cli.Models;

// `container network ls|inspect --format json` and `container volume ls|inspect --format json`
// (docs/apple-container-notes.md §1, §6b, §8).

internal sealed class AppleNetworkJson
{
    public AppleNetworkConfiguration? Configuration { get; set; }

    public string? Id { get; set; }

    public AppleNetworkStatus? Status { get; set; }
}

internal sealed class AppleNetworkConfiguration
{
    public string? Name { get; set; }

    /// <summary>Apple's network mode, e.g. <c>nat</c>.</summary>
    public string? Mode { get; set; }

    public string? Plugin { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public Dictionary<string, string>? Options { get; set; }

    public DateTimeOffset? CreationDate { get; set; }

    public bool? Internal { get; set; }

    public string? Subnet { get; set; }
}

internal sealed class AppleNetworkStatus
{
    public string? Ipv4Gateway { get; set; }

    public string? Ipv4Subnet { get; set; }

    public string? Ipv6Subnet { get; set; }
}

internal sealed class AppleVolumeJson
{
    public AppleVolumeConfiguration? Configuration { get; set; }

    public string? Id { get; set; }
}

internal sealed class AppleVolumeConfiguration
{
    public string? Name { get; set; }

    public string? Driver { get; set; }

    public string? Format { get; set; }

    /// <summary>Path of the backing <c>volume.img</c> on the host.</summary>
    public string? Source { get; set; }

    public long? SizeInBytes { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public Dictionary<string, string>? Options { get; set; }

    public DateTimeOffset? CreationDate { get; set; }
}
