using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Xunit;

namespace Cider.Tests.DockerApi.Models;

/// <summary>Deserializes a body captured from <c>docker run --rm -it alpine sh -c 'echo hi'</c> (CLI 29).</summary>
public class ContainerCreateRequestTests
{
    private const string DockerCliCreateBody = """
        {"Hostname":"","Domainname":"","User":"","AttachStdin":false,"AttachStdout":true,"AttachStderr":true,"Tty":false,"OpenStdin":false,"StdinOnce":false,"Env":["A=1"],"Cmd":["sh","-c","echo hi"],"Image":"alpine","Volumes":{},"WorkingDir":"","Entrypoint":null,"OnBuild":null,"Labels":{"a":"b"},"HostConfig":{"Binds":null,"ContainerIDFile":"","LogConfig":{"Type":"","Config":{}},"NetworkMode":"bridge","PortBindings":{"80/tcp":[{"HostIp":"","HostPort":""}]},"RestartPolicy":{"Name":"no","MaximumRetryCount":0},"AutoRemove":true,"VolumeDriver":"","VolumesFrom":null,"ConsoleSize":[0,0],"CapAdd":null,"CapDrop":null,"CgroupnsMode":"","Dns":[],"DnsOptions":[],"DnsSearch":[],"ExtraHosts":null,"GroupAdd":null,"IpcMode":"","Cgroup":"","Links":null,"OomScoreAdj":0,"PidMode":"","Privileged":false,"PublishAllPorts":false,"ReadonlyRootfs":false,"SecurityOpt":null,"UTSMode":"","UsernsMode":"","ShmSize":0,"Runtime":"","Isolation":"","CpuShares":0,"Memory":0,"NanoCpus":0,"CgroupParent":"","BlkioWeight":0,"BlkioWeightDevice":[],"BlkioDeviceReadBps":[],"BlkioDeviceWriteBps":[],"BlkioDeviceReadIOps":[],"BlkioDeviceWriteIOps":[],"CpuPeriod":0,"CpuQuota":0,"CpuRealtimePeriod":0,"CpuRealtimeRuntime":0,"CpusetCpus":"","CpusetMems":"","Devices":[],"DeviceCgroupRules":null,"DeviceRequests":null,"MemoryReservation":0,"MemorySwap":0,"MemorySwappiness":null,"OomKillDisable":null,"PidsLimit":null,"Ulimits":null,"CpuCount":0,"CpuPercent":0,"IOMaximumIOps":0,"IOMaximumBandwidth":0,"MaskedPaths":null,"ReadonlyPaths":null},"NetworkingConfig":{"EndpointsConfig":{}}}
        """;

    [Fact]
    public void Deserializes_a_real_docker_cli_body()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(DockerCliCreateBody);

        Assert.NotNull(request);

        // Container config fields sit at the top level.
        Assert.Equal("alpine", request.Image);
        Assert.Equal(["sh", "-c", "echo hi"], request.Cmd);
        Assert.Equal(["A=1"], request.Env);
        Assert.Equal("b", request.Labels["a"]);
        Assert.True(request.AttachStdout);
        Assert.True(request.AttachStderr);
        Assert.False(request.AttachStdin);
        Assert.False(request.Tty);
        Assert.Empty(request.Volumes);

        // null must stay null: it means "inherit from the image", unlike [].
        Assert.Null(request.Entrypoint);
        Assert.Null(request.OnBuild);
    }

    [Fact]
    public void Deserializes_the_embedded_HostConfig()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(DockerCliCreateBody);
        var hostConfig = request!.HostConfig;

        Assert.NotNull(hostConfig);
        Assert.Equal("bridge", hostConfig.NetworkMode);
        Assert.True(hostConfig.AutoRemove);
        Assert.Null(hostConfig.Binds);
        Assert.Null(hostConfig.Ulimits);
        Assert.Null(hostConfig.MemorySwappiness);
        Assert.Equal("no", hostConfig.RestartPolicy.Name);
        Assert.Equal([0, 0], hostConfig.ConsoleSize);
        Assert.Empty(hostConfig.Dns!);

        var binding = Assert.Single(hostConfig.PortBindings);
        Assert.Equal("80/tcp", binding.Key);
        var hostBinding = Assert.Single(binding.Value!);
        Assert.Equal("", hostBinding.HostIp);
        Assert.Equal("", hostBinding.HostPort);
    }

    [Fact]
    public void Deserializes_the_embedded_NetworkingConfig()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(DockerCliCreateBody);

        Assert.NotNull(request!.NetworkingConfig);
        Assert.Empty(request.NetworkingConfig.EndpointsConfig);
    }

    [Fact]
    public void Serializes_back_with_HostConfig_and_config_fields_at_the_top_level()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(DockerCliCreateBody);

        var json = DockerJson.Serialize(request);

        Assert.Contains("\"Image\":\"alpine\"", json, StringComparison.Ordinal);
        Assert.Contains("\"HostConfig\":{", json, StringComparison.Ordinal);
        Assert.Contains("\"NetworkingConfig\":{\"EndpointsConfig\":{}}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_collections_that_never_mean_anything_become_empty()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(
            """{"Image":"alpine","Labels":null,"ExposedPorts":null,"Volumes":null,"HostConfig":{"PortBindings":null}}""");

        Assert.NotNull(request);
        Assert.Empty(request.Labels);
        Assert.Empty(request.ExposedPorts);
        Assert.Empty(request.Volumes);
        Assert.Empty(request.HostConfig!.PortBindings);
    }

    [Fact]
    public void Compose_style_body_with_networking_config_is_understood()
    {
        var request = DockerJson.Deserialize<ContainerCreateRequest>(
            """
            {"Image":"alpine","Entrypoint":[],"Cmd":null,
             "ExposedPorts":{"80/tcp":{}},
             "NetworkingConfig":{"EndpointsConfig":{"app_default":{"Aliases":["web"],"IPAMConfig":{"IPv4Address":"10.0.0.5"}}}}}
            """);

        Assert.NotNull(request);
        Assert.Empty(request.Entrypoint!);           // [] means "clear the image entrypoint"
        Assert.Null(request.Cmd);                     // null means "inherit the image command"
        Assert.True(request.ExposedPorts.ContainsKey("80/tcp"));

        var endpoint = request.NetworkingConfig!.EndpointsConfig["app_default"];
        Assert.Equal(["web"], endpoint.Aliases);
        Assert.Equal("10.0.0.5", endpoint.IPAMConfig!.IPv4Address);
    }
}
