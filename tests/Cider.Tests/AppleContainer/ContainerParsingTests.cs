using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// Parser tests over fixtures captured verbatim from Apple container 1.2.2
/// (docs/apple-container-notes.md §3, §6, §13). No CLI is involved.
/// </summary>
public class ContainerParsingTests
{
    private const string CreatedContainerJson = """
    [
      {
        "configuration": {
          "capAdd": [], "capDrop": [],
          "creationDate": "2026-08-21T11:47:43Z",
          "dns": {"nameservers":[],"options":[],"searchDomains":[]},
          "id": "adtest1",
          "image": {
            "descriptor": {"digest":"sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce","mediaType":"application/vnd.oci.image.index.v1+json","size":9218},
            "reference": "docker.io/library/alpine:3.22"
          },
          "initProcess": {
            "arguments": ["-c","echo out; echo err 1>&2; sleep 1; exit 3"],
            "environment": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],
            "executable": "sh",
            "rlimits": [], "supplementalGroups": [], "terminal": false,
            "user": {"id":{"gid":0,"uid":0}},
            "workingDirectory": "/"
          },
          "labels": {"a":"b","com.chillicream.cider.id":"abc"},
          "mounts": [],
          "networks": [{"network":"default","options":{"hostname":"adtest1","mtu":1280}}],
          "platform": {"architecture":"arm64","os":"linux"},
          "publishedPorts": [], "publishedSockets": [],
          "readOnly": false,
          "resources": {"cpuOverhead":1,"cpus":4,"memoryInBytes":1073741824},
          "rosetta": false, "runtimeHandler": "container-runtime-linux",
          "ssh": false, "sysctls": {}, "useInit": false, "virtualization": false
        },
        "id": "adtest1",
        "status": {"networks": [], "state": "stopped"}
      }
    ]
    """;

    private const string RunningContainerJson = """
    [
      {
        "configuration": {
          "creationDate": "2026-08-21T12:06:01Z",
          "id": "adtest6",
          "image": {"descriptor":{"digest":"sha256:b5b3f7fa81e662db6929f1ad66d835d151a1b03f682cfe5f9fcb17fa46d6bcc9"},"reference":"docker.io/library/alpine:3.22"},
          "initProcess": {
            "arguments": ["300"],
            "environment": ["PATH=/usr/bin"],
            "executable": "sleep",
            "terminal": true,
            "user": {"raw":{"userString":"1000:1000"}},
            "workingDirectory": "/tmp"
          },
          "labels": {},
          "mounts": [
            {"destination":"/data","options":[],"source":"/Users/michael/Library/Application Support/com.apple.container/volumes/adtest-vol/volume.img",
             "type":{"volume":{"cache":{"on":{}},"format":"ext4","name":"adtest-vol","sync":{"fsync":{}}}}},
            {"destination":"/host","options":["ro"],"source":"/tmp/adtest-bind","type":{"virtiofs":{}}},
            {"destination":"/t","options":[],"source":"","type":{"tmpfs":{}}}
          ],
          "networks": [{"network":"default","options":{"hostname":"adtest6","mtu":1280}}],
          "platform": {"architecture":"arm64","os":"linux","variant":"v8"},
          "publishedPorts": [
            {"containerPort":80,"count":1,"hostAddress":"0.0.0.0","hostPort":18080,"proto":"tcp"},
            {"containerPort":80,"count":1,"hostAddress":"127.0.0.1","hostPort":18081,"proto":"udp"}
          ],
          "publishedSockets": [{"containerPath":"/var/run/probe.sock","hostPath":"/tmp/adtest-probe.sock"}],
          "resources": {"cpuOverhead":1,"cpus":2,"memoryInBytes":2147483648}
        },
        "id": "adtest6",
        "status": {
          "networks": [{"hostname":"adtest6","ipv4Address":"192.168.64.20/24","ipv4Gateway":"192.168.64.1","ipv6Address":"fd3e:bc7a:df05:1995:fc74:95ff:fe02:33d1/64","macAddress":"fe:74:95:02:33:d1","mtu":1280,"network":"default","variant":"reserved"}],
          "startedDate": "2026-08-21T11:48:00Z",
          "state": "running"
        }
      }
    ]
    """;

    private static List<AppleContainerJson> Parse(string json) =>
        ContainerCli.ParseJson<List<AppleContainerJson>>(json, "test")!;

    [Fact]
    public void Created_container_maps_to_stopped_with_all_configuration_fields()
    {
        var container = RuntimeMapper.ToContainer(Parse(CreatedContainerJson)[0]);

        Assert.Equal("adtest1", container.RuntimeId);

        // Apple has no "created" state: a never-started container also reports "stopped".
        Assert.Equal(RuntimeContainerState.Stopped, container.State);
        Assert.Equal("docker.io/library/alpine:3.22", container.ImageReference);
        Assert.Equal("sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce", container.ImageDigest);
        Assert.Equal(new[] { "sh", "-c", "echo out; echo err 1>&2; sleep 1; exit 3" }, container.Argv);
        Assert.Equal(new[] { "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" }, container.Env);
        Assert.Equal("/", container.WorkingDir);
        Assert.False(container.Tty);
        Assert.Equal("b", container.Labels["a"]);
        Assert.Equal("abc", container.Labels["com.chillicream.cider.id"]);
        Assert.Equal("linux/arm64", container.Platform);
        Assert.Equal(4, container.Cpus);
        Assert.Equal(1073741824, container.MemoryBytes);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T11:47:43Z", System.Globalization.CultureInfo.InvariantCulture), container.CreatedAt);
        Assert.Null(container.StartedAt);
        Assert.Empty(container.Mounts);
        Assert.Empty(container.PublishedPorts);
    }

