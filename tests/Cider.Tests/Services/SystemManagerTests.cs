using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.Events;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class SystemManagerTests : IDisposable
{
    private readonly string _tmpDir = Directory.CreateTempSubdirectory("ad-system-").FullName;

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

    private sealed class FakeContainerCounts : IContainerCounts
    {
        public int Total { get; set; }
        public int Running { get; set; }
        public int Exited { get; set; }

        public int Count(string? status = null) => status switch
        {
            "running" => Running,
            "exited" => Exited,
            _ => Total,
        };
    }

    private (SystemManager Manager, FakeContainerCounts Counts) CreateManager()
    {
        var runtime = new FakeContainerRuntime();
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir };
        var images = new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance);
        var volumeStore = new InMemoryRecordStore<Cider.Core.State.VolumeRecord>();
        var volumes = new VolumeManager(runtime, volumeStore, events, options, NullLogger<VolumeManager>.Instance);
        var counts = new FakeContainerCounts { Total = 3, Running = 1, Exited = 2 };
        var engineId = new EngineId(options);
        var manager = new SystemManager(runtime, counts, images, volumes, options, engineId);
        return (manager, counts);
    }

    [Fact]
    public async Task VersionAsync_ReportsPlainNumericVersionAndEngineComponent()
    {
        var (manager, _) = CreateManager();

        var version = await manager.VersionAsync(CancellationToken.None);

        Assert.Equal("29.0.0", version.Version);
        Assert.Equal("1.47", version.ApiVersion);
        Assert.Equal("1.24", version.MinAPIVersion);
        Assert.Equal("linux", version.Os);
        Assert.Equal("arm64", version.Arch);
        Assert.Contains("cider", version.Platform.Name, StringComparison.Ordinal);
        var engine = Assert.Single(version.Components, c => c.Name == "Engine");
        Assert.Equal("1.47", engine.Details["ApiVersion"]);

        Assert.False(version.Experimental);
        Assert.NotEmpty(version.GitCommit);
        Assert.NotEmpty(version.KernelVersion);
        Assert.False(string.IsNullOrEmpty(engine.Details["GitCommit"]));
        Assert.Equal(version.KernelVersion, engine.Details["KernelVersion"]);
        Assert.Equal("false", engine.Details["Experimental"]);
    }

    [Fact]
    public async Task VersionAsync_GitCommit_is_stable_across_calls()
    {
        var (manager, _) = CreateManager();

        var first = await manager.VersionAsync(CancellationToken.None);
        var second = await manager.VersionAsync(CancellationToken.None);

        Assert.Equal(first.GitCommit, second.GitCommit);
        Assert.Equal(first.BuildTime, second.BuildTime);
    }

    // The old ReadCommandOutput blocked in ReadToEnd BEFORE its WaitForExit(2000),
    // so a child that never closed stdout parked a Kestrel request thread forever. This test targets
    // the bounded replacement; against the old shape the equivalent call simply never returned.
    [Fact]
    public async Task A_child_that_never_closes_stdout_does_not_hang_the_caller()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var output = await HostFacts.ReadCommandOutputAsync(
            "/bin/sh", "-c \"sleep 30\"", TimeSpan.FromMilliseconds(300));

        clock.Stop();
        Assert.Equal("", output);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(10),
            $"the probe must return at its own timeout, not the child's: took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_fast_child_reports_its_trimmed_output()
    {
        var output = await HostFacts.ReadCommandOutputAsync(
            "/bin/sh", "-c \"echo '  host-fact  '\"", TimeSpan.FromSeconds(5));

        Assert.Equal("host-fact", output);
    }

    [Fact]
    public async Task A_missing_binary_reports_empty_rather_than_throwing()
    {
        var output = await HostFacts.ReadCommandOutputAsync(
            "/does/not/exist-xfm", "", TimeSpan.FromSeconds(1));

        Assert.Equal("", output);
    }

    // The host facts are probed once per process — but only a SUCCESSFUL probe is
    // memoized. A transient first failure must not pin ""/0 for the daemon's lifetime (the old code
    // re-probed every call and so self-healed; review finding).
    [Fact]
    public async Task A_successful_probe_runs_at_most_once()
    {
        var probes = 0;
        var fact = new HostFacts.ProbedFact<string>(
            () => { probes++; return Task.FromResult("26.6.2"); },
            value => value.Length > 0);

        Assert.Equal("26.6.2", await fact.GetAsync());
        Assert.Equal("26.6.2", await fact.GetAsync());
        Assert.Equal("26.6.2", await fact.GetAsync());
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task A_failed_probe_is_not_cached_and_heals_on_the_next_call()
    {
        var probes = 0;
        var fact = new HostFacts.ProbedFact<string>(
            () => Task.FromResult(++probes < 3 ? "" : "26.6.2"),
            value => value.Length > 0);

        Assert.Equal("", await fact.GetAsync());       // transient failure: served, not cached
        Assert.Equal("", await fact.GetAsync());
        Assert.Equal("26.6.2", await fact.GetAsync()); // success: cached from here on
        Assert.Equal("26.6.2", await fact.GetAsync());
        Assert.Equal(3, probes);
    }

    [Fact]
    public async Task InfoAsync_serves_the_cached_host_facts()
    {
        var (manager, _) = CreateManager();

        var first = await manager.InfoAsync(CancellationToken.None);
        var second = await manager.InfoAsync(CancellationToken.None);

        // Cached: identical across calls. On a real Mac both probes succeed (non-empty version,
        // positive memory); when the probe genuinely failed the documented fallbacks apply.
        Assert.Equal(first.OperatingSystem, second.OperatingSystem);
        Assert.Equal(first.MemTotal, second.MemTotal);
        Assert.Equal(await HostFacts.MemTotalBytes, first.MemTotal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Matches(@"macOS \d+", first.OperatingSystem);
            Assert.True(first.MemTotal > 0, "hw.memsize must parse on macOS");
        }
    }

    [Fact]
    public async Task InfoAsync_ReportsContainerAndImageCounts()
    {
        var (manager, _) = CreateManager();

        var info = await manager.InfoAsync(CancellationToken.None);

        Assert.Equal(3, info.Containers);
        Assert.Equal(1, info.ContainersRunning);
        Assert.Equal(2, info.ContainersStopped);
        Assert.Equal(0, info.ContainersPaused);
        Assert.Equal(4, info.Images);
        Assert.Equal("apple-container", info.Driver);
        Assert.Equal("linux", info.OSType);
        Assert.Equal("aarch64", info.Architecture);
        Assert.Equal("inactive", info.Swarm.LocalNodeState);
        Assert.Equal(["local"], info.Plugins.Volume);
        Assert.Null(info.Plugins.Authorization);
        Assert.True(Guid.TryParse(info.ID, out _));
    }

    [Fact]
    public async Task InfoAsync_ID_is_stable_across_calls_and_daemon_restarts()
    {
        var (manager, _) = CreateManager();

        var first = await manager.InfoAsync(CancellationToken.None);
        var second = await manager.InfoAsync(CancellationToken.None);
        Assert.Equal(first.ID, second.ID);

        // Simulate a restart: a fresh EngineId reading the same data directory must recover the
        // same id rather than minting a new one.
        var options = new CiderOptions { DataDir = _tmpDir };
        var reloaded = new EngineId(options);
        Assert.Equal(first.ID, reloaded.Value);
    }

    [Fact]
    public void Ping_ReportsApiVersionAndBuilderVersion()
    {
        var (manager, _) = CreateManager();

        var ping = manager.Ping();

        Assert.Equal("1.47", ping.ApiVersion);
        Assert.Equal("2", ping.BuilderVersion);
        Assert.False(ping.Experimental);
        Assert.Equal("linux", ping.OsType);
        Assert.Equal("inactive", ping.Swarm);
    }

    [Fact]
    public void Ping_ReportsBuilderVersion1_When_BuildKitDisabled()
    {
        var runtime = new FakeContainerRuntime();
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir, BuildKitEnabled = false };
        var images = new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance);
        var volumeStore = new InMemoryRecordStore<Cider.Core.State.VolumeRecord>();
        var volumes = new VolumeManager(runtime, volumeStore, events, options, NullLogger<VolumeManager>.Instance);
        var counts = new FakeContainerCounts { Total = 3, Running = 1, Exited = 2 };
        var engineId = new EngineId(options);
        var manager = new SystemManager(runtime, counts, images, volumes, options, engineId);

        var ping = manager.Ping();

        Assert.Equal("1", ping.BuilderVersion);
    }

    [Fact]
    public async Task InfoAsync_ReportsCliTransport_WhenRuntimeIsCliBacked()
    {
        var (manager, _) = CreateManager();

        var info = await manager.InfoAsync(CancellationToken.None);

        Assert.Equal("cli", info.Runtimes["apple-container"].Path);
    }

    /// <summary>
    /// Task cider-ede.14: <c>/info</c> must report which transport is actually serving calls
    /// (<c>xpc</c>/<c>cli</c>), never the CLI binary path — even though nothing in this fake actually
    /// runs the CLI, <see cref="FakeContainerRuntime.IsXpcTransport"/> alone must flip the reported
    /// value (docs/spikes/xpc/01-cider-runtime-map.md §6).
    /// </summary>
    [Fact]
    public async Task InfoAsync_ReportsXpcTransport_WhenRuntimeIsXpcBacked()
    {
        var runtime = new FakeContainerRuntime { IsXpcTransport = true };
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir };
        var images = new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance);
        var volumeStore = new InMemoryRecordStore<Cider.Core.State.VolumeRecord>();
        var volumes = new VolumeManager(runtime, volumeStore, events, options, NullLogger<VolumeManager>.Instance);
        var counts = new FakeContainerCounts { Total = 3, Running = 1, Exited = 2 };
        var engineId = new EngineId(options);
        var manager = new SystemManager(runtime, counts, images, volumes, options, engineId);

        var info = await manager.InfoAsync(CancellationToken.None);

        Assert.Equal("xpc", info.Runtimes["apple-container"].Path);
    }
}
