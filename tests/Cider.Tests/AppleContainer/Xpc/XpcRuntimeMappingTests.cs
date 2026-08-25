using System.Runtime.CompilerServices;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerRuntime"/>'s wire-model → <c>Cider.Core.Runtime</c> mapping (task cider-ede.5,
/// fix direction §3), exercised as pure functions over fixtures — no live apiserver, no
/// <see cref="XpcClient"/> involved. The fixture (<c>container-list-mapping.json</c>) is a
/// <c>containerList</c>-shaped reply covering the task's verification scenarios: a running container
/// with IPv4+IPv6, a stopped (never-bootstrapped) container, and a container with virtiofs+tmpfs
/// mounts and a published port.
/// </summary>
public class XpcRuntimeMappingTests
{
    // ---- ToContainer: running container with IPv4+IPv6 ------------------------------------------

    [Fact]
    public void ToContainer_maps_a_running_dual_stack_container()
    {
        var snapshot = LoadSnapshots()[0];

        var container = XpcContainerRuntime.ToContainer(snapshot);

        Assert.Equal("dualstack-app", container.RuntimeId);
        Assert.Equal(RuntimeContainerState.Running, container.State);
        Assert.Equal("docker.io/library/alpine:3.20", container.ImageReference);
        Assert.Equal("sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc", container.ImageDigest);
        Assert.Equal(["sleep", "60"], container.Argv);
        Assert.Contains("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", container.Env);
        Assert.Equal("/", container.WorkingDir);
        Assert.False(container.Tty);
        Assert.Equal(4, container.Cpus);
        Assert.Equal(1024L * 1024 * 1024, container.MemoryBytes);
        Assert.Equal("linux/arm64", container.Platform);
        Assert.NotNull(container.StartedAt);

        var attachment = Assert.Single(container.Networks);
        Assert.Equal("default", attachment.Network);
        Assert.Equal("dualstack-app", attachment.Hostname);
        // CIDR suffixes stripped (RuntimeMapper.StripCidr parity).
        Assert.Equal("192.168.64.2", attachment.IPv4Address);
        Assert.Equal("192.168.64.1", attachment.IPv4Gateway);
        Assert.Equal("fd3e:bc7a:df05:1995:f4b2:c1ff:fe2d:3baa", attachment.Ipv6Address);
        // No per-attachment IPv6 gateway on the wire (§2.2) — always null, same as the CLI mapping.
        Assert.Null(attachment.Ipv6Gateway);
        Assert.Equal("f6:b2:c1:2d:3b:aa", attachment.MacAddress);
    }

    // ---- ToContainer: stopped (never-bootstrapped) container ------------------------------------

    [Fact]
    public void ToContainer_maps_a_stopped_never_bootstrapped_container()
    {
        var snapshot = LoadSnapshots()[1];

        var container = XpcContainerRuntime.ToContainer(snapshot);

        Assert.Equal("stopped-app", container.RuntimeId);
        Assert.Equal(RuntimeContainerState.Stopped, container.State);
        Assert.Null(container.StartedAt);

        // snapshot.networks is empty (never bootstrapped) — falls back to the configuration's
        // *requested* network (name + hostname only, no addresses), matching RuntimeMapper's own
        // two-tier fallback for the CLI transport.
        var attachment = Assert.Single(container.Networks);
        Assert.Equal("default", attachment.Network);
        Assert.Equal("stopped-app", attachment.Hostname);
        Assert.Null(attachment.IPv4Address);
        Assert.Null(attachment.Ipv6Address);
    }

    // ---- ToContainer: virtiofs+tmpfs mounts and a published port --------------------------------

    [Fact]
    public void ToContainer_maps_virtiofs_and_tmpfs_mounts_and_a_published_port()
    {
        var snapshot = LoadSnapshots()[2];

        var container = XpcContainerRuntime.ToContainer(snapshot);

        Assert.Equal(2, container.Mounts.Count);

        var bind = container.Mounts[0];
        Assert.Equal(MountKind.Bind, bind.Kind);
        Assert.Equal("/Users/michael/data", bind.Source);
        Assert.Equal("/data", bind.Target);
        Assert.True(bind.ReadOnly);

        var tmpfs = container.Mounts[1];
        Assert.Equal(MountKind.Tmpfs, tmpfs.Kind);
        Assert.Equal("", tmpfs.Source);
        Assert.Equal("/run", tmpfs.Target);
        Assert.False(tmpfs.ReadOnly);

        var port = Assert.Single(container.PublishedPorts);
        Assert.Equal("0.0.0.0", port.HostIp);
        Assert.Equal(8080, port.HostPort);
        Assert.Equal(80, port.ContainerPort);
        Assert.Equal("tcp", port.Proto);

        // Ordinary labels survive verbatim.
        Assert.Equal("app", container.Labels["com.chillicream.cider.system"]);
    }

