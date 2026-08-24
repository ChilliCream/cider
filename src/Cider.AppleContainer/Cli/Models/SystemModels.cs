namespace Cider.AppleContainer.Cli.Models;

// `container system status --format json`, `container system version --format json`,
// `container system df --format json`, `container stats --format json --no-stream`
// (docs/apple-container-notes.md §1, §9 and probes of 1.2.2).

internal sealed class AppleSystemStatus
{
    /// <summary><c>running</c> when the apiserver is up.</summary>
    public string? Status { get; set; }

    public string? AppRoot { get; set; }

    public string? InstallRoot { get; set; }

    public string? LogRoot { get; set; }

    public string? ApiServerAppName { get; set; }

    public string? ApiServerBuild { get; set; }

    public string? ApiServerCommit { get; set; }

    public string? ApiServerVersion { get; set; }

    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);
}

internal sealed class AppleVersionEntry
{
    public string? AppName { get; set; }

    public string? BuildType { get; set; }

    public string? Commit { get; set; }

    public string? Version { get; set; }
}

internal sealed class AppleDiskUsage
{
    public AppleDiskUsageEntry? Containers { get; set; }

    public AppleDiskUsageEntry? Images { get; set; }

    public AppleDiskUsageEntry? Volumes { get; set; }
}

internal sealed class AppleDiskUsageEntry
{
    public int? Active { get; set; }

    public long? Reclaimable { get; set; }

    public long? SizeInBytes { get; set; }

    public int? Total { get; set; }
}

internal sealed class AppleStats
{
    public string? Id { get; set; }

    public long? MemoryUsageBytes { get; set; }

    public long? MemoryLimitBytes { get; set; }

    public long? CpuUsageUsec { get; set; }

    public long? NetworkRxBytes { get; set; }

    public long? NetworkTxBytes { get; set; }

    public long? BlockReadBytes { get; set; }

    public long? BlockWriteBytes { get; set; }

    public int? NumProcesses { get; set; }
}