    [Fact]
    public void Created_container_falls_back_to_requested_networks_when_not_running()
    {
        var container = RuntimeMapper.ToContainer(Parse(CreatedContainerJson)[0]);

        var attachment = Assert.Single(container.Networks);
        Assert.Equal("default", attachment.Network);
        Assert.Equal("adtest1", attachment.Hostname);
        Assert.Null(attachment.IPv4Address);
    }

    [Fact]
    public void Running_container_maps_live_networks_and_strips_the_cidr_suffix()
    {
        var container = RuntimeMapper.ToContainer(Parse(RunningContainerJson)[0]);

        Assert.Equal(RuntimeContainerState.Running, container.State);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T11:48:00Z", System.Globalization.CultureInfo.InvariantCulture), container.StartedAt);

        var attachment = Assert.Single(container.Networks);
        Assert.Equal("192.168.64.20", attachment.IPv4Address);
        Assert.Equal("192.168.64.1", attachment.IPv4Gateway);
        Assert.Equal("fe:74:95:02:33:d1", attachment.MacAddress);
        Assert.Equal("linux/arm64/v8", container.Platform);
        Assert.True(container.Tty);
    }

    [Fact]
    public void Published_ports_map_to_port_specs()
    {
        var container = RuntimeMapper.ToContainer(Parse(RunningContainerJson)[0]);

        Assert.Collection(
            container.PublishedPorts,
            tcp =>
            {
                Assert.Equal("0.0.0.0", tcp.HostIp);
                Assert.Equal(18080, tcp.HostPort);
                Assert.Equal(80, tcp.ContainerPort);
                Assert.Equal("tcp", tcp.Proto);
            },
            udp =>
            {
                Assert.Equal("127.0.0.1", udp.HostIp);
                Assert.Equal(18081, udp.HostPort);
                Assert.Equal("udp", udp.Proto);
            });
    }

    [Fact]
    public void All_three_mount_kinds_are_recognised_by_their_discriminator_key()
    {
        var container = RuntimeMapper.ToContainer(Parse(RunningContainerJson)[0]);

        Assert.Collection(
            container.Mounts,
            volume =>
            {
                Assert.Equal(MountKind.Volume, volume.Kind);

                // The volume's *name*, not the backing volume.img path.
                Assert.Equal("adtest-vol", volume.Source);
                Assert.Equal("/data", volume.Target);
                Assert.False(volume.ReadOnly);
            },
            bind =>
            {
                Assert.Equal(MountKind.Bind, bind.Kind);
                Assert.Equal("/tmp/adtest-bind", bind.Source);
                Assert.Equal("/host", bind.Target);
                Assert.True(bind.ReadOnly);
            },
            tmpfs =>
            {
                Assert.Equal(MountKind.Tmpfs, tmpfs.Kind);
                Assert.Equal("/t", tmpfs.Target);
                Assert.Equal("", tmpfs.Source);
            });
    }

    [Fact]
    public void Both_user_shapes_parse()
    {
        var resolved = Parse(CreatedContainerJson)[0].Configuration!.InitProcess!.User!;
        Assert.Equal(0, resolved.Id!.Uid);
        Assert.Equal(0, resolved.Id!.Gid);
        Assert.Null(resolved.Raw);

        var raw = Parse(RunningContainerJson)[0].Configuration!.InitProcess!.User!;
        Assert.Null(raw.Id);
        Assert.Equal("1000:1000", raw.Raw!.UserString);
    }

    [Fact]
    public void Rlimits_and_published_sockets_parse()
    {
        const string json = """
        [{"configuration":{"id":"x","initProcess":{"executable":"sh","rlimits":[{"hard":200,"limit":"RLIMIT_NOFILE","soft":100}]},
          "publishedSockets":[{"containerPath":"/var/run/probe.sock","hostPath":"/tmp/adtest-probe.sock"}]},"id":"x","status":{"state":"stopped"}}]
        """;

        var configuration = Parse(json)[0].Configuration!;
        var rlimit = Assert.Single(configuration.InitProcess!.Rlimits!);
        Assert.Equal("RLIMIT_NOFILE", rlimit.Limit);
        Assert.Equal(100, rlimit.Soft);
        Assert.Equal(200, rlimit.Hard);

        var socket = Assert.Single(configuration.PublishedSockets!);
        Assert.Equal("/tmp/adtest-probe.sock", socket.HostPath);
        Assert.Equal("/var/run/probe.sock", socket.ContainerPath);
    }

    [Theory]
    [InlineData("running", RuntimeContainerState.Running)]
    [InlineData("stopped", RuntimeContainerState.Stopped)]
    [InlineData("stopping", RuntimeContainerState.Stopping)]
    [InlineData("weird", RuntimeContainerState.Unknown)]
    [InlineData(null, RuntimeContainerState.Unknown)]
    public void State_strings_map_to_runtime_states(string? state, RuntimeContainerState expected) =>
        Assert.Equal(expected, RuntimeMapper.ToState(state));

    [Fact]
    public void Unknown_members_are_ignored()
    {
        const string json = """
        [{"configuration":{"id":"x","brandNewField":{"nested":[1,2,3]}},"id":"x","status":{"state":"running","futureKey":42}}]
        """;

        var container = RuntimeMapper.ToContainer(Parse(json)[0]);
        Assert.Equal("x", container.RuntimeId);
        Assert.Equal(RuntimeContainerState.Running, container.State);
    }
}
