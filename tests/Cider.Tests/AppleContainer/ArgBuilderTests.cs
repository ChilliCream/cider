using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>Spec → exact <c>container</c> argv (ARCHITECTURE.md §9).</summary>
public class ArgBuilderTests
{
    [Fact]
    public void Minimal_create_is_name_image_args()
    {
        var args = ArgBuilder.Create(new ContainerSpec
        {
            RuntimeId = "web",
            Image = "alpine:3.22",
            Args = ["sh", "-c", "echo hi"],
        });

        Assert.Equal(new[] { "create", "--name", "web", "alpine:3.22", "sh", "-c", "echo hi" }, args);
    }

    [Fact]
    public void Full_create_emits_every_flag_in_order()
    {
        var spec = new ContainerSpec
        {
            RuntimeId = "web",
            Image = "alpine:3.22",
            Platform = "linux/amd64",
            Entrypoint = "/entry.sh",
            Args = ["serve", "--port", "80"],
            Env = ["A=1", "B=2"],
            WorkingDir = "/srv",
            User = "1000:1000",
            Tty = true,
            OpenStdin = true,
            Labels = new Dictionary<string, string> { ["com.chillicream.cider.id"] = "abc" },
            Mounts =
            [
                new MountSpec { Kind = MountKind.Bind, Source = "/tmp/host", Target = "/host" },
                new MountSpec { Kind = MountKind.Volume, Source = "data", Target = "/data", ReadOnly = true },
                new MountSpec { Kind = MountKind.Tmpfs, Target = "/scratch" },
            ],
            Tmpfs = [new TmpfsSpec { Target = "/run", SizeBytes = 1024 }],
            Ports =
            [
                new PortSpec { HostPort = 18080, ContainerPort = 80 },
                new PortSpec { HostIp = "127.0.0.1", HostPort = 18081, ContainerPort = 53, Proto = "udp" },
            ],
            Networks = ["default", "adtest-net"],
            DnsServers = ["192.168.64.1"],
            DnsSearch = ["example.test"],
            DnsOptions = ["ndots:1"],
            Cpus = 2,
            MemoryBytes = 2L * 1024 * 1024 * 1024,
            CapAdd = ["NET_ADMIN"],
            CapDrop = ["MKNOD"],
            ReadOnlyRootfs = true,
            ShmSizeBytes = 64 * 1024 * 1024,
            Init = true,
            Ulimits = [new UlimitSpec { Name = "nofile", Soft = 100, Hard = 200 }],
            PublishSockets = ["/tmp/x.sock:/var/run/x.sock"],
        };

        Assert.Equal(
            new[]
            {
                "create", "--name", "web",
                "-e", "A=1", "-e", "B=2",
                "-w", "/srv",
                "-u", "1000:1000",
                "-t", "-i",
                "-l", "com.chillicream.cider.id=abc",
                "-v", "/tmp/host:/host",
                "-v", "data:/data:ro",
                "--mount", "type=tmpfs,target=/scratch",
                "--tmpfs", "/run",
                "-p", "18080:80",
                "-p", "127.0.0.1:18081:53/udp",
                "--network", "default",
                "--network", "adtest-net",
                "--dns", "192.168.64.1",
                "--dns-search", "example.test",
                "--dns-option", "ndots:1",
                "-c", "2",
                "-m", "2048M",
                "--cap-add", "NET_ADMIN",
                "--cap-drop", "MKNOD",
                "--platform", "linux/amd64",
                "--read-only",
                "--shm-size", "64M",
                "--init",
                "--ulimit", "nofile=100:200",
                "--publish-socket", "/tmp/x.sock:/var/run/x.sock",
                "--entrypoint", "/entry.sh",
                "alpine:3.22", "serve", "--port", "80",
            },
            ArgBuilder.Create(spec));
    }

    [Fact]
    public void Privileged_adds_all_capabilities_and_clears_the_experimental_paths()
    {
        var args = ArgBuilder.Create(new ContainerSpec
        {
            RuntimeId = "priv",
            Image = "alpine:3.22",
            Privileged = true,
        });

        Assert.Equal(
            new[]
            {
                "create", "--name", "priv",
                "--cap-add", "ALL",
                "--masked-path", "NONE",
                "--read-only-path", "NONE",
                "alpine:3.22",
            },
            args);
    }

    [Fact]
    public void Create_never_detaches_or_auto_removes()
    {
        var args = ArgBuilder.Create(new ContainerSpec { RuntimeId = "x", Image = "alpine" });

        Assert.DoesNotContain("--rm", args);
        Assert.DoesNotContain("-d", args);
        Assert.DoesNotContain("--detach", args);
    }

    [Theory]
    [InlineData(1, "1M")]
    [InlineData(1024 * 1024, "1M")]
    [InlineData(1024 * 1024 + 1, "2M")]
    [InlineData(512L * 1024 * 1024, "512M")]
    [InlineData(2L * 1024 * 1024 * 1024, "2048M")]
    public void Memory_is_rounded_up_to_whole_mebibytes(long bytes, string expected) =>
        Assert.Equal(expected, ArgBuilder.FormatMebibytes(bytes));

