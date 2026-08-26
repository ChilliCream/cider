using System.Diagnostics;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// cider-ede.15's create-latency smoke assertion: a guard that the XPC fast path (containerCreate
/// over the apiserver, replacing a held <c>container create</c> child process) stays fast, not a
/// precise benchmark. XPC-only (<see cref="XpcOnlyFactAttribute"/>) — the CLI transport has none of
/// the latency properties these thresholds characterize, and running them there would just assert
/// something else's numbers under the wrong name. Thresholds are deliberately generous for CI
/// runners per the task's fix direction and are expected to tighten later.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class PerfSmokeTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";
    private const int Iterations = 20;

    [XpcOnlyFact]
    public async Task Sequential_create_of_a_cached_image_is_fast()
    {
        await EnsureImageCachedAsync();

        var samples = new List<double>(Iterations);
        for (var i = 0; i < Iterations; i++)
        {
            var name = DaemonFixture.NewName("perf-seq");
            var stopwatch = Stopwatch.StartNew();
            var create = await daemon.DockerAsync(["create", "--name", name, Image, "true"], timeout: TimeSpan.FromSeconds(30));
            stopwatch.Stop();
            Assert.True(create.Ok, create.ToString());
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);

            var rm = await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromSeconds(30));
            Assert.True(rm.Ok, rm.ToString());
        }

        samples.Sort();
        var median = Percentile(samples, 0.50);
        var p99 = Percentile(samples, 0.99);

        Assert.True(
            median <= 30,
            $"median docker create latency was {median:F1} ms (budget 30 ms) over {Iterations} runs: " +
            string.Join(", ", samples.Select(s => s.ToString("F1"))));
        Assert.True(
            p99 <= 80,
            $"p99 docker create latency was {p99:F1} ms (budget 80 ms) over {Iterations} runs: " +
            string.Join(", ", samples.Select(s => s.ToString("F1"))));
    }

    [XpcOnlyFact]
    public async Task Eight_parallel_creates_of_a_cached_image_finish_within_budget()
    {
        await EnsureImageCachedAsync();

        const int parallelism = 8;
        var names = Enumerable.Range(0, parallelism).Select(_ => DaemonFixture.NewName("perf-par")).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(names.Select(name =>
            daemon.DockerAsync(["create", "--name", name, Image, "true"], timeout: TimeSpan.FromSeconds(30))));
        stopwatch.Stop();

        try
        {
            foreach (var result in results)
            {
                Assert.True(result.Ok, result.ToString());
            }

            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromSeconds(0.3),
                $"{parallelism} parallel docker creates took {stopwatch.Elapsed.TotalMilliseconds:F1} ms wall " +
                "(budget 300 ms)");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", .. names], timeout: TimeSpan.FromSeconds(30));
        }
    }

    [XpcOnlyFact]
    public async Task Docker_ps_a_is_fast()
    {
        await EnsureImageCachedAsync();

        var samples = new List<double>(Iterations);
        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var ps = await daemon.DockerAsync(["ps", "-a"], timeout: TimeSpan.FromSeconds(30));
            stopwatch.Stop();
            Assert.True(ps.Ok, ps.ToString());
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = Percentile(samples, 0.50);

        Assert.True(
            median <= 10,
            $"median `docker ps -a` latency was {median:F1} ms (budget 10 ms) over {Iterations} runs: " +
            string.Join(", ", samples.Select(s => s.ToString("F1"))));
    }

    /// <summary>Pulls <see cref="Image"/> once, untimed, so the timed loops below only ever measure a
    /// cached-image create — a cold pull is a completely different (network-bound) latency budget.</summary>
    private async Task EnsureImageCachedAsync()
    {
        var pull = await daemon.DockerAsync(["pull", Image], timeout: TimeSpan.FromMinutes(6));
        Assert.True(pull.Ok, pull.ToString());
    }

    /// <summary>Nearest-rank percentile over an already-sorted sample set.</summary>
    private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedSamples.Count) - 1;
        return sortedSamples[Math.Clamp(rank, 0, sortedSamples.Count - 1)];
    }
}
