namespace Cider.AppleContainer.Xpc.Models;

/// <summary>
/// `ContainerStats` (`ContainerStats.swift:19-51`), the `containerStats` reply's `statistics`
/// payload (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.2). <see cref="Id"/> is required;
/// every measurement is optional (a stopped/just-created container may have no samples yet).
/// </summary>
internal sealed class ContainerStats
{
    public required string Id { get; init; }

    public ulong? MemoryUsageBytes { get; init; }

    public ulong? MemoryLimitBytes { get; init; }

    public ulong? CpuUsageUsec { get; init; }

    public ulong? NetworkRxBytes { get; init; }

    public ulong? NetworkTxBytes { get; init; }

    public ulong? BlockReadBytes { get; init; }

    public ulong? BlockWriteBytes { get; init; }

    public ulong? NumProcesses { get; init; }
}
