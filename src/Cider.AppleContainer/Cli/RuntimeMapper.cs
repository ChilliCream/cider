using Cider.AppleContainer.Cli.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Cli;

/// <summary>Maps the CLI's JSON documents onto <c>Cider.Core.Runtime</c> models.</summary>
internal static class RuntimeMapper
{
    private static readonly Dictionary<string, string> EmptyLabels = new(StringComparer.Ordinal);

    public static RuntimeContainer ToContainer(AppleContainerJson json)
    {
        var configuration = json.Configuration;
        var status = json.Status;
        var init = configuration?.InitProcess;

        var argv = new List<string>();
        if (!string.IsNullOrEmpty(init?.Executable))
        {
            argv.Add(init.Executable);
        }

        if (init?.Arguments is { Count: > 0 })
        {
            argv.AddRange(init.Arguments);
        }

        return new RuntimeContainer
        {
            RuntimeId = json.Id ?? configuration?.Id ?? "",
            State = ToState(status?.State),
            ImageReference = configuration?.Image?.Reference ?? "",
            ImageDigest = configuration?.Image?.Descriptor?.Digest,
            Labels = configuration?.Labels is { Count: > 0 }
                ? new Dictionary<string, string>(configuration.Labels, StringComparer.Ordinal)
                : EmptyLabels,
            Networks = ToNetworkAttachments(configuration, status),
            PublishedPorts = ToPorts(configuration?.PublishedPorts),
            Mounts = ToMounts(configuration?.Mounts),
            Platform = ToPlatform(configuration?.Platform),
            Argv = argv,
            Env = init?.Environment is { Count: > 0 } ? [.. init.Environment] : [],
            WorkingDir = init?.WorkingDirectory,
            Tty = init?.Terminal ?? false,
            Cpus = configuration?.Resources?.Cpus,
            MemoryBytes = configuration?.Resources?.MemoryInBytes,
            CreatedAt = configuration?.CreationDate,
            StartedAt = status?.StartedDate,
        };
    }

    /// <summary>Apple reports only <c>running</c>/<c>stopping</c>/<c>stopped</c>; there is no <c>created</c>.</summary>
    public static RuntimeContainerState ToState(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => RuntimeContainerState.Running,
        "stopping" => RuntimeContainerState.Stopping,
        "stopped" => RuntimeContainerState.Stopped,
        _ => RuntimeContainerState.Unknown,
    };

    public static IReadOnlyList<RuntimeNetworkAttachment> ToNetworkAttachments(
        AppleContainerConfiguration? configuration,
        AppleContainerStatus? status)
    {
        if (status?.Networks is { Count: > 0 })
        {
            var live = new List<RuntimeNetworkAttachment>(status.Networks.Count);
            foreach (var attachment in status.Networks)
            {
                live.Add(new RuntimeNetworkAttachment
                {
                    Network = attachment.Network ?? "",
                    Hostname = attachment.Hostname,
                    IPv4Address = StripCidr(attachment.Ipv4Address),
                    IPv4Gateway = StripCidr(attachment.Ipv4Gateway),
                    MacAddress = attachment.MacAddress,
                });
            }

            return live;
        }

        if (configuration?.Networks is { Count: > 0 })
        {
            var requested = new List<RuntimeNetworkAttachment>(configuration.Networks.Count);
            foreach (var network in configuration.Networks)
            {
                requested.Add(new RuntimeNetworkAttachment
                {
                    Network = network.Network ?? "",
                    Hostname = network.Options?.Hostname,
                });
            }

            return requested;
        }

        return [];
    }

    public static IReadOnlyList<PortSpec> ToPorts(List<ApplePublishedPort>? ports)
    {
        if (ports is not { Count: > 0 })
        {
            return [];
        }

        var mapped = new List<PortSpec>(ports.Count);
        foreach (var port in ports)
        {
            mapped.Add(new PortSpec
            {
                HostIp = port.HostAddress ?? "",
                HostPort = port.HostPort ?? 0,
                ContainerPort = port.ContainerPort ?? 0,
                Proto = string.IsNullOrEmpty(port.Proto) ? "tcp" : port.Proto,
            });
        }

        return mapped;
    }

