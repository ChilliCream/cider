using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary><c>GET /containers/{id}/stats</c> — Docker serializes this whole tree in snake_case.</summary>
public sealed class ContainerStats
{
    [JsonPropertyName("read")]
    public string Read { get; set; } = "";

    [JsonPropertyName("preread")]
    public string Preread { get; set; } = "";

    [JsonPropertyName("pids_stats")]
    public PidsStats PidsStats { get; set; } = new();

    [JsonPropertyName("blkio_stats")]
    public BlkioStats BlkioStats { get; set; } = new();

    [JsonPropertyName("num_procs")]
    public uint NumProcs { get; set; }

    [JsonPropertyName("storage_stats")]
    public StorageStats StorageStats { get; set; } = new();

    [JsonPropertyName("cpu_stats")]
    public CpuStats CpuStats { get; set; } = new();

    [JsonPropertyName("precpu_stats")]
    public CpuStats PreCpuStats { get; set; } = new();

    [JsonPropertyName("memory_stats")]
    public MemoryStats MemoryStats { get; set; } = new();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("networks")]
    public Dictionary<string, NetworkStats>? Networks { get; set; }
}

/// <summary><c>ContainerStats.pids_stats</c>.</summary>
public sealed class PidsStats
{
    [JsonPropertyName("current")]
    public ulong Current { get; set; }

    [JsonPropertyName("limit")]
    public ulong Limit { get; set; }
}

/// <summary><c>ContainerStats.blkio_stats</c>.</summary>
public sealed class BlkioStats
{
    [JsonPropertyName("io_service_bytes_recursive")]
    public List<BlkioStatEntry> IoServiceBytesRecursive { get; set; } = [];

    [JsonPropertyName("io_serviced_recursive")]
    public List<BlkioStatEntry> IoServicedRecursive { get; set; } = [];

    [JsonPropertyName("io_queue_recursive")]
    public List<BlkioStatEntry> IoQueueRecursive { get; set; } = [];

    [JsonPropertyName("io_service_time_recursive")]
    public List<BlkioStatEntry> IoServiceTimeRecursive { get; set; } = [];

    [JsonPropertyName("io_wait_time_recursive")]
    public List<BlkioStatEntry> IoWaitTimeRecursive { get; set; } = [];

    [JsonPropertyName("io_merged_recursive")]
    public List<BlkioStatEntry> IoMergedRecursive { get; set; } = [];

    [JsonPropertyName("io_time_recursive")]
    public List<BlkioStatEntry> IoTimeRecursive { get; set; } = [];

    [JsonPropertyName("sectors_recursive")]
    public List<BlkioStatEntry> SectorsRecursive { get; set; } = [];
}

/// <summary>One row of a blkio table.</summary>
public sealed class BlkioStatEntry
{
    [JsonPropertyName("major")]
    public ulong Major { get; set; }

    [JsonPropertyName("minor")]
    public ulong Minor { get; set; }

    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("value")]
    public ulong Value { get; set; }
}

/// <summary><c>ContainerStats.storage_stats</c> (Windows-only in Docker; empty here).</summary>
public sealed class StorageStats
{
    [JsonPropertyName("read_count_normalized")]
    public ulong? ReadCountNormalized { get; set; }

    [JsonPropertyName("read_size_bytes")]
    public ulong? ReadSizeBytes { get; set; }

    [JsonPropertyName("write_count_normalized")]
    public ulong? WriteCountNormalized { get; set; }

    [JsonPropertyName("write_size_bytes")]
    public ulong? WriteSizeBytes { get; set; }
}

/// <summary><c>ContainerStats.cpu_stats</c> / <c>precpu_stats</c>.</summary>
public sealed class CpuStats
{
    [JsonPropertyName("cpu_usage")]
    public CpuUsage CpuUsage { get; set; } = new();

    [JsonPropertyName("system_cpu_usage")]
    public ulong? SystemCpuUsage { get; set; }

    [JsonPropertyName("online_cpus")]
    public uint? OnlineCpus { get; set; }

    [JsonPropertyName("throttling_data")]
    public ThrottlingData ThrottlingData { get; set; } = new();
}

/// <summary><c>cpu_stats.cpu_usage</c>.</summary>
public sealed class CpuUsage
{
    [JsonPropertyName("total_usage")]
    public ulong TotalUsage { get; set; }

    [JsonPropertyName("percpu_usage")]
    public List<ulong>? PerCpuUsage { get; set; }

    [JsonPropertyName("usage_in_kernelmode")]
    public ulong UsageInKernelmode { get; set; }

    [JsonPropertyName("usage_in_usermode")]
    public ulong UsageInUsermode { get; set; }
}

/// <summary><c>cpu_stats.throttling_data</c>.</summary>
public sealed class ThrottlingData
{
    [JsonPropertyName("periods")]
    public ulong Periods { get; set; }

    [JsonPropertyName("throttled_periods")]
    public ulong ThrottledPeriods { get; set; }

    [JsonPropertyName("throttled_time")]
    public ulong ThrottledTime { get; set; }
}

/// <summary><c>ContainerStats.memory_stats</c>.</summary>
public sealed class MemoryStats
{
    [JsonPropertyName("usage")]
    public ulong Usage { get; set; }

    [JsonPropertyName("max_usage")]
    public ulong MaxUsage { get; set; }

    [JsonPropertyName("stats")]
    public Dictionary<string, ulong> Stats { get; set; } = [];

    [JsonPropertyName("failcnt")]
    public ulong Failcnt { get; set; }

    [JsonPropertyName("limit")]
    public ulong Limit { get; set; }
}

/// <summary>One entry of <c>ContainerStats.networks</c>.</summary>
public sealed class NetworkStats
{
    [JsonPropertyName("rx_bytes")]
    public ulong RxBytes { get; set; }

    [JsonPropertyName("rx_packets")]
    public ulong RxPackets { get; set; }

    [JsonPropertyName("rx_errors")]
    public ulong RxErrors { get; set; }

    [JsonPropertyName("rx_dropped")]
    public ulong RxDropped { get; set; }

    [JsonPropertyName("tx_bytes")]
    public ulong TxBytes { get; set; }

    [JsonPropertyName("tx_packets")]
    public ulong TxPackets { get; set; }

    [JsonPropertyName("tx_errors")]
    public ulong TxErrors { get; set; }

    [JsonPropertyName("tx_dropped")]
    public ulong TxDropped { get; set; }
}
