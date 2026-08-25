using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="ContainerConfigurationBuilder.Build"/> (task cider-ede.6) as a pure function over
/// fixtures — no XPC, no live apiserver. Covers the task's own verification-section scenarios (a)-(f):
/// a plain create, bind+volume+tmpfs mounts, hostname/sysctl/stop-signal, <c>--network none</c>,
/// <c>--privileged</c>, and fractional cpus/memory rounding with the 200 MiB floor.
/// </summary>
public class ContainerConfigurationBuilderTests
{
    private static readonly ImageDescription Image = new()
    {
        Reference = "docker.io/library/alpine:3.20",
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:abc", Size = 9226 },
    };

    private static ContainerSpec PlainSpec(string id = "myapp") => new()
    {
        RuntimeId = id,
        Image = "docker.io/library/alpine:3.20",
        Args = ["sleep", "1"],
        Networks = ["default"],
    };

    // ---- (a) plain `docker run alpine sleep 1` ----------------------------------------------------

    [Fact]
    public void Build_maps_a_plain_create()
    {
        var config = ContainerConfigurationBuilder.Build(PlainSpec(), Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal("myapp", config.Id);
        Assert.Same(Image, config.Image);
        Assert.Equal("sleep", config.InitProcess.Executable);
        Assert.Equal(["1"], config.InitProcess.Arguments);
        Assert.Equal("/", config.InitProcess.WorkingDirectory);
        Assert.False(config.InitProcess.Terminal);
        Assert.NotNull(config.InitProcess.User.Id);
        Assert.Equal(0, config.InitProcess.User.Id!.Uid);
        Assert.Equal(0, config.InitProcess.User.Id!.Gid);
        Assert.Empty(config.InitProcess.SupplementalGroups);
        Assert.Empty(config.InitProcess.Rlimits);

        Assert.Equal(4, config.Resources.Cpus);
        Assert.Equal(1024UL * 1024 * 1024, config.Resources.MemoryInBytes);
        Assert.Equal(1, config.Resources.CpuOverhead);

        var attachment = Assert.Single(config.Networks);
        Assert.Equal("default", attachment.Network);
        Assert.Equal("myapp", attachment.Options.Hostname);
        Assert.Equal(1280u, attachment.Options.Mtu);

        Assert.Equal("container-runtime-linux", config.RuntimeHandler);
        Assert.False(config.ReadOnly);
        Assert.False(config.UseInit);
        Assert.Empty(config.CapAdd);
        Assert.Empty(config.CapDrop);
        Assert.Null(config.MaskedPaths);
        Assert.Null(config.ReadonlyPaths);
        Assert.Null(config.StopSignal);
        Assert.Null(config.ShmSize);
    }

    [Fact]
    public void Build_uses_entrypoint_when_set_and_keeps_args_separate()
    {
        var spec = PlainSpec() with { Entrypoint = "/bin/sh", Args = ["-c", "sleep 3600"] };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal("/bin/sh", config.InitProcess.Executable);
        Assert.Equal(["-c", "sleep 3600"], config.InitProcess.Arguments);
    }

    [Fact]
    public void Build_throws_invalid_argument_when_there_is_no_entrypoint_or_command()
    {
        var spec = PlainSpec() with { Args = [] };

        var ex = Assert.Throws<RuntimeException>(() =>
            ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty));

        Assert.Equal(RuntimeErrorKind.InvalidArgument, ex.Kind);
    }

    [Fact]
    public void Build_filters_environment_entries_without_an_equals_sign()
    {
        var spec = PlainSpec() with { Env = ["PATH=/usr/bin", "MALFORMED", "FOO=bar"] };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(["PATH=/usr/bin", "FOO=bar"], config.InitProcess.Environment);
    }

    [Theory]
    [InlineData("1000", 1000, 0)]
    [InlineData("1000:2000", 1000, 2000)]
    public void Build_parses_numeric_user_as_id(string user, int uid, int gid)
    {
        var spec = PlainSpec() with { User = user };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.NotNull(config.InitProcess.User.Id);
        Assert.Null(config.InitProcess.User.Raw);
        Assert.Equal(uid, config.InitProcess.User.Id!.Uid);
        Assert.Equal(gid, config.InitProcess.User.Id!.Gid);
    }