    public static IReadOnlyList<MountSpec> ToMounts(List<AppleMount>? mounts)
    {
        if (mounts is not { Count: > 0 })
        {
            return [];
        }

        var mapped = new List<MountSpec>(mounts.Count);
        foreach (var mount in mounts)
        {
            var readOnly = mount.Options is not null &&
                mount.Options.Exists(o =>
                    string.Equals(o, "ro", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o, "readonly", StringComparison.OrdinalIgnoreCase));

            if (mount.Type?.Volume is { } volume)
            {
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Volume,
                    Source = volume.Name ?? mount.Source ?? "",
                    Target = mount.Destination ?? "",
                    ReadOnly = readOnly,
                });
            }
            else if (mount.Type?.Tmpfs is not null)
            {
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Tmpfs,
                    Source = "",
                    Target = mount.Destination ?? "",
                    ReadOnly = readOnly,
                });
            }
            else
            {
                // `virtiofs` (and anything unknown) is a host bind mount.
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Bind,
                    Source = mount.Source ?? "",
                    Target = mount.Destination ?? "",
                    ReadOnly = readOnly,
                });
            }
        }

        return mapped;
    }

    public static string? ToPlatform(AplePlatform? platform)
    {
        if (platform is null || string.IsNullOrEmpty(platform.Os) || string.IsNullOrEmpty(platform.Architecture))
        {
            return null;
        }

        return string.IsNullOrEmpty(platform.Variant)
            ? $"{platform.Os}/{platform.Architecture}"
            : $"{platform.Os}/{platform.Architecture}/{platform.Variant}";
    }

    /// <summary><c>192.168.64.20/24</c> → <c>192.168.64.20</c>.</summary>
    public static string? StripCidr(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return address;
        }

        var slash = address.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? address : address[..slash];
    }

    /// <summary>
    /// Apple prints one <c>image ls</c> row per <i>reference</i>: two tags of the same image are two
    /// top-level entries sharing one <c>id</c> (docs/apple-container-notes.md §2). Docker's shape is
    /// one image per digest carrying every reference, so the rows are grouped by id here — the merged
    /// <see cref="RuntimeImage.References"/> is the union in first-seen order, everything else comes
    /// from the first row of the group (all rows of a digest describe the same variants).
    /// </summary>
    public static IReadOnlyList<RuntimeImage> ToImages(IEnumerable<AppleImageJson> rows)
    {
        var merged = new List<RuntimeImage>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var image = ToImage(row);
            if (image.Id.Length == 0)
            {
                // No id to group on: keep the row as its own image rather than merging blindly.
                merged.Add(image);
                continue;
            }

            if (!byId.TryGetValue(image.Id, out var index))
            {
                byId[image.Id] = merged.Count;
                merged.Add(image);
                continue;
            }

            merged[index] = merged[index] with { References = MergeReferences(merged[index].References, image.References) };
        }

        return merged;
    }

    /// <summary><paramref name="first"/> followed by the entries of <paramref name="extra"/> it does not already hold.</summary>
    public static IReadOnlyList<string> MergeReferences(IReadOnlyList<string> first, IReadOnlyList<string> extra)
    {
        List<string>? references = null;
        foreach (var candidate in extra)
        {
            if (first.Contains(candidate, StringComparer.Ordinal) ||
                references?.Contains(candidate, StringComparer.Ordinal) == true)
            {
                continue;
            }

            references ??= [.. first];
            references.Add(candidate);
        }

        return references ?? first;
    }

    public static RuntimeImage ToImage(AppleImageJson json)
    {
        var variants = RealVariants(json);
        long size = 0;
        var platforms = new List<string>();
        foreach (var variant in variants)
        {
            size += variant.Size ?? 0;
            var platform = ToPlatform(variant.Platform);
            if (platform is not null && !platforms.Contains(platform, StringComparer.Ordinal))
            {
                platforms.Add(platform);
            }
        }

        var preferred = PickVariant(json, null);
        return new RuntimeImage
        {
            Id = ToImageId(json.Id),
            References = References(json),
            Size = size,
            Created = preferred?.Config?.Created ?? json.Configuration?.CreationDate,
            Platforms = platforms,
            Labels = preferred?.Config?.Config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
        };
    }

    public static RuntimeImageDetail? ToImageDetail(AppleImageJson json, string? platform)
    {
        var variant = PickVariant(json, platform);
        if (variant is null)
        {
            return null;
        }

        var document = variant.Config;
        var config = document?.Config;
        var summary = ToImage(json);

        return new RuntimeImageDetail
        {
            Id = summary.Id,
            References = summary.References,
            Size = variant.Size ?? summary.Size,
            Created = document?.Created ?? summary.Created,
            Platforms = summary.Platforms,
            Labels = config?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            Architecture = document?.Architecture ?? variant.Platform?.Architecture ?? "",
            Os = document?.Os ?? variant.Platform?.Os ?? "",
            Variant = document?.Variant ?? variant.Platform?.Variant,
            Layers = document?.Rootfs?.DiffIds is { Count: > 0 } layers ? [.. layers] : [],
            Author = document?.Author,
            RepoDigests = RepoDigests(json),
            History = document?.History is { Count: > 0 } history
                ?
                [
                    .. history.Select(entry => new RuntimeImageHistory
                    {
                        Created = entry.Created,
                        CreatedBy = entry.CreatedBy ?? "",
                        Comment = entry.Comment ?? "",
                        EmptyLayer = entry.EmptyLayer,
                    }),
                ]
                : [],
            Config = ToImageConfig(config),
        };
    }

    public static ImageConfig ToImageConfig(AppleOciConfig? config)
    {
        if (config is null)
        {
            return new ImageConfig();
        }

        return new ImageConfig
        {
            Env = config.Env is { Count: > 0 } ? [.. config.Env] : [],
            Cmd = config.Cmd is { Count: > 0 } ? [.. config.Cmd] : [],
            Entrypoint = config.Entrypoint is { Count: > 0 } ? [.. config.Entrypoint] : [],
            WorkingDir = config.WorkingDir,
            User = config.User,
            ExposedPorts = config.ExposedPorts is { Count: > 0 } ? [.. config.ExposedPorts.Keys] : [],
            Volumes = config.Volumes is { Count: > 0 } ? [.. config.Volumes.Keys] : [],
            Labels = config.Labels is { Count: > 0 }
                ? new Dictionary<string, string>(config.Labels, StringComparer.Ordinal)
                : EmptyLabels,
            StopSignal = config.StopSignal,
            Healthcheck = config.Healthcheck is null || config.Healthcheck.Test is not { Count: > 0 }
                ? null
                : new HealthcheckConfig
                {
                    Test = [.. config.Healthcheck.Test],
                    Interval = config.Healthcheck.Interval ?? 0,
                    Timeout = config.Healthcheck.Timeout ?? 0,
                    Retries = config.Healthcheck.Retries ?? 0,
                    StartPeriod = config.Healthcheck.StartPeriod ?? 0,
                },
        };
    }

    /// <summary>All non-attestation variants (buildkit pairs one <c>unknown/unknown</c> manifest with each real platform).</summary>
    public static List<AppleImageVariant> RealVariants(AppleImageJson json)
    {
        var variants = new List<AppleImageVariant>();
        if (json.Variants is null)
        {
            return variants;
        }

        foreach (var variant in json.Variants)
        {
            if (!variant.IsAttestation)
            {
                variants.Add(variant);
            }
        }

        return variants;
    }

    /// <summary>Picks the requested platform's variant, else the host's, else the first real one.</summary>
    public static AppleImageVariant? PickVariant(AppleImageJson json, string? platform)
    {
        var variants = RealVariants(json);
        if (variants.Count == 0)
        {
            return null;
        }

        var (os, architecture, variantName) = ParsePlatform(platform);
        os ??= "linux";
        architecture ??= HostArchitecture;

        foreach (var candidate in variants)
        {
            if (Matches(candidate, os, architecture, variantName))
            {
                return candidate;
            }
        }

        // Fall back to the host architecture on any OS, then to anything real.
        foreach (var candidate in variants)
        {
            if (string.Equals(candidate.Platform?.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return variants[0];
    }

    /// <summary>The architecture name Apple uses for this machine.</summary>
    public static string HostArchitecture =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => "arm64",
        };

    public static (string? Os, string? Architecture, string? Variant) ParsePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return (null, null, null);
        }

        var parts = platform.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => (null, parts[0], null),
            2 => (parts[0], parts[1], null),
            _ => (parts[0], parts[1], parts[2]),
        };
    }

    public static string ToImageId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        return id.Contains(':', StringComparison.Ordinal) ? id : $"sha256:{id}";
    }

    public static IReadOnlyList<string> References(AppleImageJson json)
    {
        var references = new List<string>();
        foreach (var candidate in new[] { json.Configuration?.Name, json.DisplayReference, json.Name })
        {
            if (!string.IsNullOrEmpty(candidate) && !references.Contains(candidate, StringComparer.Ordinal))
            {
                references.Add(candidate);
            }
        }

        return references;
    }

    public static IReadOnlyList<string> RepoDigests(AppleImageJson json)
    {
        var digest = json.Configuration?.Descriptor?.Digest;
        var name = json.Configuration?.Name;
        if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(name))
        {
            return [];
        }

        var colon = name.LastIndexOf(':');
        var slash = name.LastIndexOf('/');
        var repository = colon > slash ? name[..colon] : name;
        return [$"{repository}@{digest}"];
    }

    public static RuntimeNetwork ToNetwork(AppleNetworkJson json)
    {
        var configuration = json.Configuration;
        return new RuntimeNetwork
        {
            Name = configuration?.Name ?? json.Id ?? "",
            Id = json.Id ?? configuration?.Name,
            Mode = configuration?.Mode ?? "nat",
            Subnet = json.Status?.Ipv4Subnet ?? configuration?.Subnet,
            Gateway = json.Status?.Ipv4Gateway,
            SubnetV6 = json.Status?.Ipv6Subnet,
            Internal = configuration?.Internal ?? false,
            Labels = configuration?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            Created = configuration?.CreationDate,
        };
    }

    public static RuntimeVolume ToVolume(AppleVolumeJson json)
    {
        var configuration = json.Configuration;
        return new RuntimeVolume
        {
            Name = configuration?.Name ?? json.Id ?? "",
            Driver = string.IsNullOrEmpty(configuration?.Driver) ? "local" : configuration.Driver,
            Labels = configuration?.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            Options = configuration?.Options is { Count: > 0 } options
                ? new Dictionary<string, string>(options, StringComparer.Ordinal)
                : EmptyLabels,
            Created = configuration?.CreationDate,
            Mountpoint = configuration?.Source,
            SizeBytes = configuration?.SizeInBytes,
        };
    }

    public static RuntimeStats ToStats(AppleStats stats, DateTimeOffset readAt) => new()
    {
        MemoryUsageBytes = stats.MemoryUsageBytes ?? 0,
        MemoryLimitBytes = stats.MemoryLimitBytes ?? 0,
        CpuUsageUsec = stats.CpuUsageUsec ?? 0,
        NetworkRxBytes = stats.NetworkRxBytes ?? 0,
        NetworkTxBytes = stats.NetworkTxBytes ?? 0,
        BlockReadBytes = stats.BlockReadBytes ?? 0,
        BlockWriteBytes = stats.BlockWriteBytes ?? 0,
        NumProcesses = stats.NumProcesses ?? 0,
        ReadAt = readAt,
    };

    public static RuntimeDiskUsage ToDiskUsage(AppleDiskUsage usage) => new()
    {
        ImagesBytes = usage.Images?.SizeInBytes ?? 0,
        ContainersBytes = usage.Containers?.SizeInBytes ?? 0,
        VolumesBytes = usage.Volumes?.SizeInBytes ?? 0,
        BuildCacheBytes = 0,
        ImagesCount = usage.Images?.Total ?? 0,
        ContainersCount = usage.Containers?.Total ?? 0,
        VolumesCount = usage.Volumes?.Total ?? 0,
    };

    private static bool Matches(AppleImageVariant variant, string os, string architecture, string? variantName)
    {
        if (!string.Equals(variant.Platform?.Os, os, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(variant.Platform?.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return variantName is null ||
            string.Equals(variant.Platform?.Variant, variantName, StringComparison.OrdinalIgnoreCase);
    }
}
