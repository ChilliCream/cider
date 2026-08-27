using System.Diagnostics;
using Cider.Daemon.BuildKit;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="TokenBucketPacer"/> in isolation: the planner-ruled defaults (32 MiB/s, 1 MiB burst;
/// cider-ger.21, retuning cider-ger.8 comment #10's original 8 MiB/s placeholder) actually throttle a
/// write that exceeds the burst, a small write within the burst is not delayed at all, and an
/// <c>AcquireAsync</c> call always reports progress to an attached tracker regardless of whether it
/// had to wait.
/// </summary>
public sealed class TokenBucketPacerTests
{
    [Fact]
    public async Task A_33_MiB_write_from_a_cold_bucket_is_throttled_to_roughly_the_configured_rate()
    {
        // Burst 1 MiB is available immediately; the remaining 32 MiB must wait at 32 MiB/s, i.e. at
        // least ~1.0 s. A generous floor (850 ms) keeps this from flaking on a loaded CI box while
        // still failing hard for an unthrottled implementation (which would return in single-digit ms).
        var pacer = new TokenBucketPacer(TokenBucketPacer.DefaultBytesPerSecond, TokenBucketPacer.DefaultBurstBytes);
        const int thirtyThreeMiB = 33 * 1024 * 1024;

        var stopwatch = Stopwatch.StartNew();
        await pacer.AcquireAsync(thirtyThreeMiB, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(850),
            $"expected the pacer to hold a 33 MiB write back for at least 850ms, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_write_within_the_burst_is_not_delayed()
    {
        var pacer = new TokenBucketPacer(TokenBucketPacer.DefaultBytesPerSecond, TokenBucketPacer.DefaultBurstBytes);
        const int halfBurst = 512 * 1024;

        var stopwatch = Stopwatch.StartNew();
        await pacer.AcquireAsync(halfBurst, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"expected a burst-sized write to pass through immediately, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task AcquireAsync_bumps_the_attached_trackers_progress_even_when_it_has_to_wait()
    {
        var tracker = new BuilderLinkTracker();
        // Fast rate so the wait itself stays short; the point is only that RecordProgress runs after it.
        var pacer = new TokenBucketPacer(bytesPerSecond: 1024 * 1024, burstBytes: 1024, tracker: tracker);
        using var scope = tracker.BeginCall();

        var before = tracker.LastProgress;
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await pacer.AcquireAsync(4096, CancellationToken.None);

        // Both sides of this assertion are timestamps captured in this test (before/after), never a
        // fresh 'now' read -- so there is no wall-clock race with dispatch or scheduler latency to
        // widen a margin against. A pacer that never calls RecordProgress leaves after == before and
        // fails the first assertion outright.
        var after = tracker.LastProgress;
        Assert.True(after > before, $"expected AcquireAsync to bump LastProgress; before={before}, after={after}");
        var recordedAfterTheWait = Stopwatch.GetElapsedTime(before, after);
        Assert.True(
            recordedAfterTheWait >= TimeSpan.FromMilliseconds(40),
            $"expected RecordProgress to run after the ~50ms wait, measured {recordedAfterTheWait}");
    }

    [Fact]
    public void IsStalled_reflects_only_open_calls_and_elapsed_time_against_fixed_bounds()
    {
        var withOpenCall = new BuilderLinkTracker();
        using (withOpenCall.BeginCall())
        {
            // A huge threshold can never have elapsed yet -- this cannot flake.
            Assert.False(withOpenCall.IsStalled(TimeSpan.FromHours(1)));
            // Any measurable elapsed time exceeds a zero threshold -- this cannot flake either.
            Assert.True(withOpenCall.IsStalled(TimeSpan.Zero));
        }

        // A link nothing is using (no open call) is never stalled, regardless of threshold.
        var withNoOpenCall = new BuilderLinkTracker();
        Assert.False(withNoOpenCall.IsStalled(TimeSpan.Zero));
    }

    [Fact]
    public void Non_positive_rate_or_burst_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketPacer(bytesPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketPacer(burstBytes: -1));
    }
}
