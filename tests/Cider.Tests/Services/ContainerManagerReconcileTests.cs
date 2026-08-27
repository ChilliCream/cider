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
    public async Task Reconcile_of_a_record_whose_runtime_container_is_gone_completes_a_pending_docker_wait()
    {
        // cider-1ki: ReconcileAsync's startup missing-record branch is the fourth observer of a
        // record leaving the running state and was the last one still flipping the record to exited
        // and persisting without completing NextExit. A waiter is unlikely at startup, but the
        // branch is reachable from any later ReconcileAsync call, and the point of cider-ede.33 is
        // that exit completion belongs to the transition rather than to whoever observed it.
        await using var harness = await ContainerTestHarness.CreateAsync();

        var record = await harness.CreateAsync("alpine", "web");
        record.State.Status = "running";
        harness.Store.Upsert(record.Id, record);
        harness.Runtime.VanishContainer("web");

        var nextExit = harness.Containers.WaitAsync(record.Id, "next-exit", default);
        var notRunning = harness.Containers.WaitAsync(record.Id, "not-running", default);

        await harness.Containers.ReconcileAsync(default);

        Assert.Equal("exited", record.State.Status);
        var nextExitResponse = await nextExit.WaitAsync(TimeSpan.FromSeconds(2));
        var notRunningResponse = await notRunning.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(record.State.ExitCode, nextExitResponse.StatusCode);
        Assert.Equal(record.State.ExitCode, notRunningResponse.StatusCode);
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

    /// <summary>
    /// cider-ede.29: an adopted container carries the engine's raw (index) digest in
    /// <see cref="RuntimeContainer.ImageDigest"/> — not the content-addressed config digest
    /// <c>docker images</c> reports as the image's id since cider-ger.19. Adoption must resolve that
    /// raw digest to the matching <see cref="RuntimeImage.Id"/> via <see cref="RuntimeImage.IndexDigests"/>
    /// so <c>docker inspect</c>'s <c>.Image</c> agrees with <c>docker images -q</c>.
    /// </summary>
    [Fact]
    public async Task Reconcile_resolves_an_adopted_containers_image_digest_to_the_images_config_digest_id()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        const string configDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        const string rawIndexDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        harness.Runtime.SeedImage(new RuntimeImageDetail
        {
            Id = configDigest,
            References = ["docker.io/library/alpine:latest"],
            IndexDigests = [rawIndexDigest],
        });
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            ImageDigest = rawIndexDigest,
            Argv = ["sh"],
        });

        await harness.Containers.ReconcileAsync(default);

        var record = Assert.Single(harness.Store.GetAll());
        Assert.Equal(configDigest, record.ImageId);

        var inspected = await harness.Containers.InspectAsync(record.Id, size: false, default);
        Assert.Equal(configDigest, inspected.Image);
    }

    /// <summary>
    /// cider-ede.29: when the adopted container's image cannot be resolved (deleted underneath a
    /// running container, or the store cannot be listed), adoption must keep the raw engine digest
    /// rather than losing it to an empty string, and must not fail the adoption itself.
    /// </summary>
    [Fact]
    public async Task Reconcile_keeps_the_raw_engine_digest_when_the_adopted_containers_image_is_not_resolvable()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        const string unresolvableDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            ImageDigest = unresolvableDigest,
            Argv = ["sh"],
        });

        await harness.Containers.ReconcileAsync(default);

        var record = Assert.Single(harness.Store.GetAll());
        Assert.Equal(unresolvableDigest, record.ImageId);
    }
}
