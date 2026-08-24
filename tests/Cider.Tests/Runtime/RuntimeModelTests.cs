using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.Runtime;

/// <summary>The runtime records must be usable with only the fields a caller actually cares about.</summary>
public class RuntimeModelTests
{
    [Fact]
    public void ContainerSpec_needs_only_a_runtime_id_and_an_image()
    {
        var spec = new ContainerSpec { RuntimeId = "web", Image = "alpine:latest" };

        Assert.Equal("web", spec.RuntimeId);
        Assert.Empty(spec.Args);
        Assert.Empty(spec.Env);
        Assert.Empty(spec.Mounts);
        Assert.Empty(spec.Ports);
        Assert.Empty(spec.Networks);
        Assert.Empty(spec.Labels);
        Assert.Null(spec.Cpus);
        Assert.False(spec.Tty);
    }

    [Fact]
    public void Records_support_with_expressions()
    {
        var spec = new ContainerSpec { RuntimeId = "web", Image = "alpine" };
        var tty = spec with { Tty = true };

        Assert.False(spec.Tty);
        Assert.True(tty.Tty);
        Assert.Equal(spec.RuntimeId, tty.RuntimeId);
    }

    [Fact]
    public void RuntimeImageDetail_extends_RuntimeImage()
    {
        RuntimeImage image = new RuntimeImageDetail
        {
            Id = "sha256:abc",
            References = ["docker.io/library/alpine:latest"],
            Config = new ImageConfig { Cmd = ["/bin/sh"], Env = ["PATH=/usr/bin"] },
            Architecture = "arm64",
            Os = "linux",
        };

        var detail = Assert.IsType<RuntimeImageDetail>(image);
        Assert.Equal("sha256:abc", detail.Id);
        Assert.Equal(["/bin/sh"], detail.Config.Cmd);
        Assert.Empty(detail.Layers);
        Assert.Empty(detail.Config.Entrypoint);
        Assert.Null(detail.Config.Healthcheck);
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        Assert.Equal(RuntimeContainerState.Unknown, new RuntimeContainer { RuntimeId = "x" }.State);
        Assert.Equal("tcp", new PortSpec { ContainerPort = 80 }.Proto);
        Assert.Equal("local", new RuntimeVolume { Name = "data" }.Driver);
        Assert.Equal("apple-container", new RuntimeInfo().Name);
        Assert.Equal("Dockerfile", new BuildSpec { ContextDir = "/tmp/ctx" }.Dockerfile);
        Assert.False(new StartOptions().AttachStdin);
    }

    [Fact]
    public void RuntimeException_carries_a_kind()
    {
        var error = RuntimeException.NotFound("no such container");

        Assert.Equal(RuntimeErrorKind.NotFound, error.Kind);
        Assert.Equal("no such container", error.Message);
        Assert.Equal(RuntimeErrorKind.NotSupported, RuntimeException.NotSupported("nope").Kind);
        Assert.Equal(RuntimeErrorKind.Unavailable, new RuntimeException(RuntimeErrorKind.Unavailable, "apiserver down").Kind);
    }

    [Fact]
    public void MountSpec_and_TmpfsSpec_shapes()
    {
        var bind = new MountSpec { Kind = MountKind.Bind, Source = "/host", Target = "/data" };
        var volume = new MountSpec { Kind = MountKind.Volume, Source = "data", Target = "/data", ReadOnly = true };
        var tmpfs = new TmpfsSpec { Target = "/tmp", SizeBytes = 1024 };

        Assert.Equal(MountKind.Bind, bind.Kind);
        Assert.True(volume.ReadOnly);
        Assert.False(bind.ReadOnly);
        Assert.Equal(1024, tmpfs.SizeBytes);
    }
}
