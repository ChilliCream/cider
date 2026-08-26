using System.Net;
using Cider.Core.Configuration;
using Cider.Core.Events;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Daemon.Dns;
using Cider.Dns;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// cider-0o3 MAJOR #4: <see cref="DnsForwarderService.ReapOrphanedForwardersAsync"/> is a machine-wide,
/// destructive scan that used to have coverage only in <c>tests/Cider.E2E.Tests/DnsForwarderReapingTests.cs</c>
/// (gated on <c>CIDER_E2E=1</c> and a real Apple runtime, so no default build/CI run exercised it). These
/// drive the same reap logic — through <see cref="DnsForwarderService.StartAsync"/>, against
/// <see cref="FakeContainerRuntime"/> — cheaply and every run.
/// </summary>
public sealed class DnsForwarderReapingTests
{
    private static async Task<(DnsForwarderService Service, FakeContainerRuntime Runtime, CiderOptions Options)> CreateAsync()
    {
        var options = new CiderOptions
        {
            DataDir = Path.Combine(Path.GetTempPath(), "cider-dns-reap-tests", Guid.NewGuid().ToString("n")[..8]),
            DnsListen = "127.0.0.1:0",
        };
        options.EnsureDirectories();

        var runtime = new FakeContainerRuntime();
        var networks = new NetworkManager(runtime, new InMemoryRecordStore<NetworkRecord>(), new EventBus(), NullLogger<NetworkManager>.Instance);

        // So CleanupStaleForwardersAsync (which StartAsync runs right before the reap this suite
        // targets) recognises "bridge" as a live network and leaves our own-hash fixture container
        // alone for the reason this suite is testing, not by accident.
        await networks.EnsureDefaultAsync(CancellationToken.None);

        var service = new DnsForwarderService(
            runtime, networks, new NullDnsResolver(), options, NullLogger<DnsForwarderService>.Instance);

        return (service, runtime, options);
    }

