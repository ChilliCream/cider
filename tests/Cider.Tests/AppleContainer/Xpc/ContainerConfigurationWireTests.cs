using System.Text.Json;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="ContainerConfigurationBuilder.Build"/>'s output serialized through the real
/// <see cref="XpcJson"/>/<see cref="XpcJsonContext"/> pipeline (task cider-ede.6 fix direction §4) —
/// <see cref="ContainerConfigurationBuilderTests"/> only ever asserts on the .NET object graph, never
/// the bytes that actually go over the wire, so this checks the verbatim camelCase key names and the
/// single-key union encodings (<c>{"virtiofs":{}}</c>, <c>{"volume":{...,"cache":{"on":{}}}}</c>) a
/// silent naming-policy or converter regression would otherwise slip past every other test.
/// </summary>
public class ContainerConfigurationWireTests
{
    private static readonly ImageDescription Image = new()
    {
        Reference = "docker.io/library/alpine:3.20",
        Descriptor = new Descriptor { MediaType = "application/vnd.oci.image.index.v1+json", Digest = "sha256:abc", Size = 9226 },
    };

    [Fact]
    public void Serializes_a_plain_create_with_verbatim_camelCase_wire_keys()
    {
        var spec = new ContainerSpec
        {
            RuntimeId = "myapp",
            Image = "docker.io/library/alpine:3.20",
            Entrypoint = "sleep",
            Args = ["1"],
            Networks = ["default"],
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);
        using var document = JsonDocument.Parse(XpcJson.SerializeToUtf8Bytes(config));
        var root = document.RootElement;

        Assert.Equal("myapp", root.GetProperty("id").GetString());
        Assert.Equal("sleep", root.GetProperty("initProcess").GetProperty("executable").GetString());
        Assert.Equal("1", root.GetProperty("initProcess").GetProperty("arguments")[0].GetString());
        Assert.Equal("container-runtime-linux", root.GetProperty("runtimeHandler").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("useInit").ValueKind);

        var network = root.GetProperty("networks")[0];
        Assert.Equal("default", network.GetProperty("network").GetString());
        Assert.Equal("myapp", network.GetProperty("options").GetProperty("hostname").GetString());

        // §2.0 rule 11 (custom init(from:)): only id/image/initProcess are required, so a field the
        // Swift side must not miss has to actually be present, not merely correct once parsed.
        Assert.True(root.TryGetProperty("resources", out var resources));
        Assert.Equal(4, resources.GetProperty("cpus").GetInt32());

        // MaskedPaths/ReadonlyPaths are null (unprivileged) and DefaultIgnoreCondition.WhenWritingNull
        // must actually omit them, not write a JSON null the apiserver's decoder could choke on.
        Assert.False(root.TryGetProperty("maskedPaths", out _));
        Assert.False(root.TryGetProperty("readonlyPaths", out _));
    }

    [Fact]
    public void Serializes_mount_fs_types_as_single_key_union_objects()
    {
        var spec = new ContainerSpec
        {
            RuntimeId = "myapp",
            Image = "docker.io/library/alpine:3.20",
            Entrypoint = "sleep",
            Args = ["1"],
            Networks = [],
            Mounts =
            [
                new MountSpec { Kind = MountKind.Bind, Source = "/host/data", Target = "/data" },
                new MountSpec { Kind = MountKind.Volume, Source = "myvol", Target = "/vol" },
                new MountSpec { Kind = MountKind.Tmpfs, Source = "", Target = "/tmp" },
            ],
        };

        var volumes = new Dictionary<string, VolumeConfiguration>
        {
            ["myvol"] = new VolumeConfiguration
            {
                Name = "myvol",
                Driver = "local",
                Format = "ext4",
                Source = "/var/lib/cider/volumes/myvol/_data",
            },
        };

        var context = new ContainerConfigurationBuilder.BuildContext(volumes, DnsDomain: null);
        var config = ContainerConfigurationBuilder.Build(spec, Image, context);
        using var document = JsonDocument.Parse(XpcJson.SerializeToUtf8Bytes(config));
        var mounts = document.RootElement.GetProperty("mounts");

        Assert.Equal(3, mounts.GetArrayLength());

        var bind = mounts[0].GetProperty("type");
        Assert.True(bind.GetProperty("virtiofs").ValueKind == JsonValueKind.Object);
        Assert.Empty(bind.GetProperty("virtiofs").EnumerateObject());

        var volume = mounts[1].GetProperty("type").GetProperty("volume");
        Assert.Equal("myvol", volume.GetProperty("name").GetString());
        Assert.Equal("ext4", volume.GetProperty("format").GetString());
        Assert.True(volume.GetProperty("cache").TryGetProperty("on", out _));
        Assert.True(volume.GetProperty("sync").TryGetProperty("fsync", out _));

        var tmpfs = mounts[2].GetProperty("type");
        Assert.True(tmpfs.GetProperty("tmpfs").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public void Serializes_user_as_a_single_key_union_object()
    {
        var spec = new ContainerSpec
        {
            RuntimeId = "myapp",
            Image = "docker.io/library/alpine:3.20",
            Entrypoint = "sleep",
            Args = ["1"],
            Networks = [],
            User = "1000:1000",
        };

        var config = ContainerConfigurationBuilder.Build(spec, Image, ContainerConfigurationBuilder.BuildContext.Empty);
        using var document = JsonDocument.Parse(XpcJson.SerializeToUtf8Bytes(config));
        var user = document.RootElement.GetProperty("initProcess").GetProperty("user");

        var id = user.GetProperty("id");
        Assert.Equal(1000, id.GetProperty("uid").GetInt32());
        Assert.Equal(1000, id.GetProperty("gid").GetInt32());
        Assert.False(user.TryGetProperty("raw", out _));
    }
}
