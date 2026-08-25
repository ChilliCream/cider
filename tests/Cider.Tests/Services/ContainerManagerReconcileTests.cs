using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>
/// Apple's builder VM (<c>buildkit</c>, labelled <c>com.apple.container.plugin=builder</c> /
/// <c>com.apple.container.resource.role=builder</c>) is the Apple CLI's own build cache, not a
/// Docker container: <see cref="ContainerManager.IsSystemContainer"/> must hide it from
/// reconcile/list the same way it hides the DNS forwarders, and an already-adopted record from an
/// older daemon must be dropped on the next reconcile rather than left behind as a phantom
/// <c>docker ps -a</c> entry.
/// </summary>
public sealed class ContainerManagerReconcileTests
{
    private static RuntimeContainer Builder(
        string runtimeId = "buildkit",
        IReadOnlyDictionary<string, string>? labels = null) => new()
        {
            RuntimeId = runtimeId,
            State = RuntimeContainerState.Running,
            ImageReference = "vminit",
            Labels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["com.apple.container.plugin"] = "builder",
                ["com.apple.container.resource.role"] = "builder",
            },
        };

    [Theory]
    [InlineData("com.apple.container.resource.role", "builder")]
    [InlineData("com.apple.container.plugin", "builder")]
    public void IsSystemContainer_is_true_for_either_apple_builder_label(string key, string value)
    {
        var container = Builder(labels: new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

        Assert.True(ContainerManager.IsSystemContainer(container));
    }

    [Fact]
    public void IsSystemContainer_is_true_for_the_bare_buildkit_runtime_id_with_no_cider_labels()
    {
        // Belt and braces: even an unlabelled "buildkit" (older Apple runtime versions, or a
        // labelless inspect) must not be adopted as a normal container.
        var container = Builder(labels: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.True(ContainerManager.IsSystemContainer(container));
    }

    [Fact]
    public void IsSystemContainer_is_false_for_a_buildkit_named_container_cider_itself_created()
    {
        // A user container that happens to be named "buildkit" but carries Cider's own id label
        // (i.e. cider created it) must not be swept up by the belt-and-braces runtime-id guard.
        var container = Builder(labels: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerIdentity.IdLabel] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            [ContainerIdentity.NameLabel] = "buildkit",
        });

        Assert.False(ContainerManager.IsSystemContainer(container));
    }

    [Fact]
    public void IsSystemContainer_still_recognises_the_dns_forwarder_label()
    {
        var container = new RuntimeContainer
        {
            RuntimeId = "cider-dns-bridge-abc",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerManager.SystemLabel] = "dns",
            },
        };

        Assert.True(ContainerManager.IsSystemContainer(container));
    }

    [Fact]
    public async Task Reconcile_never_adopts_apples_builder_container()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        harness.Runtime.SeedContainer(Builder());

        await harness.Containers.ReconcileAsync(default);

        var listed = await harness.Containers.ListAsync(all: true, null, false, Filters.Empty, default);
        Assert.Empty(listed);
        Assert.Empty(harness.Store.GetAll());
    }

    [Fact]
    public async Task Reconcile_drops_a_previously_adopted_builder_record()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        // The runtime still has the builder VM running...
        harness.Runtime.SeedContainer(Builder());

        // ...but an older daemon (before this filter existed) had already adopted it as a
        // read-only Managed=false record, the way ContainerManager.AdoptContainer would have.
        harness.Store.Upsert("buildkit-record-id", new ContainerRecord
        {
            Id = "buildkit-record-id",
            Name = "buildkit",
            RuntimeId = "buildkit",
            Managed = false,
            Request = new ContainerCreateRequest { Image = "vminit" },
            State = new ContainerState { Status = "running" },
        });

        await harness.Containers.ReconcileAsync(default);

        Assert.Empty(harness.Store.GetAll());

        var listed = await harness.Containers.ListAsync(all: true, null, false, Filters.Empty, default);
        Assert.Empty(listed);

        // docker rm/stop/start buildkit now hit the normal "no such container" path.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Containers.RemoveAsync("buildkit", force: true, removeVolumes: false, default));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task Reconcile_still_adopts_an_unrelated_unmanaged_container()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            Argv = ["sh"],
        });

        await harness.Containers.ReconcileAsync(default);

        var listed = await harness.Containers.ListAsync(all: true, null, false, Filters.Empty, default);
        Assert.Contains(listed, c => string.Equals(c.Names.FirstOrDefault()?.TrimStart('/'), "web", StringComparison.Ordinal));
    }
}