    private static void Seed(FakeContainerRuntime runtime, string runtimeId, Dictionary<string, string> labels) =>
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = runtimeId,
            State = RuntimeContainerState.Running,
            ImageReference = "docker.io/coredns/coredns:1.14.7",
            Labels = labels,
        });

    private static Dictionary<string, string> ForwarderLabels(string hash, string? network = "bridge", string? path = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DnsForwarderService.SystemLabel] = "dns",
            [DnsForwarderService.DataDirLabel] = hash,
        };
        if (network is not null)
        {
            labels[DnsForwarderService.NetworkLabel] = network;
        }

        if (path is not null)
        {
            labels[DnsForwarderService.DataDirPathLabel] = path;
        }

        return labels;
    }

    [Fact]
    public async Task Own_hash_is_never_reaped()
    {
        var (service, runtime, options) = await CreateAsync();
        var ownHash = DnsForwarderService.DataDirHash(options.DataDir);
        Seed(runtime, "cider-dns-web-" + ownHash, ForwarderLabels(ownHash));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();

        Assert.NotNull(runtime.GetContainer("cider-dns-web-" + ownHash));
        Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("RemoveContainerAsync:cider-dns-web-" + ownHash, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hash_with_a_still_existing_data_dir_is_not_reaped()
    {
        var (service, runtime, _) = await CreateAsync();

        // ComputeLiveDataDirHashes only scans literal /tmp/cider-* (plus the real default and this
        // instance's own DataDir) -- this is the E2E fixture's/compat harness's own convention.
        var liveDir = Path.Combine("/tmp", "cider-reap-live-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(liveDir);
        try
        {
            var otherHash = DnsForwarderService.DataDirHash(liveDir);
            Seed(runtime, "cider-dns-web-" + otherHash, ForwarderLabels(otherHash));

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync();

            Assert.NotNull(runtime.GetContainer("cider-dns-web-" + otherHash));
            Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("RemoveContainerAsync:cider-dns-web-" + otherHash, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(liveDir, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_hash_with_no_live_data_dir_is_reaped()
    {
        var (service, runtime, _) = await CreateAsync();
        const string deadHash = "deadbee0";
        Seed(runtime, "cider-dns-web-" + deadHash, ForwarderLabels(deadHash));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();

        Assert.Contains("RemoveContainerAsync:cider-dns-web-" + deadHash + ":True", runtime.Calls);
        Assert.Null(runtime.GetContainer("cider-dns-web-" + deadHash));
    }

    [Fact]
    public async Task Labelled_path_to_a_live_but_unconventional_data_dir_is_not_reaped()
    {
        var (service, runtime, _) = await CreateAsync();

        // Deliberately outside both conventions ComputeLiveDataDirHashes scans (~/.cider and literal
        // /tmp/cider-*): Path.GetTempPath() on macOS resolves through $TMPDIR to a per-user
        // /var/folders/.../T/ path, not /tmp -- exactly the "custom --data-dir" case the path label
        // exists to cover, since the hash-set heuristic alone would misjudge it as orphaned.
        var unconventionalDir = Path.Combine(Path.GetTempPath(), "cider-unconventional-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(unconventionalDir);
        try
        {
            const string hash = "f00dcafe";
            Seed(runtime, "cider-dns-web-" + hash, ForwarderLabels(hash, path: unconventionalDir));

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync();

            Assert.NotNull(runtime.GetContainer("cider-dns-web-" + hash));
            Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("RemoveContainerAsync:cider-dns-web-" + hash, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(unconventionalDir, recursive: true);
        }
    }

    [Fact]
    public async Task Labelled_path_to_a_gone_data_dir_is_reaped_even_if_its_hash_would_look_live()
    {
        var (service, runtime, _) = await CreateAsync();

        // A path that never existed, so the label alone (not the hash-set heuristic) must drive the
        // decision, per the "never fall back to the hash heuristic once the path label is present" rule.
        var goneDir = Path.Combine("/tmp", "cider-reap-gone-" + Guid.NewGuid().ToString("n")[..8]);
        const string hash = "0ff1ce00";
        Seed(runtime, "cider-dns-web-" + hash, ForwarderLabels(hash, path: goneDir));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();

        Assert.Contains("RemoveContainerAsync:cider-dns-web-" + hash + ":True", runtime.Calls);
    }

    [Fact]
    public async Task Non_dns_system_label_is_left_untouched()
    {
        var (service, runtime, _) = await CreateAsync();
        var labels = ForwarderLabels("deadbee1");
        labels[DnsForwarderService.SystemLabel] = "buildkit";
        Seed(runtime, "some-other-system-container", labels);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync();

        Assert.NotNull(runtime.GetContainer("some-other-system-container"));
        Assert.DoesNotContain(runtime.Calls, call => call.StartsWith("RemoveContainerAsync:some-other-system-container", StringComparison.Ordinal));
    }

    [Fact]
    public void IsOrphanedForwarder_decision_matrix()
    {
        var liveHashes = new HashSet<string>(StringComparer.Ordinal) { "own00000", "live0000" };

        // Own hash: never orphaned, regardless of the live set.
        Assert.False(DnsForwarderService.IsOrphanedForwarder(ForwarderLabels("own00000"), liveHashes, "own00000"));

        // Hash present in the live set: not orphaned.
        Assert.False(DnsForwarderService.IsOrphanedForwarder(ForwarderLabels("live0000"), liveHashes, "own00000"));

        // Unknown hash, no live entry: orphaned.
        Assert.True(DnsForwarderService.IsOrphanedForwarder(ForwarderLabels("dead0000"), liveHashes, "own00000"));

        // Path label present and the path exists: never orphaned, even for a hash absent from the live set.
        Assert.False(DnsForwarderService.IsOrphanedForwarder(
            ForwarderLabels("dead0000", path: Path.GetTempPath()), liveHashes, "own00000"));

        // Path label present but the path is gone: orphaned, even for a hash the live set happens to contain
        // (the path label always wins over the hash heuristic once present).
        Assert.True(DnsForwarderService.IsOrphanedForwarder(
            ForwarderLabels("live0000", path: "/tmp/cider-does-not-exist-xyz"), liveHashes, "own00000"));

        // Not a DNS forwarder at all: never orphaned (not applicable).
        var nonDns = ForwarderLabels("dead0000");
        nonDns[DnsForwarderService.SystemLabel] = "buildkit";
        Assert.False(DnsForwarderService.IsOrphanedForwarder(nonDns, liveHashes, "own00000"));
    }

    private sealed class NullDnsResolver : IDnsResolver
    {
        public ValueTask<DnsAnswer?> ResolveAsync(DnsQuestion question, IPEndPoint client, CancellationToken ct) =>
            ValueTask.FromResult<DnsAnswer?>(null);
    }
}
