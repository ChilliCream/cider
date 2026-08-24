using Cider.Core.Configuration;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>
/// Transitional (rename to Cider): proves the dual-read migration path. Apple objects labelled by a
/// pre-rename daemon (<c>com.apple-demon.*</c>) must still be recognised, while everything written
/// from now on carries <c>com.chillicream.cider.*</c> only. Delete with the legacy label constants.
/// </summary>
public sealed class LabelMigrationTests
{
    private const string LegacyId = "b7e2c4a19f5d3e6081cabf42d7093e58a1b6c3d90e2f4718a5c6b9d0e3f21748";

    [Fact]
    public async Task A_container_labelled_before_the_rename_is_reconciled_and_listed()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/library/alpine:latest",
            Argv = ["sh"],
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerIdentity.LegacyIdLabel] = LegacyId,
                [ContainerIdentity.LegacyNameLabel] = "web",
            },
        });

        await harness.Containers.ReconcileAsync(default);

        var record = await harness.Containers.ResolveAsync(LegacyId, default);
        Assert.Equal("web", record.Name);
        Assert.True(record.Managed, "a container this daemon's predecessor created must stay managed");

        var listed = await harness.Containers.ListAsync(
            all: true, null, false, Core.DockerApi.Filters.Empty, default);
        Assert.Contains(listed, c => string.Equals(c.Id, LegacyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_system_container_labelled_before_the_rename_stays_hidden()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();

        harness.Runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "cider-dns-bridge-0badc0de",
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/coredns/coredns:1.14.7",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerManager.LegacySystemLabel] = "dns",
            },
        });

        await harness.Containers.ReconcileAsync(default);

        Assert.Empty(await harness.Containers.ListAsync(
            all: true, null, false, Core.DockerApi.Filters.Empty, default));
    }

    [Fact]
    public void Labels_are_only_ever_written_under_the_new_prefix()
    {
        var labels = ContainerIdentity.BuildLabels(LegacyId, "/web");

        Assert.Equal(LegacyId, labels[ContainerIdentity.IdLabel]);
        Assert.Equal("web", labels[ContainerIdentity.NameLabel]);
        Assert.DoesNotContain(labels.Keys, key => key.StartsWith("com.apple-demon.", StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_prefers_the_new_key_when_an_object_carries_both()
    {
        const string current = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerIdentity.LegacyIdLabel] = LegacyId,
            [ContainerIdentity.IdLabel] = current,
        };

        Assert.Equal(current, ContainerIdentity.ReadDockerId(labels));
    }

    [Fact]
    public void The_pre_rename_data_directory_produces_a_loud_move_command()
    {
        var root = NewTempDir();
        var legacy = Path.Combine(root, ".apple-demon");
        var current = Path.Combine(root, ".cider");
        Directory.CreateDirectory(Path.Combine(legacy, "state"));

        var hint = CiderOptions.LegacyDataDirHint(current, current, legacy);

        Assert.NotNull(hint);
        Assert.Contains($"rm -rf {current} && mv {legacy} {current}", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_move_hint_is_silent_once_the_new_data_directory_holds_state()
    {
        var root = NewTempDir();
        var legacy = Path.Combine(root, ".apple-demon");
        var current = Path.Combine(root, ".cider");
        Directory.CreateDirectory(Path.Combine(legacy, "state"));
        Directory.CreateDirectory(Path.Combine(current, "state"));
        File.WriteAllText(Path.Combine(current, "state", "containers.json"), "[]");

        Assert.Null(CiderOptions.LegacyDataDirHint(current, current, legacy));
    }

    [Fact]
    public void The_move_hint_is_silent_for_an_explicitly_chosen_data_directory()
    {
        var root = NewTempDir();
        var legacy = Path.Combine(root, ".apple-demon");
        var current = Path.Combine(root, ".cider");
        Directory.CreateDirectory(Path.Combine(legacy, "state"));

        Assert.Null(CiderOptions.LegacyDataDirHint(Path.Combine(root, "elsewhere"), current, legacy));
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "cider-tests", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }
}
