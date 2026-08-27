using System.Diagnostics;
using System.Text;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Daemon.Hosting;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// cider-ede.15's create-latency smoke assertion: a guard that the XPC fast path (containerCreate
/// over the apiserver, replacing a held <c>container create</c> child process) stays fast, not a
/// precise benchmark. The two create-timing tests are XPC-only (<see cref="XpcOnlyFactAttribute"/>)
/// — the CLI transport has none of the latency properties those thresholds characterize, and running
/// them there would just assert something else's numbers under the wrong name.
/// <see cref="Docker_ps_a_is_fast"/> is the exception: <c>GET /containers/json?all=1</c> is served
/// entirely from in-memory state regardless of transport, so it runs under both (<see
/// cref="E2EFactAttribute"/>) and is not really a runtime measurement at all — see its own doc.
///
/// Every timed call goes straight over the daemon's own unix socket (<see cref="DaemonClient"/>),
/// never through a spawned <c>docker</c> CLI process: a cider-ede.15 fixer re-verification
/// (2026-08-26) measured the CLI's own process-spawn-plus-client-startup cost in isolation
/// (<c>docker --version</c> median 11.4 ms, <c>docker context ls</c> median 20.1 ms over 10 runs,
/// idle machine) — a fixed floor that alone exceeded this suite's original 10 ms <c>ps -a</c>
/// budget before a single byte reached cider, and that made all three of the original
/// CLI-driven assertions fail against the real Apple runtime at load average 4.4. Measuring
/// through the socket instead removes that floor and leaves only what these tests are meant to
/// characterize: cider's own containerCreate-over-XPC latency.
///
/// Thresholds below were re-derived from repeated local runs against the real Apple runtime
/// (2026-08-26) after switching to the socket, set with roughly 2.5-3x headroom over the observed
/// medians/typical values. Those local runs were NOT on an idle machine — this box carried several
/// other concurrently-running agent processes at the time (load average ranging ~35-70 on 16
/// cores), so the numbers below already bake in a fair amount of real contention rather than
/// characterizing a best case; the task's original literal 30/80/300/10 ms figures were never
/// reachable through any path that includes a spawned <c>docker</c> client and are superseded here
/// regardless. As of aab77a9, .github/workflows/e2e.yml DOES gate the build on this class (its perf
/// step no longer sets <c>continue-on-error</c>) — an earlier version of this comment claimed
/// otherwise; that was stale even before this revision.
///
/// cider-ede.36 (2026-08-27) recorded real medians against this box (still under concurrent-agent
/// contention, not idle) to check the two create-timing budgets against the epic's promised
/// targets (<c>docker create</c> &lt;= 25 ms median, 8-parallel &lt;= 0.2 s wall) rather than leaving
/// them unrecorded: <see cref="Sequential_create_of_a_cached_image_is_fast"/>'s median measured
/// 13.0-20.2 ms over 10 independent 100-sample runs split across net10.0/net11.0, comfortably and
/// consistently under the promised 25 ms with no outliers; <see
/// cref="Eight_parallel_creates_of_a_cached_image_finish_within_budget"/>'s wall time measured
/// 71.4-93.5 ms over 9 runs split the same way (well below the 700-1200 ms this same test saw before
/// cider-ede.30's eager DNS-forwarder bootstrap fix), also with no outliers. Both support tightening
/// toward the epic's literal promise rather than restating it: the median budget below moved
/// 40 ms -> 25 ms (matching the promise directly) and the 8-parallel budget moved 1200 ms -> 200 ms
/// (matching the promise directly, ~2.1x headroom over the worst observed 93.5 ms). p99 (no epic
/// target) is a different story: 9 of 10 sampled runs put it at 23.6-29.8 ms, but one run spiked to
/// 82.0 ms with no corresponding median spike in that same run — a real, occasional tail-latency
/// event under this box's contention rather than measurement noise to be averaged away, so p99's
/// budget moved 300 ms -> 150 ms (not all the way to the ~2.5x-over-typical convention's ~75 ms,
/// which the observed spike alone would already have exceeded) rather than to something tighter that
/// this one data point shows would be flaky.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class PerfSmokeTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    /// <summary>
    /// Sample count for the sequential create test. High enough that p99 (<c>ceil(0.99*100)-1 =
    /// 98</c>) is a real percentile over the tail rather than, as at 20 samples, arithmetically
    /// identical to the maximum.
    /// </summary>
    private const int SequentialIterations = 100;

    /// <summary>Sample count for the <c>ps -a</c>-equivalent test, which only asserts a median.</summary>
    private const int PsIterations = 20;

    [XpcOnlyFact]
    public async Task Sequential_create_of_a_cached_image_is_fast()
    {
        await EnsureImageCachedAsync();

        using var client = await CreateWarmClientAsync();

        var samples = new List<double>(SequentialIterations);
        for (var i = 0; i < SequentialIterations; i++)
        {
            var name = DaemonFixture.NewName("perf-seq");
            var stopwatch = Stopwatch.StartNew();
            var id = await CreateContainerAsync(client, name);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);

            using var delete = await client.DeleteAsync(new Uri($"/containers/{id}?force=true", UriKind.Relative));
            Assert.True(delete.IsSuccessStatusCode, await DescribeAsync(delete));
        }

        samples.Sort();
        var median = Percentile(samples, 0.50);
        var p99 = Percentile(samples, 0.99);

        Assert.True(
            median <= 25,
            $"median containerCreate-over-XPC latency was {median:F1} ms (budget 25 ms) over " +
            $"{SequentialIterations} runs: " + string.Join(", ", samples.Select(s => s.ToString("F1"))));
        Assert.True(
            p99 <= 150,
            $"p99 containerCreate-over-XPC latency was {p99:F1} ms (budget 150 ms) over " +
            $"{SequentialIterations} runs: " + string.Join(", ", samples.Select(s => s.ToString("F1"))));
    }

    /// <summary>
    /// 8 concurrent <c>containerCreate</c> calls consistently took 700-1200 ms wall on this box across
    /// several runs (2026-08-26) before cider-ede.30 traced this: the cost was never the 8 creates
    /// failing to parallelize — it was <c>DnsForwarderService.EnsureAsync</c>'s per-network gate
    /// (<c>src/Cider.Daemon/Dns/DnsForwarderService.cs</c>), which every create routes through
    /// (<c>ContainerManager.CreateAsync</c> → <c>ResolveDnsServersAsync</c>) to get a
    /// <c>--dns</c> address for the container's network. On a freshly started daemon nothing has
    /// bootstrapped the default "bridge" network's DNS forwarder container yet, so whichever of the 8
    /// concurrent creates reaches the gate first pays a real Apple container create+start for that
    /// forwarder (~550-650 ms measured, <c>containerBootstrap</c> alone ~520-560 ms) while the other 7
    /// queue behind the same <c>SemaphoreSlim(1,1)</c> gate — a one-time, whole-batch tax that a naive
    /// "wall / 8" reading misreads as "~125 ms/container that does not parallelize". Confirmed by
    /// instrumenting a diagnostic copy of this test: an untimed warm-up create before the timed batch
    /// (which pays the bootstrap itself, off the clock) dropped 8-way wall time from 700-1200 ms to
    /// 100-250 ms — within reach of the ~100 ms Apple's own CLI does 8 concurrent creates in with no
    /// cider at all (planner-1, cider-ede.30 comment). <c>DaemonLifecycle.StartAsync</c> now pays this
    /// bootstrap once, eagerly, right after the DNS server starts and before Kestrel takes real client
    /// traffic, so the create path never queues behind it. Re-measured post-fix (2026-08-26, same box,
    /// load average ~30-36, i.e. not idle): 19 runs of this test against a fresh daemon ranged
    /// 101.5-561.6 ms wall (median ~180 ms, two outliers over 450 ms under momentary extra load from
    /// other concurrently-running processes on this box) — the budget below keeps ~2x headroom over
    /// the worst of those.
    /// </summary>
    [XpcOnlyFact]
    public async Task Eight_parallel_creates_of_a_cached_image_finish_within_budget()
    {
        await EnsureImageCachedAsync();

        using var client = await CreateWarmClientAsync();

        const int parallelism = 8;
        var names = Enumerable.Range(0, parallelism).Select(_ => DaemonFixture.NewName("perf-par")).ToArray();
        var creates = names.Select(name => CreateContainerAsync(client, name)).ToArray();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var ids = await Task.WhenAll(creates);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromMilliseconds(200),
                $"{parallelism} parallel containerCreate-over-XPC calls took " +
                $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms wall (budget 200 ms)");
        }
        finally
        {
            // Delete every container that actually got created, even if one of the parallel
            // creates above threw (leaving `creates` partially completed) — otherwise up to 7
            // perf-par-* containers leak into DaemonCollection's shared daemon. Swallow delete
            // failures here so a cleanup error can never mask the original create exception.
            foreach (var task in creates)
            {
                if (!task.IsCompletedSuccessfully)
                {
                    continue;
                }

                try
                {
                    using var delete = await client.DeleteAsync(
                        new Uri($"/containers/{task.Result}?force=true", UriKind.Relative));
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    /// <summary>
    /// Not a runtime measurement, despite the <c>docker ps -a</c> naming: <c>GET
    /// /containers/json?all=1</c> is served entirely from in-memory state
    /// (<c>ContainerManager.ListAsync</c> iterates <c>_store.GetAll()</c>; <c>ContainerManager.Query.cs</c>
    /// makes no <c>_runtime.</c> call at all), so it never reaches the XPC or CLI runtime and runs
    /// identically under both transports (<see cref="E2EFactAttribute"/>). What this guards is the
    /// Docker-API list path over the daemon socket staying cheap. The 2 ms budget is tight against the
    /// observed ~0.1 ms median deliberately — an 8 ms budget on a ~0.1 ms operation is 80x headroom,
    /// wide enough that this test could never actually fail a regression.
    /// </summary>
    [E2EFact]
    public async Task Docker_ps_a_is_fast()
    {
        using var client = await CreateWarmClientAsync();

        var samples = new List<double>(PsIterations);
        for (var i = 0; i < PsIterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(new Uri("/containers/json?all=1", UriKind.Relative));
            stopwatch.Stop();
            Assert.True(response.IsSuccessStatusCode, await DescribeAsync(response));
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = Percentile(samples, 0.50);

        Assert.True(
            median <= 2,
            $"median GET /containers/json?all=1 latency was {median:F1} ms (budget 2 ms) over " +
            $"{PsIterations} runs: " + string.Join(", ", samples.Select(s => s.ToString("F1"))));
    }

    /// <summary>Pulls <see cref="Image"/> once, untimed, so the timed loops above only ever measure a
    /// cached-image create — a cold pull is a completely different (network-bound) latency budget.</summary>
    private async Task EnsureImageCachedAsync()
    {
        var pull = await daemon.DockerAsync(["pull", Image], timeout: TimeSpan.FromMinutes(6));
        Assert.True(pull.Ok, pull.ToString());
    }

    /// <summary>
    /// Opens an <see cref="HttpClient"/> bound to this fixture's daemon socket and issues one
    /// untimed request first, so socket connect and JIT warmup never land inside a timed sample.
    /// </summary>
    private async Task<HttpClient> CreateWarmClientAsync()
    {
        var client = DaemonClient.Create(daemon.Options.SocketPath, TimeSpan.FromSeconds(30));
        using var warmup = await client.GetAsync(new Uri("/_ping", UriKind.Relative));
        Assert.True(warmup.IsSuccessStatusCode, await DescribeAsync(warmup));
        return client;
    }

    /// <summary><c>POST /containers/create</c> for a cached <see cref="Image"/>, returning the new id.</summary>
    private static async Task<string> CreateContainerAsync(HttpClient client, string name)
    {
        var request = new ContainerCreateRequest { Image = Image, Cmd = ["true"] };
        using var content = new StringContent(DockerJson.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            new Uri($"/containers/create?name={Uri.EscapeDataString(name)}", UriKind.Relative),
            content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST /containers/create -> {(int)response.StatusCode}: {body}");
        var created = DockerJson.Deserialize<ContainerCreateResponse>(body);
        Assert.False(string.IsNullOrEmpty(created?.Id), "containerCreate response had no Id: " + body);
        return created!.Id;
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";

    /// <summary>Nearest-rank percentile over an already-sorted sample set.</summary>
    private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedSamples.Count) - 1;
        return sortedSamples[Math.Clamp(rank, 0, sortedSamples.Count - 1)];
    }
}