    [Fact]
    public void Build_treats_a_non_numeric_user_as_raw()
    {
        var spec = PlainSpec() with { User = "nonroot" };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Null(config.InitProcess.User.Id);
        Assert.NotNull(config.InitProcess.User.Raw);
        Assert.Equal("nonroot", config.InitProcess.User.Raw!.UserString);
    }

    [Fact]
    public void Build_maps_ulimits_to_named_rlimits()
    {
        var spec = PlainSpec() with
        {
            Ulimits = [new UlimitSpec { Name = "nofile", Soft = 1024, Hard = 2048 }],
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        var rlimit = Assert.Single(config.InitProcess.Rlimits);
        Assert.Equal("RLIMIT_NOFILE", rlimit.Limit);
        Assert.Equal(1024UL, rlimit.Soft);
        Assert.Equal(2048UL, rlimit.Hard);
    }

    // ---- id validation ------------------------------------------------------------------------

    [Theory]
    [InlineData("a")] // single-character ids are rejected (the `+` quantifier)
    [InlineData("")]
    [InlineData("has a space")]
    [InlineData("-leading-dash")]
    public void Build_rejects_an_invalid_container_id(string id)
    {
        var spec = PlainSpec(id);

        var ex = Assert.Throws<RuntimeException>(() =>
            ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty));

        Assert.Equal(RuntimeErrorKind.InvalidArgument, ex.Kind);
    }

    [Fact]
    public void Build_rejects_a_container_id_over_63_characters()
    {
        var spec = PlainSpec(new string('a', 64));

        var ex = Assert.Throws<RuntimeException>(() =>
            ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty));