    [Theory]
    [InlineData("", 8080, 80, "tcp", "8080:80")]
    [InlineData("0.0.0.0", 8080, 80, "tcp", "8080:80")]
    [InlineData("127.0.0.1", 8081, 80, "tcp", "127.0.0.1:8081:80")]
    [InlineData("", 8082, 53, "udp", "8082:53/udp")]
    [InlineData("127.0.0.1", 8083, 53, "udp", "127.0.0.1:8083:53/udp")]
    public void Ports_render_in_docker_syntax(string ip, int hostPort, int containerPort, string proto, string expected) =>
        Assert.Equal(
            expected,
            ArgBuilder.FormatPort(new PortSpec { HostIp = ip, HostPort = hostPort, ContainerPort = containerPort, Proto = proto }));

    [Fact]
    public void Start_is_always_attached_and_only_adds_stdin_when_asked()
    {
        Assert.Equal(new[] { "start", "-a", "web" }, ArgBuilder.Start("web", attachStdin: false));
        Assert.Equal(new[] { "start", "-a", "-i", "web" }, ArgBuilder.Start("web", attachStdin: true));
    }

    [Fact]
    public void Exec_emits_flags_then_the_container_then_argv()
    {
        var args = ArgBuilder.Exec("web", new ExecSpec
        {
            Argv = ["sh", "-c", "id"],
            Env = ["A=1"],
            WorkingDir = "/tmp",
            User = "root",
            Tty = true,
            OpenStdin = true,
        });

        Assert.Equal(
            new[] { "exec", "-i", "-t", "-e", "A=1", "-w", "/tmp", "-u", "root", "web", "sh", "-c", "id" },
            args);
    }

    [Fact]
    public void Exec_without_options_is_bare()
    {
        var args = ArgBuilder.Exec("web", new ExecSpec { Argv = ["true"] });
        Assert.Equal(new[] { "exec", "web", "true" }, args);
    }

    [Fact]
    public void Build_emits_plain_progress_and_every_option()
    {
        var args = ArgBuilder.Build(
            new BuildSpec
            {
                ContextDir = "/tmp/ctx",
                Dockerfile = "docker/Dockerfile",
                Tags = ["a:1", "b:2"],
                BuildArgs = new Dictionary<string, string> { ["MYARG"] = "hello" },
                Labels = new Dictionary<string, string> { ["l"] = "v" },
                Target = "final",
                Platforms = ["linux/arm64"],
                NoCache = true,
                Pull = true,
                Quiet = true,
                Cpus = 4,
                MemoryBytes = 1024L * 1024 * 1024,
            },
            ["a:1", "b:2"]);

        Assert.Equal(
            new[]
            {
                "build", "--progress", "plain",
                "-f", "/tmp/ctx/docker/Dockerfile",
                "-t", "a:1", "-t", "b:2",
                "--build-arg", "MYARG=hello",
                "-l", "l=v",
                "--target", "final",
                "--platform", "linux/arm64",
                "--no-cache", "--pull", "-q",
                "-c", "4",
                "-m", "1024M",
                "/tmp/ctx",
            },
            args);
    }

    [Fact]
    public void Build_keeps_an_absolute_dockerfile_outside_the_context()
    {
        var spec = new BuildSpec { ContextDir = "/tmp/ctx", Dockerfile = "/elsewhere/Dockerfile.custom" };
        Assert.Equal("/elsewhere/Dockerfile.custom", ArgBuilder.ResolveDockerfile(spec));
    }

    [Fact]
    public void Network_create_maps_the_spec()
    {
        var args = ArgBuilder.CreateNetwork(new NetworkSpec
        {
            Name = "adtest-net",
            Internal = true,
            Labels = new Dictionary<string, string> { ["a"] = "b" },
            Options = new Dictionary<string, string> { ["k"] = "v" },
            Subnet = "192.168.70.0/24",
            SubnetV6 = "fd00::/64",
        });

        Assert.Equal(
            new[]
            {
                "network", "create", "--internal",
                "--label", "a=b",
                "--option", "k=v",
                "--subnet", "192.168.70.0/24",
                "--subnet-v6", "fd00::/64",
                "adtest-net",
            },
            args);
    }

    [Fact]
    public void Volume_create_maps_the_spec()
    {
        var args = ArgBuilder.CreateVolume(new VolumeSpec
        {
            Name = "adtest-vol",
            Labels = new Dictionary<string, string> { ["x"] = "y" },
            Options = new Dictionary<string, string> { ["o"] = "1" },
            SizeBytes = 1048576,
        });

        Assert.Equal(
            new[] { "volume", "create", "--label", "x=y", "--opt", "o=1", "-s", "1048576", "adtest-vol" },
            args);
    }

    [Theory]
    [InlineData(null, "TERM")]
    [InlineData("", "TERM")]
    [InlineData("SIGKILL", "KILL")]
    [InlineData("KILL", "KILL")]
    [InlineData("sigterm", "TERM")]
    [InlineData("9", "KILL")]
    [InlineData("15", "TERM")]
    [InlineData("SIGUSR1", "USR1")]
    public void Signals_lose_the_sig_prefix(string? input, string expected) =>
        Assert.Equal(expected, ArgBuilder.NormalizeSignal(input));
}