    // ---- Hidden-container labels survive (ContainerManager.IsSystemContainer keeps filtering) ----

    [Theory]
    [InlineData("com.apple.container.resource.role", "builder")]
    [InlineData("com.apple.container.plugin", "builder")]
    public void ToContainer_preserves_hidden_container_labels(string labelKey, string labelValue)
    {
        // Not an interpolated raw string on purpose: the JSON body's own literal "{{"/"}}" runs
        // (nested objects like "user":{"id":{...}}) collide with raw-string interpolation escaping,
        // so the label key/value are substituted via plain Replace instead.
        const string template = """
            {
              "configuration": {
                "id": "buildkit",
                "image": {"reference": "docker.io/library/alpine:3.20",
                          "descriptor": {"mediaType":"application/vnd.oci.image.index.v1+json","digest":"sha256:abc","size":1}},
                "initProcess": {"executable":"buildkitd","arguments":[],"environment":[],"workingDirectory":"/",
                                "terminal":false,"user":{"id":{"uid":0,"gid":0}},"supplementalGroups":[],"rlimits":[]},
                "mounts": [], "publishedPorts": [], "publishedSockets": [],
                "labels": {"__KEY__": "__VALUE__"},
                "sysctls": {}, "networks": [], "dns": {"nameservers":[],"searchDomains":[],"options":[]},
                "rosetta": true, "platform": {"os":"linux","architecture":"arm64"},
                "resources": {"cpus":2,"memoryInBytes":2147483648,"cpuOverhead":1},
                "runtimeHandler": "container-runtime-linux",
                "virtualization": false, "ssh": false, "readOnly": false, "useInit": false,
                "capAdd": ["ALL"], "capDrop": [], "creationDate": 809343921.910427
              },
              "status": "running",
              "networks": []
            }
            """;
        var json = template.Replace("__KEY__", labelKey, StringComparison.Ordinal).Replace("__VALUE__", labelValue, StringComparison.Ordinal);

        var snapshot = XpcJson.Deserialize<ContainerSnapshot>(json);
        var container = XpcContainerRuntime.ToContainer(snapshot);

        Assert.Equal(labelValue, container.Labels[labelKey]);
        Assert.True(ContainerManager.IsSystemContainer(container));
    }

    // ---- ToState ------------------------------------------------------------------------------
    // RuntimeStatus is internal (Cider.Tests only sees it via InternalsVisibleTo), so it cannot be a
    // public [Theory] parameter type (CS0051) — one [Fact] per case instead.

    [Fact]
    public void ToState_maps_running() =>
        Assert.Equal(RuntimeContainerState.Running, XpcContainerRuntime.ToState(RuntimeStatus.Running));

    [Fact]
    public void ToState_maps_stopping() =>
        Assert.Equal(RuntimeContainerState.Stopping, XpcContainerRuntime.ToState(RuntimeStatus.Stopping));

    [Fact]
    public void ToState_maps_stopped() =>
        Assert.Equal(RuntimeContainerState.Stopped, XpcContainerRuntime.ToState(RuntimeStatus.Stopped));

    [Fact]
    public void ToState_maps_unknown() =>
        Assert.Equal(RuntimeContainerState.Unknown, XpcContainerRuntime.ToState(RuntimeStatus.Unknown));

