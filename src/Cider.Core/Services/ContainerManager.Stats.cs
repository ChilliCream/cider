using System.Diagnostics;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.Time;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    /// <summary>
    /// <c>GET /containers/{id}/stats</c>: one sample in Docker's shape. <c>precpu_stats</c> comes
    /// from the previous sample of the same container so clients can compute a CPU percentage.
    /// </summary>
    public async Task<ContainerStats> StatsAsync(string idOrName, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        RuntimeStats? sample = null;
        if (record.State.Running)
        {
            try
            {
                sample = await _runtime.GetStatsAsync(record.RuntimeId, ct);
            }
            catch (RuntimeException ex)
            {
                _logger.LogDebug(ex, "stats for container {Container} are unavailable", record.Id);
            }
        }

        var stats = BuildStats(record, sample, handle.PreviousSample);
        handle.PreviousSample = stats;
        return stats;
    }

    private ContainerStats BuildStats(State.ContainerRecord record, RuntimeStats? sample, ContainerStats? previous)
    {
        var onlineCpus = (uint)Math.Max(Environment.ProcessorCount, 1);
        var readAt = sample?.ReadAt ?? DateTimeOffset.UtcNow;

        var stats = new ContainerStats
        {
            Id = record.Id,
            Name = "/" + record.Name,
            Read = DockerTime.Format(readAt),
            Preread = previous is null ? DockerTime.ZeroTime : previous.Read,
        };

        if (sample is null)
        {
            stats.PreCpuStats = previous?.CpuStats ?? new CpuStats();
            return stats;
        }

        // Docker's system_cpu_usage is host CPU time in nanoseconds; a monotonic clock times the
        // online CPUs gives clients a denominator that grows exactly like the real one.
        var systemUsage = (ulong)MonotonicNanos() * onlineCpus;
        var totalUsage = (ulong)Math.Max(sample.CpuUsageUsec, 0) * 1000UL;

        stats.CpuStats = new CpuStats
        {
            CpuUsage = new CpuUsage
            {
                TotalUsage = totalUsage,
                UsageInKernelmode = 0,
                UsageInUsermode = totalUsage,
            },
            SystemCpuUsage = systemUsage,
            OnlineCpus = onlineCpus,
        };

        stats.PreCpuStats = previous?.CpuStats ?? new CpuStats
        {
            CpuUsage = new CpuUsage(),
            SystemCpuUsage = null,
            OnlineCpus = onlineCpus,
        };

        stats.MemoryStats = new MemoryStats
        {
            Usage = (ulong)Math.Max(sample.MemoryUsageBytes, 0),
            MaxUsage = (ulong)Math.Max(sample.MemoryUsageBytes, 0),
            Limit = (ulong)Math.Max(sample.MemoryLimitBytes, 0),
        };

        stats.PidsStats = new PidsStats { Current = (ulong)Math.Max(sample.NumProcesses, 0) };
        stats.NumProcs = (uint)Math.Max(sample.NumProcesses, 0);

        stats.Networks = new Dictionary<string, NetworkStats>(StringComparer.Ordinal)
        {
            ["eth0"] = new NetworkStats
            {
                RxBytes = (ulong)Math.Max(sample.NetworkRxBytes, 0),
                TxBytes = (ulong)Math.Max(sample.NetworkTxBytes, 0),
            },
        };

        stats.BlkioStats.IoServiceBytesRecursive =
        [
            new BlkioStatEntry { Major = 8, Minor = 0, Op = "read", Value = (ulong)Math.Max(sample.BlockReadBytes, 0) },
            new BlkioStatEntry { Major = 8, Minor = 0, Op = "write", Value = (ulong)Math.Max(sample.BlockWriteBytes, 0) },
        ];

        return stats;
    }

    private static long MonotonicNanos() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
}
