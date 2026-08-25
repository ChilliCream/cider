using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// Wire model → <c>Cider.Core.Runtime</c> mapping for <see cref="XpcContainerRuntime"/>'s read paths
/// (task cider-ede.5, fix direction §3). Mirrors <c>Cli.RuntimeMapper</c> field-for-field wherever the
/// two transports carry the same information, so a container/network/volume/stats/disk-usage record
/// looks identical to callers above the <see cref="IContainerRuntime"/> seam regardless of which
/// transport produced it. Every member here is a pure function over already-decoded wire models — no
/// XPC call, no CLI fallback — so it is testable straight from fixtures
/// (<c>tests/Cider.Tests/AppleContainer/Xpc/XpcRuntimeMappingTests.cs</c>) without a live apiserver.
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    private static readonly Dictionary<string, string> EmptyLabels = new(StringComparer.Ordinal);

    // ---- containerList / containerList-with-id-filter reply → RuntimeContainer -----------------

    /// <summary>
    /// docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.2. <see cref="RuntimeContainer.RuntimeId"/> is
    /// <c>configuration.id</c> (the snapshot's own computed <c>id</c> is never on the wire — §2.2).
    /// Hidden-container labels (<c>com.apple.container.plugin</c>, <c>resource.role</c>) survive
    /// verbatim in <see cref="RuntimeContainer.Labels"/> so
    /// <c>ContainerManager.IsSystemContainer</c> keeps filtering the builder and forwarders exactly
    /// as it does for the CLI transport.
    /// </summary>
    internal static RuntimeContainer ToContainer(ContainerSnapshot snapshot)
    {
        var configuration = snapshot.Configuration;
        var init = configuration.InitProcess;

        // Defensive, not decorative: ContainerConfiguration has a custom Swift `init(from:)` that
        // tolerates every field below being absent from the JSON except id/image/initProcess (§2.0
        // rule 11) — but our C# model has no matching custom converter, only field initializers on
        // non-required properties. Confirmed live: System.Text.Json's required-member deserialization
        // path does NOT run those field initializers when the class also carries `required` members
        // elsewhere (as this one does) — an absent key leaves the property null/default(T), not its
        // declared default, despite the non-nullable annotation. `?? <the field's own declared
        // default>` below restores exactly what ContainerConfiguration's own initializer already
        // promises, so a real reply that omits one of these (matching the Swift decoder's own
        // tolerance) maps the same as the CLI transport instead of throwing a NullReferenceException.
        var labels = configuration.Labels ?? [];
        var mounts = configuration.Mounts ?? [];
        var publishedPorts = configuration.PublishedPorts ?? [];
        var resources = configuration.Resources ?? new Resources();
        var platform = configuration.Platform ?? Platform.Current;

        var argv = new List<string>(1 + init.Arguments.Count);
        if (!string.IsNullOrEmpty(init.Executable))
        {
            argv.Add(init.Executable);
        }

        argv.AddRange(init.Arguments);

        return new RuntimeContainer
        {
            RuntimeId = configuration.Id,
            State = ToState(snapshot.Status),
            ImageReference = configuration.Image.Reference,
            ImageDigest = configuration.Image.Descriptor.Digest,
            Labels = labels.Count > 0
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            Networks = ToNetworkAttachments(configuration, snapshot.Networks),
            PublishedPorts = ToPorts(publishedPorts),
            Mounts = ToMounts(mounts),
            Platform = ToPlatform(platform),
            Argv = argv,
            Env = init.Environment.Count > 0 ? [.. init.Environment] : [],
            WorkingDir = init.WorkingDirectory,
            Tty = init.Terminal,
            Cpus = resources.Cpus,
            MemoryBytes = (long)resources.MemoryInBytes,
            CreatedAt = configuration.CreationDate,
            StartedAt = snapshot.StartedDate,
        };
    }

    /// <summary>
    /// Apple reports only <c>running</c>/<c>stopping</c>/<c>stopped</c>/<c>unknown</c> (§2.0 rule 4);
    /// a snapshot for a container that was created but never bootstrapped is <c>stopped</c> — the
    /// same value the CLI transport's own <c>RuntimeMapper.ToState</c> derives for that case, so this
    /// needs no special "never bootstrapped" branch to stay at parity with it.
    /// </summary>
    internal static RuntimeContainerState ToState(RuntimeStatus status) => status switch
    {
        RuntimeStatus.Running => RuntimeContainerState.Running,
        RuntimeStatus.Stopping => RuntimeContainerState.Stopping,
        RuntimeStatus.Stopped => RuntimeContainerState.Stopped,
        _ => RuntimeContainerState.Unknown,
    };

    /// <summary>
    /// Prefers the snapshot's live <c>networks</c> (actual attached addresses); falls back to the
    /// configuration's <i>requested</i> networks (name + hostname only, no addresses yet) when the
    /// container has never been bootstrapped and so carries none — the same two-tier fallback
    /// <c>RuntimeMapper.ToNetworkAttachments</c> uses for the CLI transport. <c>ipv6Gateway</c> has no
    /// wire source (Apple reports no per-attachment IPv6 gateway, §2.2) and so is always <c>null</c>,
    /// same as the CLI mapping.
    /// </summary>
    internal static IReadOnlyList<RuntimeNetworkAttachment> ToNetworkAttachments(
        ContainerConfiguration configuration, List<Attachment> liveNetworks)
    {
        if (liveNetworks.Count > 0)
        {
            var live = new List<RuntimeNetworkAttachment>(liveNetworks.Count);
            foreach (var attachment in liveNetworks)
            {
                live.Add(new RuntimeNetworkAttachment
                {
                    Network = attachment.Network,
                    Hostname = attachment.Hostname,
                    IPv4Address = StripCidr(attachment.Ipv4Address),
                    IPv4Gateway = StripCidr(attachment.Ipv4Gateway),
                    Ipv6Address = StripCidr(attachment.Ipv6Address),
                    MacAddress = attachment.MacAddress,
                });
            }

            return live;
        }

        var requestedNetworks = configuration.Networks ?? [];
        if (requestedNetworks.Count > 0)
        {
            var requested = new List<RuntimeNetworkAttachment>(requestedNetworks.Count);
            foreach (var network in requestedNetworks)
            {
                requested.Add(new RuntimeNetworkAttachment
                {
                    Network = network.Network,
                    Hostname = network.Options.Hostname,
                });
            }

            return requested;
        }

        return [];
    }

    /// <summary>One <see cref="PortSpec"/> per <c>PublishPort</c> entry — <c>count</c> (a host port
    /// range starting at <c>hostPort</c>) is ignored, same as <c>RuntimeMapper.ToPorts</c> for the
    /// CLI transport.</summary>
    internal static IReadOnlyList<PortSpec> ToPorts(List<PublishPort> ports)
    {
        if (ports.Count == 0)
        {
            return [];
        }

        var mapped = new List<PortSpec>(ports.Count);
        foreach (var port in ports)
        {
            mapped.Add(new PortSpec
            {
                HostIp = port.HostAddress,
                HostPort = port.HostPort,
                ContainerPort = port.ContainerPort,
                Proto = string.IsNullOrEmpty(port.Proto) ? "tcp" : port.Proto,
            });
        }

        return mapped;
    }

    /// <summary>
    /// <c>Filesystem.type</c> is a single-key union (§2.0 rule 3): <c>volume</c> → <see cref="MountKind.Volume"/>
    /// (source = the volume name, not the host-side backing path); <c>tmpfs</c> → <see cref="MountKind.Tmpfs"/>
    /// (source blank — the wire's literal <c>"tmpfs"</c> source string is not a real path, matching
    /// <c>RuntimeMapper.ToMounts</c>); <c>virtiofs</c>, <c>block</c>, and any other case are host bind
    /// mounts, mirroring the CLI mapper's own "anything unknown is a bind mount" default. <c>"ro"</c>/
    /// <c>"readonly"</c> in <c>options</c> (case-insensitive) marks the mount read-only.
    /// </summary>
    internal static IReadOnlyList<MountSpec> ToMounts(List<Filesystem> mounts)
    {
        if (mounts.Count == 0)
        {
            return [];
        }

        var mapped = new List<MountSpec>(mounts.Count);
        foreach (var mount in mounts)
        {
            var readOnly = mount.Options.Exists(o =>
                string.Equals(o, "ro", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o, "readonly", StringComparison.OrdinalIgnoreCase));

            if (mount.Type.Volume is { } volume)
            {
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Volume,
                    Source = volume.Name,
                    Target = mount.Destination,
                    ReadOnly = readOnly,
                });
            }
            else if (mount.Type.Tmpfs is not null)
            {
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Tmpfs,
                    Source = "",
                    Target = mount.Destination,
                    ReadOnly = readOnly,
                });
            }
            else
            {
                mapped.Add(new MountSpec
                {
                    Kind = MountKind.Bind,
                    Source = mount.Source,
                    Target = mount.Destination,
                    ReadOnly = readOnly,
                });
            }
        }

        return mapped;
    }

    internal static string? ToPlatform(Platform platform)
    {
        if (string.IsNullOrEmpty(platform.Os) || string.IsNullOrEmpty(platform.Architecture))
        {
            return null;
        }

        return string.IsNullOrEmpty(platform.Variant)
            ? $"{platform.Os}/{platform.Architecture}"
            : $"{platform.Os}/{platform.Architecture}/{platform.Variant}";
    }

    /// <summary><c>"192.168.64.2/24"</c> → <c>"192.168.64.2"</c>; a no-op for a bare address (the
    /// gateway fields already are one, §2.0 rule 7) — identical to <c>RuntimeMapper.StripCidr</c>.</summary>
    internal static string? StripCidr(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return address;
        }

        var slash = address.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? address : address[..slash];
    }

    // ---- networkList reply → RuntimeNetwork -------------------------------------------------------

    /// <summary>
    /// docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.4. The wire carries no network id the decoder
    /// keeps (§2.2: <c>NetworkResource</c> encodes <c>id</c> but decoding ignores it), so
    /// <see cref="RuntimeNetwork.Id"/> falls back to the name, same as
    /// <c>RuntimeMapper.ToNetwork</c> does when the CLI's own <c>id</c> field is absent.
    /// <see cref="RuntimeNetwork.Internal"/> has no wire source in this protocol version and is
    /// always <c>false</c>.
    /// </summary>
    internal static RuntimeNetwork ToNetwork(NetworkResource resource)
    {
        var configuration = resource.Configuration;
        return new RuntimeNetwork
        {
            Name = configuration.Name,
            Id = configuration.Name,
            Mode = string.IsNullOrEmpty(configuration.Mode) ? "nat" : configuration.Mode,
            Subnet = resource.Status.Ipv4Subnet,
            Gateway = resource.Status.Ipv4Gateway,
            SubnetV6 = resource.Status.Ipv6Subnet,
            Internal = false,
            Labels = configuration.Labels.Count > 0
                ? new Dictionary<string, string>(configuration.Labels, StringComparer.Ordinal)
                : EmptyLabels,
            Created = configuration.CreationDate,
        };
    }

    // ---- volumeList reply → RuntimeVolume ---------------------------------------------------------

    /// <summary>docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.5 — field-for-field identical to
    /// <c>RuntimeMapper.ToVolume</c>, since <see cref="VolumeConfiguration"/> already mirrors the
    /// CLI's own volume JSON one-for-one. <c>?? []</c> on <c>Labels</c>/<c>Options</c>: the same
    /// required-member deserialization gap <see cref="ToContainer"/> guards against — confirmed live
    /// for this type too.</summary>
    internal static RuntimeVolume ToVolume(VolumeConfiguration configuration)
    {
        var labels = configuration.Labels ?? [];
        var options = configuration.Options ?? [];

        return new RuntimeVolume
        {
            Name = configuration.Name,
            Driver = string.IsNullOrEmpty(configuration.Driver) ? "local" : configuration.Driver,
            Labels = labels.Count > 0
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : EmptyLabels,
            Options = options.Count > 0
                ? new Dictionary<string, string>(options, StringComparer.Ordinal)
                : EmptyLabels,
            Created = configuration.CreationDate,
            Mountpoint = configuration.Source,
            SizeBytes = configuration.SizeInBytes is { } size ? (long)size : null,
        };
    }

    // ---- containerStats reply → RuntimeStats -------------------------------------------------------

    internal static RuntimeStats ToStats(ContainerStats stats, DateTimeOffset readAt) => new()
    {
        MemoryUsageBytes = (long)(stats.MemoryUsageBytes ?? 0),
        MemoryLimitBytes = (long)(stats.MemoryLimitBytes ?? 0),
        CpuUsageUsec = (long)(stats.CpuUsageUsec ?? 0),
        NetworkRxBytes = (long)(stats.NetworkRxBytes ?? 0),
        NetworkTxBytes = (long)(stats.NetworkTxBytes ?? 0),
        BlockReadBytes = (long)(stats.BlockReadBytes ?? 0),
        BlockWriteBytes = (long)(stats.BlockWriteBytes ?? 0),
        NumProcesses = (int)(stats.NumProcesses ?? 0),
        ReadAt = readAt,
    };

    // ---- systemDiskUsage reply → RuntimeDiskUsage --------------------------------------------------

    /// <summary>Apple reports no separate build-cache figure over XPC (same as the CLI transport, §2.6),
    /// so <see cref="RuntimeDiskUsage.BuildCacheBytes"/> is always 0.</summary>
    internal static RuntimeDiskUsage ToDiskUsage(DiskUsageStats stats) => new()
    {
        ImagesBytes = (long)stats.Images.SizeInBytes,
        ContainersBytes = (long)stats.Containers.SizeInBytes,
        VolumesBytes = (long)stats.Volumes.SizeInBytes,
        BuildCacheBytes = 0,
        ImagesCount = stats.Images.Total,
        ContainersCount = stats.Containers.Total,
        VolumesCount = stats.Volumes.Total,
    };
}
