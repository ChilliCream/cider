using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class VolumeManagerTests : IDisposable
{
    private readonly string _tmpDir = Directory.CreateTempSubdirectory("ad-volume-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (VolumeManager Manager, FakeContainerRuntime Runtime, InMemoryRecordStore<VolumeRecord> Store) CreateManager()
    {
        var runtime = new FakeContainerRuntime();
        var store = new InMemoryRecordStore<VolumeRecord>();
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir };
        var manager = new VolumeManager(runtime, store, events, options, NullLogger<VolumeManager>.Instance);
        return (manager, runtime, store);
    }

    [Fact]
    public async Task CreateAsync_SameNameTwice_IsIdempotent()
    {
        var (manager, _, store) = CreateManager();
        var request = new VolumeCreateRequest { Name = "data" };

        var first = await manager.CreateAsync(request, CancellationToken.None);
        var second = await manager.CreateAsync(new VolumeCreateRequest { Name = "data" }, CancellationToken.None);

        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task CreateAsync_NoName_GeneratesSixtyFourHexName()
    {
        var (manager, _, _) = CreateManager();

        var volume = await manager.CreateAsync(new VolumeCreateRequest(), CancellationToken.None);

        Assert.Equal(64, volume.Name.Length);
    }

    [Fact]
    public async Task ListInspectRemove_RoundTrip()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new VolumeCreateRequest { Name = "data" }, CancellationToken.None);

        var list = await manager.ListAsync(Filters.Empty, CancellationToken.None);
        Assert.Contains(list.Volumes, v => v.Name == "data");

        var inspected = await manager.InspectAsync("data", CancellationToken.None);
        Assert.EndsWith(Path.Combine("data", "_data"), inspected.Mountpoint, StringComparison.Ordinal);

        await manager.RemoveAsync("data", force: false, CancellationToken.None);
        Assert.Null(store.Get("data"));
    }

    [Fact]
    public async Task InspectAsync_UnknownVolume_ThrowsNoSuchVolume()
    {
        var (manager, _, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync("missing", CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task CreateAsync_UnknownDriver_Is404_AndNoVolumeIsCreated()
    {
        var (manager, _, store) = CreateManager();

        // Volumes here are host directories: `local` is the only driver there is. Accepting `nfs`
        // used to answer 201 and then report `"Driver":"local"` back, so the client believed it had
        // an nfs volume. dockerd fails the plugin lookup, with a 404 not a 400.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.CreateAsync(new VolumeCreateRequest { Name = "data", Driver = "nfs" }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("create data: error looking up volume plugin nfs: plugin \"nfs\" not found", ex.Message);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task CreateAsync_LocalOrUnsetDriver_IsAccepted()
    {
        var (manager, _, _) = CreateManager();

        var explicitLocal = await manager.CreateAsync(
            new VolumeCreateRequest { Name = "one", Driver = "local" }, CancellationToken.None);
        var unset = await manager.CreateAsync(new VolumeCreateRequest { Name = "two" }, CancellationToken.None);

        Assert.Equal("local", explicitLocal.Driver);
        Assert.Equal("local", unset.Driver);
    }

    [Fact]
    public async Task PruneAsync_UnknownFilterKey_Is400_AndNothingIsRemoved()
    {
        var (manager, _, store) = CreateManager();
        await manager.CreateAsync(new VolumeCreateRequest { Name = "data" }, CancellationToken.None);

        // dockerd validates prune filter keys per endpoint; ignoring an unknown one means a mistyped
        // guard prunes exactly what it was written to protect.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"bogus":["x"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'bogus'", ex.Message);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task PruneAsync_UntilFilter_Is400_BecauseVolumePruneDoesNotAcceptIt()
    {
        var (manager, _, _) = CreateManager();

        // `until` is accepted by container/image/network prune but not by volume prune — dockerd's
        // acceptedPruneFilters for volumes is label/label!/all.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"until":["10h"]}"""), CancellationToken.None));

        Assert.Equal("invalid filter 'until'", ex.Message);
    }

    [Fact]
    public async Task RemoveAsync_VolumeInUse_WithoutForce_Throws409()
    {
        var (manager, runtime, _) = CreateManager();
        await manager.CreateAsync(new VolumeCreateRequest { Name = "data" }, CancellationToken.None);
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            Mounts = [new MountSpec { Kind = MountKind.Volume, Source = "data", Target = "/data" }],
        });

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.RemoveAsync("data", force: false, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("volume is in use", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruneAsync_RemovesOnlyUnusedVolumes()
    {
        var (manager, runtime, store) = CreateManager();
        await manager.CreateAsync(new VolumeCreateRequest { Name = "unused" }, CancellationToken.None);
        await manager.CreateAsync(new VolumeCreateRequest { Name = "used" }, CancellationToken.None);
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            Mounts = [new MountSpec { Kind = MountKind.Volume, Source = "used", Target = "/data" }],
        });

        var result = await manager.PruneAsync(Filters.Empty, CancellationToken.None);

        Assert.Contains("unused", result.VolumesDeleted);
        Assert.DoesNotContain("used", result.VolumesDeleted);
        Assert.Null(store.Get("unused"));
        Assert.NotNull(store.Get("used"));
    }
}
