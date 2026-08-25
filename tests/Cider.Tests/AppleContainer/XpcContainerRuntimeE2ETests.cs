using Cider.AppleContainer;
using Cider.AppleContainer.Xpc;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// Live checks that <see cref="XpcContainerRuntime"/>'s read paths (task cider-ede.5) answer the same
/// content as <see cref="AppleContainerRuntime"/> for whatever containers/networks/volumes already
/// exist on this machine, and that <c>containerList</c> is dramatically faster than the CLI transport
/// (the task's own verification section). Runs only with <c>CIDER_E2E=1</c> (<see cref="E2EFactAttribute"/>).
///
/// Read-only by construction: every call here is a list/inspect/stats/disk-usage read — nothing
/// creates, deletes, starts, or stops anything, so this suite is safe to run against the user's live
/// apiserver and its already-running containers, concurrently with the rest of the
/// "apple-container-e2e" collection.
/// </summary>
[Collection("apple-container-e2e")]
public class XpcContainerRuntimeE2ETests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(1);

    private static string ResolveCliPath()
    {
        var configured = Environment.GetEnvironmentVariable("CIDER_CONTAINER_CLI");
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        return File.Exists("/usr/local/bin/container") ? "/usr/local/bin/container" : "container";
    }

    /// <summary>One CLI-backed runtime and one XPC runtime wrapping that very same CLI runtime as its
    /// own fallback — mirrors exactly what <c>RuntimeTransportSelector</c> wires up in production.
    /// Caller disposes the returned <see cref="XpcContainerRuntime"/> (which owns and disposes its two
    /// <see cref="XpcClient"/>s); the CLI runtime holds no disposable resources of its own.</summary>
    private static (XpcContainerRuntime Xpc, AppleContainerRuntime Cli) NewRuntimes()
    {
        var options = new AppleContainerOptions { CliPath = ResolveCliPath() };
        var cli = new AppleContainerRuntime(options, NullLogger<AppleContainerRuntime>.Instance);
        var apiserver = new XpcClient(RuntimeTransportSelector.ApiServerService, NullLogger.Instance);
        var images = new XpcClient(RuntimeTransportSelector.ImagesService, NullLogger.Instance);
        var capabilities = new RuntimeCapabilities { Transport = RuntimeTransportKind.Xpc };
        var xpc = new XpcContainerRuntime(
            cli, apiserver, images, capabilities, options, NullLogger<XpcContainerRuntime>.Instance);
        return (xpc, cli);
    }

    [E2EFact]
    public async Task ListContainers_agrees_with_the_CLI_transport_for_every_container_seen_by_both()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, cli) = NewRuntimes();
        using var _ = xpc;

        var cliContainers = await cli.ListContainersAsync(ct);
        var xpcContainers = await xpc.ListContainersAsync(ct);

        var xpcById = xpcContainers.ToDictionary(c => c.RuntimeId, StringComparer.Ordinal);

        // Compare only containers both transports actually saw: a container created/removed by
        // something else on this machine between the two enumerations must not make this flaky. Not
        // asserting the intersection is non-empty either: a freshly provisioned machine may have zero
        // containers, and this test must still pass — there is simply nothing to compare.
        foreach (var cliContainer in cliContainers)
        {
            if (xpcById.TryGetValue(cliContainer.RuntimeId, out var xpcContainer))
            {
                AssertContainersEquivalent(cliContainer, xpcContainer);
            }
        }
    }

    [E2EFact]
    public async Task InspectContainer_agrees_with_the_CLI_transport_and_both_report_null_for_a_missing_id()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, cli) = NewRuntimes();
        using var _ = xpc;

        var missingId = $"cider-e2e-definitely-missing-{Guid.NewGuid():N}";
        Assert.Null(await cli.InspectContainerAsync(missingId, ct));
        Assert.Null(await xpc.InspectContainerAsync(missingId, ct));

        var existing = await cli.ListContainersAsync(ct);
        if (existing.Count == 0)
        {
            return;
        }

        var id = existing[0].RuntimeId;
        var cliContainer = await cli.InspectContainerAsync(id, ct);
        var xpcContainer = await xpc.InspectContainerAsync(id, ct);

        Assert.NotNull(cliContainer);
        Assert.NotNull(xpcContainer);
        AssertContainersEquivalent(cliContainer!, xpcContainer!);
    }

    [E2EFact]
    public async Task GetStats_agrees_with_the_CLI_transport_for_a_running_container_and_both_answer_null_for_a_missing_id()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, cli) = NewRuntimes();
        using var _ = xpc;

        var missingId = $"cider-e2e-definitely-missing-{Guid.NewGuid():N}";
        Assert.Null(await cli.GetStatsAsync(missingId, ct));
        Assert.Null(await xpc.GetStatsAsync(missingId, ct));

        var running = (await cli.ListContainersAsync(ct)).FirstOrDefault(c => c.State == RuntimeContainerState.Running);
        if (running is null)
        {
            return;
        }

        var cliStats = await cli.GetStatsAsync(running.RuntimeId, ct);
        var xpcStats = await xpc.GetStatsAsync(running.RuntimeId, ct);

        // Both come from the same live process; only assert both transports actually answered
        // (agree on "stats exist"), not exact byte equality — a real sample taken microseconds apart
        // on two separate calls can legitimately differ.
        Assert.Equal(cliStats is null, xpcStats is null);
    }

    [E2EFact]
    public async Task GetInfo_reports_a_ready_apiserver_with_a_parseable_version_and_app_root()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, _) = NewRuntimes();
        using var _ = xpc;

        var info = await xpc.GetInfoAsync(ct);

        Assert.True(info.Ready);
        Assert.StartsWith("1.", info.Version, StringComparison.Ordinal);
        Assert.NotNull(info.AppRoot);
        Assert.StartsWith("/", info.AppRoot, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task GetDiskUsage_answers_over_XPC_with_non_negative_counts()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, _) = NewRuntimes();
        using var _ = xpc;

        var usage = await xpc.GetDiskUsageAsync(ct);

        Assert.True(usage.ImagesBytes >= 0);
        Assert.True(usage.ImagesCount >= 0);
        Assert.True(usage.ContainersCount >= 0);
        Assert.True(usage.VolumesCount >= 0);
    }

    [E2EFact]
    public async Task ListNetworks_and_InspectNetwork_agree_with_the_CLI_transport_for_the_default_network()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, cli) = NewRuntimes();
        using var _ = xpc;

        var cliNetworks = await cli.ListNetworksAsync(ct);
        var xpcNetworks = await xpc.ListNetworksAsync(ct);

        var cliDefault = Assert.Single(cliNetworks, n => n.Name == "default");
        var xpcDefault = Assert.Single(xpcNetworks, n => n.Name == "default");

        Assert.Equal(cliDefault.Mode, xpcDefault.Mode);
        Assert.Equal(cliDefault.Subnet, xpcDefault.Subnet);
        Assert.Equal(cliDefault.Gateway, xpcDefault.Gateway);

        var inspected = await xpc.InspectNetworkAsync("default", ct);
        Assert.NotNull(inspected);
        Assert.Equal(xpcDefault.Subnet, inspected!.Subnet);
        Assert.Null(await xpc.InspectNetworkAsync($"cider-e2e-missing-{Guid.NewGuid():N}", ct));
    }

    [E2EFact]
    public async Task ListVolumes_agrees_with_the_CLI_transport_for_every_volume_seen_by_both()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, cli) = NewRuntimes();
        using var _ = xpc;

        var cliVolumes = await cli.ListVolumesAsync(ct);
        var xpcVolumes = await xpc.ListVolumesAsync(ct);
        var xpcByName = xpcVolumes.ToDictionary(v => v.Name, StringComparer.Ordinal);

        foreach (var cliVolume in cliVolumes)
        {
            if (!xpcByName.TryGetValue(cliVolume.Name, out var xpcVolume))
            {
                continue;
            }

            Assert.Equal(cliVolume.Driver, xpcVolume.Driver);
            Assert.Equal(cliVolume.Mountpoint, xpcVolume.Mountpoint);
            Assert.Equal(cliVolume.SizeBytes, xpcVolume.SizeBytes);

            var inspected = await xpc.InspectVolumeAsync(cliVolume.Name, ct);
            Assert.NotNull(inspected);
            Assert.Equal(cliVolume.Driver, inspected!.Driver);
        }

        Assert.Null(await xpc.InspectVolumeAsync($"cider-e2e-missing-{Guid.NewGuid():N}", ct));
    }

    /// <summary>Task's verification section: "20× <c>docker ps -a</c> median ≤ 5 ms" — measured here at
    /// the runtime layer as 20× <see cref="IContainerRuntime.ListContainersAsync"/>, the call
    /// <c>docker ps -a</c> ultimately makes. One untimed warm-up call first, same as
    /// <c>XpcClientE2ETests.Hundred_pings_have_a_median_round_trip_under_0_2ms</c>, so JIT/connection
    /// setup is not counted against the budget.</summary>
    [E2EFact]
    public async Task Twenty_ListContainers_calls_have_a_median_latency_at_or_under_5ms()
    {
        using var cts = new CancellationTokenSource(Budget);
        var ct = cts.Token;
        var (xpc, _) = NewRuntimes();
        using var _ = xpc;

        await xpc.ListContainersAsync(ct); // warm-up

        var samples = new List<double>(20);
        for (var i = 0; i < 20; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await xpc.ListContainersAsync(ct);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        Assert.True(median <= 5.0, $"median ListContainersAsync latency was {median:F3} ms, expected <= 5 ms");
    }

    /// <summary>Compares the fields <see cref="XpcContainerRuntime.ToContainer"/> derives from the
    /// wire the same way <c>Cli.RuntimeMapper.ToContainer</c> derives them from the CLI's JSON —
    /// tolerant of the two transports' different date precisions (Apple reference date carries
    /// fractional seconds; the CLI's ISO-8601 display JSON does not).</summary>
    private static void AssertContainersEquivalent(RuntimeContainer cli, RuntimeContainer xpc)
    {
        Assert.Equal(cli.RuntimeId, xpc.RuntimeId);
        Assert.Equal(cli.State, xpc.State);
        Assert.Equal(cli.ImageReference, xpc.ImageReference);
        Assert.Equal(cli.Argv, xpc.Argv);
        Assert.Equal(cli.Env, xpc.Env);
        Assert.Equal(cli.WorkingDir, xpc.WorkingDir);
        Assert.Equal(cli.Tty, xpc.Tty);
        Assert.Equal(cli.Cpus, xpc.Cpus);
        Assert.Equal(cli.MemoryBytes, xpc.MemoryBytes);

        Assert.Equal(cli.Labels.Count, xpc.Labels.Count);
        foreach (var (key, value) in cli.Labels)
        {
            Assert.True(xpc.Labels.TryGetValue(key, out var xpcValue), $"xpc labels missing '{key}'");
            Assert.Equal(value, xpcValue);
        }

        Assert.Equal(cli.Networks.Count, xpc.Networks.Count);
        foreach (var cliNet in cli.Networks)
        {
            var xpcNet = xpc.Networks.FirstOrDefault(n => n.Network == cliNet.Network);
            Assert.NotNull(xpcNet);
            Assert.Equal(cliNet.Hostname, xpcNet!.Hostname);
            Assert.Equal(cliNet.IPv4Address, xpcNet.IPv4Address);
            Assert.Equal(cliNet.IPv4Gateway, xpcNet.IPv4Gateway);
        }

        Assert.Equal(cli.Mounts.Count, xpc.Mounts.Count);
        Assert.Equal(cli.PublishedPorts.Count, xpc.PublishedPorts.Count);

        if (cli.CreatedAt is { } cliCreated && xpc.CreatedAt is { } xpcCreated)
        {
            Assert.True(
                Math.Abs((cliCreated - xpcCreated).TotalSeconds) < 2,
                $"CreatedAt mismatch: cli={cliCreated:o} xpc={xpcCreated:o}");
        }
    }
}
