using Cider.Core.DockerApi.Models;
using Cider.Core.State;
using Xunit;

namespace Cider.Tests.State;

public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cider-tests", Guid.NewGuid().ToString("n")[..12]);

    [Fact]
    public void Upsert_persists_and_a_new_store_loads_it_back()
    {
        var store = new JsonFileStore<ContainerRecord>(_directory);
        var record = NewRecord("c1", "web");
        record.State.Status = "running";
        record.Ports["80/tcp"] = [new PortBinding { HostIp = "0.0.0.0", HostPort = "32768" }];

        store.Upsert(record.Id, record);

        var reloaded = new JsonFileStore<ContainerRecord>(_directory);
        var loaded = reloaded.Get("c1");

        Assert.NotNull(loaded);
        Assert.Equal("web", loaded.Name);
        Assert.Equal("running", loaded.State.Status);
        Assert.Equal("32768", loaded.Ports["80/tcp"][0].HostPort);
        Assert.Equal("alpine:latest", loaded.Request.Image);
    }

    [Fact]
    public void Upsert_writes_atomically_and_leaves_no_temporary_files()
    {
        var store = new JsonFileStore<ContainerRecord>(_directory);
        store.Upsert("c1", NewRecord("c1", "web"));
        store.Upsert("c1", NewRecord("c1", "web2"));

        var files = Directory.GetFiles(_directory);
        Assert.Single(files);
        Assert.EndsWith(".json", files[0], StringComparison.Ordinal);
        Assert.Equal("web2", store.Get("c1")!.Name);
    }

    [Fact]
    public void GetAll_TryGet_and_Delete_behave()
    {
        var store = new JsonFileStore<ContainerRecord>(_directory);
        store.Upsert("a", NewRecord("a", "one"));
        store.Upsert("b", NewRecord("b", "two"));

        Assert.Equal(2, store.GetAll().Count);
        Assert.True(store.TryGet("a", out var found));
        Assert.Equal("one", found!.Name);
        Assert.False(store.TryGet("missing", out var missing));
        Assert.Null(missing);

        Assert.True(store.Delete("a"));
        Assert.False(store.Delete("a"));
        Assert.Null(store.Get("a"));
        Assert.Single(store.GetAll());
        Assert.Single(Directory.GetFiles(_directory));

        var reloaded = new JsonFileStore<ContainerRecord>(_directory);
        Assert.Single(reloaded.GetAll());
    }

    [Fact]
    public void Keys_that_are_not_file_safe_round_trip()
    {
        var store = new JsonFileStore<ContainerRecord>(_directory);
        store.Upsert("weird/key:1", NewRecord("weird/key:1", "odd"));

        var reloaded = new JsonFileStore<ContainerRecord>(_directory);
        Assert.Equal("odd", reloaded.Get("weird/key:1")!.Name);
    }

    [Fact]
    public void Corrupt_files_are_skipped_instead_of_throwing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ this is not json");

        var store = new JsonFileStore<ContainerRecord>(_directory);
        Assert.Empty(store.GetAll());
    }

    private static ContainerRecord NewRecord(string id, string name) => new()
    {
        Id = id,
        Name = name,
        RuntimeId = name,
        Created = DateTimeOffset.UtcNow,
        Request = new ContainerCreateRequest { Image = "alpine:latest" },
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
