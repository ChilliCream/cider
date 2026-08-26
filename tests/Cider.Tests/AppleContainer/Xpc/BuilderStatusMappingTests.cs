using System.Runtime.CompilerServices;
using Cider.AppleContainer.Xpc;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Xunit;

namespace Cider.Tests.AppleContainer.Xpc;

/// <summary>
/// <see cref="XpcContainerRuntime.ToBuilderStatus"/> (task cider-ede.13): the <c>containerList{ids:
/// ["buildkit"]}</c> snapshot → <see cref="BuilderStatus"/> mapping, exercised as a pure function over
/// fixtures — no live apiserver, no <see cref="XpcClient"/> involved. The fixture
/// (<c>builder-status-mapping.json</c>) covers a running builder with an address and resources, and a
/// stopped (never-bootstrapped) one with no network attachment.
/// </summary>
public class BuilderStatusMappingTests
{
    [Fact]
    public void ToBuilderStatus_maps_a_running_builder()
    {
        var snapshot = LoadSnapshots()[0];

        var status = XpcContainerRuntime.ToBuilderStatus(snapshot);

        Assert.Equal("buildkit", status.Name);
        Assert.Equal("ghcr.io/apple/container-builder-shim/builder:0.4.0", status.Image);
        Assert.True(status.Running);
        Assert.Equal("192.168.64.9/24", status.Address);
        Assert.Equal(2, status.Cpus);
        Assert.Equal(2147483648L, status.MemoryBytes);
    }

    [Fact]
    public void ToBuilderStatus_maps_a_stopped_never_bootstrapped_builder()
    {
        var snapshot = LoadSnapshots()[1];

        var status = XpcContainerRuntime.ToBuilderStatus(snapshot);

        Assert.Equal("buildkit", status.Name);
        Assert.False(status.Running);
        // Never bootstrapped: snapshot.networks is empty, so there is no address to report — the same
        // "unknown until running" shape ParseBuilderStatus gives the CLI transport's own empty column.
        Assert.Null(status.Address);
        Assert.Equal(2, status.Cpus);
        Assert.Equal(2147483648L, status.MemoryBytes);
    }

    private static List<ContainerSnapshot> LoadSnapshots([CallerFilePath] string sourcePath = "")
    {
        var fixturePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "Fixtures", "xpc", "builder-status-mapping.json");
        return XpcJson.Deserialize<List<ContainerSnapshot>>(File.ReadAllText(fixturePath));
    }
}
