using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>Network, volume, stats, system status and system df fixtures from 1.2.2.</summary>
public class ResourceParsingTests
{
    [Fact]
    public void Network_list_maps_gateway_subnet_and_labels()
    {
        const string json = """
        [{"configuration":{"creationDate":"2026-08-21T05:21:55Z","labels":{"com.apple.container.resource.role":"builtin"},"mode":"nat","name":"default","options":{},"plugin":"container-network-vmnet"},"id":"default","status":{"ipv4Gateway":"192.168.64.1","ipv4Subnet":"192.168.64.0/24","ipv6Subnet":"fd3e:bc7a:df05:1995::/64"}}]
        """;

        var parsed = ContainerCli.ParseJson<List<AppleNetworkJson>>(json, "test")!;
        var network = RuntimeMapper.ToNetwork(parsed[0]);

        Assert.Equal("default", network.Name);
        Assert.Equal("default", network.Id);
        Assert.Equal("nat", network.Mode);
        Assert.Equal("192.168.64.0/24", network.Subnet);
        Assert.Equal("192.168.64.1", network.Gateway);
        Assert.Equal("fd3e:bc7a:df05:1995::/64", network.SubnetV6);
        Assert.False(network.Internal);
        Assert.Equal("builtin", network.Labels["com.apple.container.resource.role"]);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-21T05:21:55Z", System.Globalization.CultureInfo.InvariantCulture),
            network.Created);
    }

    [Fact]
    public void Volume_maps_the_backing_image_path_as_mountpoint()
    {
        const string json = """
        [{"configuration":{"creationDate":"2026-08-21T11:57:44Z","driver":"local","format":"ext4","labels":{"x":"y"},"name":"adtest-vol","options":{},"sizeInBytes":549755813888,"source":"/Users/michael/Library/Application Support/com.apple.container/volumes/adtest-vol/volume.img"},"id":"adtest-vol"}]
        """;

        var parsed = ContainerCli.ParseJson<List<AppleVolumeJson>>(json, "test")!;
        var volume = RuntimeMapper.ToVolume(parsed[0]);

        Assert.Equal("adtest-vol", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.Equal("y", volume.Labels["x"]);
        Assert.Equal(549755813888, volume.SizeBytes);
        Assert.EndsWith("adtest-vol/volume.img", volume.Mountpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Stats_map_one_to_one()
    {
        const string json = """
        [{"blockReadBytes":3977216,"blockWriteBytes":0,"cpuUsageUsec":2801,"id":"adtest2","memoryLimitBytes":1073741824,"memoryUsageBytes":4308992,"networkRxBytes":27532,"networkTxBytes":602,"numProcesses":1}]
        """;

        var parsed = ContainerCli.ParseJson<List<AppleStats>>(json, "test")!;
        var readAt = DateTimeOffset.UnixEpoch;
        var stats = RuntimeMapper.ToStats(parsed[0], readAt);

        Assert.Equal(4308992, stats.MemoryUsageBytes);
        Assert.Equal(1073741824, stats.MemoryLimitBytes);
        Assert.Equal(2801, stats.CpuUsageUsec);
        Assert.Equal(27532, stats.NetworkRxBytes);
        Assert.Equal(602, stats.NetworkTxBytes);
        Assert.Equal(3977216, stats.BlockReadBytes);
        Assert.Equal(0, stats.BlockWriteBytes);
        Assert.Equal(1, stats.NumProcesses);
        Assert.Equal(readAt, stats.ReadAt);
    }

    [Fact]
    public void System_status_reports_running()
    {
        const string json = """
        {"apiServerAppName":"container-apiserver","apiServerBuild":"release","apiServerCommit":"0190097d","apiServerVersion":"container-apiserver version 1.2.2 (build: release, commit: 0190097)","appRoot":"/Users/michael/Library/Application Support/com.apple.container/","installRoot":"/usr/local/","status":"running"}
        """;

        var status = ContainerCli.ParseJson<AppleSystemStatus>(json, "test")!;

        Assert.True(status.IsRunning);
        Assert.Equal("/Users/michael/Library/Application Support/com.apple.container/", status.AppRoot);
        Assert.Contains("1.2.2", status.ApiServerVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void System_status_reports_not_running()
    {
        var status = ContainerCli.ParseJson<AppleSystemStatus>("""{"status":"not running"}""", "test")!;
        Assert.False(status.IsRunning);
    }

    [Fact]
    public void Disk_usage_maps_the_three_buckets()
    {
        const string json = """
        {"containers":{"active":1,"reclaimable":0,"sizeInBytes":1637646336,"total":1},
         "images":{"active":1,"reclaimable":10869964800,"sizeInBytes":12390469632,"total":4},
         "volumes":{"active":0,"reclaimable":0,"sizeInBytes":0,"total":0}}
        """;

        var usage = RuntimeMapper.ToDiskUsage(ContainerCli.ParseJson<AppleDiskUsage>(json, "test")!);

        Assert.Equal(1637646336, usage.ContainersBytes);
        Assert.Equal(1, usage.ContainersCount);
        Assert.Equal(12390469632, usage.ImagesBytes);
        Assert.Equal(4, usage.ImagesCount);
        Assert.Equal(0, usage.VolumesBytes);
        Assert.Equal(0, usage.VolumesCount);
    }

    [Fact]
    public void System_version_is_an_array_of_components()
    {
        const string json = """
        [{"appName":"container","buildType":"release","commit":"0190097d","version":"1.2.2"},
         {"appName":"container-apiserver","buildType":"release","commit":"0190097d","version":"container-apiserver version 1.2.2"}]
        """;

        var entries = ContainerCli.ParseJson<List<AppleVersionEntry>>(json, "test")!;

        Assert.Equal(2, entries.Count);
        Assert.Equal("container", entries[0].AppName);
        Assert.Equal("1.2.2", entries[0].Version);
    }
}