        Assert.Equal(RuntimeErrorKind.InvalidArgument, ex.Kind);
    }

    // ---- (b) bind + volume + tmpfs mounts -----------------------------------------------------

    [Fact]
    public void Build_maps_bind_volume_and_tmpfs_mounts()
    {
        var spec = PlainSpec() with
        {
            Mounts =
            [
                new MountSpec { Kind = MountKind.Bind, Source = "/host/data", Target = "/data", ReadOnly = true },
                new MountSpec { Kind = MountKind.Volume, Source = "myvol", Target = "/vol" },
            ],
            Tmpfs = [new TmpfsSpec { Target = "/run" }],
        };

        var volumes = new Dictionary<string, VolumeConfiguration>(StringComparer.Ordinal)
        {
            ["myvol"] = new VolumeConfiguration
            {
                Name = "myvol",
                Driver = "local",
                Format = "ext4",
                Source = "/var/volumes/myvol",
            },
        };

        var config = ContainerConfigurationBuilder.Build(
            spec, Image, new ContainerConfigurationBuilder.BuildContext(volumes, DnsDomain: null));

        Assert.Equal(3, config.Mounts.Count);

        var bind = config.Mounts[0];
        Assert.NotNull(bind.Type.Virtiofs);
        Assert.Equal("/host/data", bind.Source);
        Assert.Equal("/data", bind.Destination);
        Assert.Contains("ro", bind.Options);

        var volume = config.Mounts[1];
        Assert.NotNull(volume.Type.Volume);
        Assert.Equal("myvol", volume.Type.Volume!.Name);
        Assert.Equal("ext4", volume.Type.Volume!.Format);
        Assert.Equal("on", volume.Type.Volume!.Cache.CaseName);
        Assert.Equal("fsync", volume.Type.Volume!.Sync.CaseName);
        Assert.Equal("/var/volumes/myvol", volume.Source);
        Assert.Equal("/vol", volume.Destination);
        Assert.DoesNotContain("ro", volume.Options);

        var tmpfs = config.Mounts[2];
        Assert.NotNull(tmpfs.Type.Tmpfs);
        Assert.Equal("tmpfs", tmpfs.Source);
        Assert.Equal("/run", tmpfs.Destination);
    }

    [Fact]
    public void Build_throws_not_found_for_a_volume_mount_missing_from_the_context()
    {
        var spec = PlainSpec() with
        {
            Mounts = [new MountSpec { Kind = MountKind.Volume, Source = "missing", Target = "/vol" }],
        };

        var ex = Assert.Throws<RuntimeException>(() =>
            ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty));

        Assert.Equal(RuntimeErrorKind.NotFound, ex.Kind);
    }

    // ---- (c) --hostname db --sysctl net.core.somaxconn=1024 --stop-signal SIGINT ---------------

    [Fact]
    public void Build_maps_hostname_sysctls_and_stop_signal()
    {
        var spec = PlainSpec() with
        {
            Hostname = "db",
            Sysctls = new Dictionary<string, string> { ["net.core.somaxconn"] = "1024" },
            StopSignal = "SIGINT",
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        var attachment = Assert.Single(config.Networks);
        Assert.Equal("db", attachment.Options.Hostname);
        Assert.Equal("1024", config.Sysctls["net.core.somaxconn"]);
        Assert.Equal("SIGINT", config.StopSignal);
    }

    [Fact]
    public void Build_applies_the_hostname_override_to_every_attachment()
    {
        var spec = PlainSpec() with { Hostname = "db", Networks = ["default", "extra"] };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(2, config.Networks.Count);
        Assert.All(config.Networks, n => Assert.Equal("db", n.Options.Hostname));
    }

    // ---- attachment FQDN rule (§3.4): no explicit --hostname, dns.domain plumbed through --------

    [Fact]
    public void Build_applies_the_fqdn_rule_to_only_the_first_attachment_when_no_hostname_is_set()
    {
        var spec = PlainSpec() with { Hostname = null, Networks = ["default", "extra"] };
        var context = new ContainerConfigurationBuilder.BuildContext(
            new Dictionary<string, VolumeConfiguration>(), DnsDomain: "test");

        var config = ContainerConfigurationBuilder.Build(spec, Image, context);

        Assert.Equal(2, config.Networks.Count);
        Assert.Equal("myapp.test.", config.Networks[0].Options.Hostname);
        Assert.Equal("myapp", config.Networks[1].Options.Hostname);
    }

    [Fact]
    public void Build_fqdn_rule_uses_the_bare_dotted_id_when_the_id_already_contains_a_dot()
    {
        var spec = PlainSpec("my.app") with { Hostname = null };
        var context = new ContainerConfigurationBuilder.BuildContext(
            new Dictionary<string, VolumeConfiguration>(), DnsDomain: "test");

        var config = ContainerConfigurationBuilder.Build(spec, Image, context);

        var attachment = Assert.Single(config.Networks);
        Assert.Equal("my.app.", attachment.Options.Hostname);
    }

    [Fact]
    public void Build_fqdn_rule_falls_back_to_the_bare_id_when_no_domain_is_configured()
    {
        var spec = PlainSpec() with { Hostname = null };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        var attachment = Assert.Single(config.Networks);
        Assert.Equal("myapp", attachment.Options.Hostname);
    }

    [Fact]
    public void Build_explicit_hostname_overrides_the_fqdn_rule_even_with_a_domain_configured()
    {
        var spec = PlainSpec() with { Hostname = "db" };
        var context = new ContainerConfigurationBuilder.BuildContext(
            new Dictionary<string, VolumeConfiguration>(), DnsDomain: "test");

        var config = ContainerConfigurationBuilder.Build(spec, Image, context);

        var attachment = Assert.Single(config.Networks);
        Assert.Equal("db", attachment.Options.Hostname);
    }

    [Theory]
    [InlineData("15", "SIGTERM")]
    [InlineData("TERM", "SIGTERM")]
    [InlineData("SIGTERM", "SIGTERM")]
    [InlineData("int", "SIGINT")]
    public void Build_normalizes_the_stop_signal(string input, string expected)
    {
        var spec = PlainSpec() with { StopSignal = input };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(expected, config.StopSignal);
    }

    // ---- (d) --network none --------------------------------------------------------------------

    [Fact]
    public void Build_maps_network_none_to_an_empty_networks_list()
    {
        var spec = PlainSpec() with { Networks = [] };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Empty(config.Networks);
    }

    // ---- (e) --privileged ------------------------------------------------------------------------

    [Fact]
    public void Build_maps_privileged_to_cap_all_and_empty_typed_masked_paths()
    {
        var spec = PlainSpec() with { Privileged = true };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Contains("ALL", config.CapAdd);
        Assert.NotNull(config.MaskedPaths);
        Assert.Empty(config.MaskedPaths!);
        Assert.NotNull(config.ReadonlyPaths);
        Assert.Empty(config.ReadonlyPaths!);
    }

    [Theory]
    [InlineData("sys_admin", "CAP_SYS_ADMIN")]
    [InlineData("CAP_NET_ADMIN", "CAP_NET_ADMIN")]
    [InlineData("ALL", "ALL")]
    public void Build_normalizes_capability_names(string input, string expected)
    {
        var spec = PlainSpec() with { CapAdd = [input] };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Contains(expected, config.CapAdd);
    }

    // ---- (f) fractional cpus/memory rounding and the 200 MiB floor ------------------------------

    [Theory]
    [InlineData(1.4, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)] // AwayFromZero, not banker's rounding
    [InlineData(0.2, 1)] // floor of 1 cpu even when the request rounds to 0
    public void Build_rounds_fractional_cpus(double requested, int expected)
    {
        var spec = PlainSpec() with { Cpus = requested };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(expected, config.Resources.Cpus);
    }

    [Fact]
    public void Build_floors_memory_at_200_mebibytes()
    {
        var spec = PlainSpec() with { MemoryBytes = 100L * 1024 * 1024 };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(200UL * 1024 * 1024, config.Resources.MemoryInBytes);
    }

    [Fact]
    public void Build_passes_through_memory_above_the_floor()
    {
        var spec = PlainSpec() with { MemoryBytes = 512L * 1024 * 1024 };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal(512UL * 1024 * 1024, config.Resources.MemoryInBytes);
    }

    // ---- ports / sockets / labels ----------------------------------------------------------------

    [Fact]
    public void Build_maps_published_ports()
    {
        var spec = PlainSpec() with
        {
            Ports = [new PortSpec { HostIp = "0.0.0.0", HostPort = 8080, ContainerPort = 80, Proto = "tcp" }],
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        var port = Assert.Single(config.PublishedPorts);
        Assert.Equal("0.0.0.0", port.HostAddress);
        Assert.Equal((ushort)8080, port.HostPort);
        Assert.Equal((ushort)80, port.ContainerPort);
        Assert.Equal("tcp", port.Proto);
        Assert.Equal((ushort)1, port.Count);
    }

    [Fact]
    public void Build_carries_labels_through_verbatim()
    {
        var spec = PlainSpec() with
        {
            Labels = new Dictionary<string, string> { ["com.chillicream.cider.system"] = "app" },
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);

        Assert.Equal("app", config.Labels["com.chillicream.cider.system"]);
    }

    // ---- rosetta / platform --------------------------------------------------------------------

    [Fact]
    public void ResolveTargetPlatform_defaults_to_the_host_platform_when_unset()
    {
        var platform = ContainerConfigurationBuilder.ResolveTargetPlatform(null);

        Assert.Equal("linux", platform.Os);
        Assert.Equal(Platform.Current.Architecture, platform.Architecture);
    }

    [Fact]
    public void ResolveTargetPlatform_parses_os_arch_variant()
    {
        var platform = ContainerConfigurationBuilder.ResolveTargetPlatform("linux/arm64/v8");

        Assert.Equal("linux", platform.Os);
        Assert.Equal("arm64", platform.Architecture);
        Assert.Equal("v8", platform.Variant);
    }

    // ---- signal normalization -----------------------------------------------------------------

    [Theory]
    [InlineData("9", "SIGKILL")]
    [InlineData("KILL", "SIGKILL")]
    [InlineData("SIGKILL", "SIGKILL")]
    [InlineData(null, "SIGKILL")]
    public void NormalizeSignal_produces_the_canonical_sig_prefixed_form(string? input, string expected)
    {
        Assert.Equal(expected, ContainerConfigurationBuilder.NormalizeSignal(input, "KILL"));
    }
}
