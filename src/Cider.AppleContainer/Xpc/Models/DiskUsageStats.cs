namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `DiskUsageStats` (`Client/DiskUsage.swift:19-57`), the `systemDiskUsage` reply's
/// `diskUsageStats` payload (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.6): <c>{images,
/// containers, volumes}</c>, each a <see cref="ResourceUsage"/>.
/// </summary>
internal sealed class DiskUsageStats
{
    public required ResourceUsage Images { get; init; }

    public required ResourceUsage Containers { get; init; }

    public required ResourceUsage Volumes { get; init; }
}

/// <summary><c>ResourceUsage {total: Int, active: Int, sizeInBytes: UInt64, reclaimable:
/// UInt64}</c>.</summary>
internal sealed class ResourceUsage
{
    public required int Total { get; init; }

    public required int Active { get; init; }

    public required ulong SizeInBytes { get; init; }

    public required ulong Reclaimable { get; init; }
}