    // ---- StripCidr ------------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.64.2/24", "192.168.64.2")]
    [InlineData("192.168.64.1", "192.168.64.1")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void StripCidr_removes_only_a_trailing_slash_suffix(string? input, string? expected) =>
        Assert.Equal(expected, XpcContainerRuntime.StripCidr(input));

    // ---- ToNetwork ------------------------------------------------------------------------------

    [Fact]
    public void ToNetwork_maps_a_networkList_entry()
    {
        const string json = """
            {"configuration":{"name":"default","creationDate":809330969.025174,"mode":"nat",
             "ipv4Subnet":"192.168.64.0/24","labels":{"a":"b"},"options":{}},
             "status":{"ipv4Subnet":"192.168.64.0/24","ipv4Gateway":"192.168.64.1","ipv6Subnet":"fd3e::/64"}}
            """;
        var resource = XpcJson.Deserialize<NetworkResource>(json);

        var network = XpcContainerRuntime.ToNetwork(resource);

        Assert.Equal("default", network.Name);
        Assert.Equal("default", network.Id);
        Assert.Equal("nat", network.Mode);
        Assert.Equal("192.168.64.0/24", network.Subnet);
        Assert.Equal("192.168.64.1", network.Gateway);
        Assert.Equal("fd3e::/64", network.SubnetV6);
        Assert.False(network.Internal);
        Assert.Equal("b", network.Labels["a"]);
    }

    // ---- ToVolume -------------------------------------------------------------------------------

    [Fact]
    public void ToVolume_maps_a_volumeList_entry()
    {
        const string json = """
            {"name":"myvol","driver":"local","format":"ext4","source":"/path/to/volume.img",
             "creationDate":809330969.025174,"labels":{"x":"y"},"options":{},"sizeInBytes":104857600}
            """;
        var configuration = XpcJson.Deserialize<VolumeConfiguration>(json);

        var volume = XpcContainerRuntime.ToVolume(configuration);

        Assert.Equal("myvol", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.Equal("y", volume.Labels["x"]);
        Assert.Equal("/path/to/volume.img", volume.Mountpoint);
        Assert.Equal(104857600L, volume.SizeBytes);
    }

    [Fact]
    public void ToVolume_defaults_an_empty_driver_to_local()
    {
        const string json = """{"name":"v","driver":"","format":"ext4","source":"/p"}""";
        var configuration = XpcJson.Deserialize<VolumeConfiguration>(json);

        Assert.Equal("local", XpcContainerRuntime.ToVolume(configuration).Driver);
    }

    // ---- ToStats --------------------------------------------------------------------------------

    [Fact]
    public void ToStats_maps_a_full_sample_and_defaults_missing_measurements_to_zero()
    {
        const string json = """
            {"id":"myapp","memoryUsageBytes":1234,"memoryLimitBytes":1073741824,"cpuUsageUsec":5000,
             "networkRxBytes":10,"networkTxBytes":20,"blockReadBytes":30,"blockWriteBytes":40,"numProcesses":3}
            """;
        var stats = XpcJson.Deserialize<ContainerStats>(json);
        var readAt = DateTimeOffset.Parse("2026-08-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var mapped = XpcContainerRuntime.ToStats(stats, readAt);

        Assert.Equal(1234, mapped.MemoryUsageBytes);
        Assert.Equal(1073741824, mapped.MemoryLimitBytes);
        Assert.Equal(5000, mapped.CpuUsageUsec);
        Assert.Equal(10, mapped.NetworkRxBytes);
        Assert.Equal(20, mapped.NetworkTxBytes);
        Assert.Equal(30, mapped.BlockReadBytes);
        Assert.Equal(40, mapped.BlockWriteBytes);
        Assert.Equal(3, mapped.NumProcesses);
        Assert.Equal(readAt, mapped.ReadAt);

        var partial = XpcJson.Deserialize<ContainerStats>("{\"id\":\"myapp\"}");
        var mappedPartial = XpcContainerRuntime.ToStats(partial, readAt);
        Assert.Equal(0, mappedPartial.MemoryUsageBytes);
        Assert.Equal(0, mappedPartial.NumProcesses);
    }

    // ---- ToDiskUsage ----------------------------------------------------------------------------

    [Fact]
    public void ToDiskUsage_maps_the_three_resource_buckets_and_leaves_build_cache_zero()
    {
        const string json = """
            {"images":{"total":3,"active":2,"sizeInBytes":100,"reclaimable":10},
             "containers":{"total":1,"active":1,"sizeInBytes":50,"reclaimable":0},
             "volumes":{"total":4,"active":2,"sizeInBytes":75,"reclaimable":5}}
            """;
        var stats = XpcJson.Deserialize<DiskUsageStats>(json);

        var usage = XpcContainerRuntime.ToDiskUsage(stats);

        Assert.Equal(100, usage.ImagesBytes);
        Assert.Equal(3, usage.ImagesCount);
        Assert.Equal(50, usage.ContainersBytes);
        Assert.Equal(1, usage.ContainersCount);
        Assert.Equal(75, usage.VolumesBytes);
        Assert.Equal(4, usage.VolumesCount);
        Assert.Equal(0, usage.BuildCacheBytes);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>Loads the checked-in <c>containerList</c>-shaped fixture, resolved relative to this
    /// source file (matches <c>WireModelTests.LoadFixture</c>'s convention).</summary>
    private static List<ContainerSnapshot> LoadSnapshots([CallerFilePath] string sourcePath = "")
    {
        var fixturePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "Fixtures", "xpc", "container-list-mapping.json");
        return XpcJson.Deserialize<List<ContainerSnapshot>>(File.ReadAllText(fixturePath));
    }
}
