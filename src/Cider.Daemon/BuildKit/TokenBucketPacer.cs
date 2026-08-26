using System.Diagnostics;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// The <see cref="IUpstreamPacer"/> attached to a <see cref="BuilderLink"/>'s <see cref="ForwardTarget"/>:
/// a classic token bucket bounding how many bytes may move onto the builder link per second, with a
/// small burst allowance so one gRPC frame slightly over the steady rate does not stall. Explicit
/// pacing is needed here — see <see cref="IUpstreamPacer"/> — because buildkitd's own HTTP/2 receive
/// window is BDP-driven up to 16 MiB and does not bound it on its own.
/// <para>
/// <see cref="BuilderConnection"/> constructs exactly one of these per <see cref="BuilderLink"/>, and
/// that link is the daemon's single long-lived exec dial into the builder VM (see its own summary) --
/// every concurrent build shares the same <see cref="ForwardTarget.Pacer"/> instance. So
/// <see cref="DefaultBytesPerSecond"/> is an aggregate ceiling on daemon-&gt;buildkitd bytes across
/// however many builds are running at once, not a per-build allowance that multiplies with
/// concurrency: two builds sharing the link split one <see cref="DefaultBytesPerSecond"/>-sized
/// budget (via <see cref="Reserve"/>'s shared debt) rather than each getting their own.
/// </para>
/// </summary>
internal sealed class TokenBucketPacer : IUpstreamPacer
{
    /// <summary>
    /// Steady-state upstream rate: 32 MiB/s (cider-ger.21, retuning cider-ger.8 comment #10's
    /// placeholder default from cider-ger.15's large-context measurements, per planner ruling
    /// cider-ger.21 comment #102).
    /// <para>
    /// cider-ger.15 measured the exec pipe (<c>container exec -i buildkit buildctl dial-stdio</c>)
    /// sustaining well over 100 MiB/s with zero stalls or link-recovery events: a 200 MiB context
    /// upload at 8x this rate (a temporary, uncommitted 512 MiB/s diagnostic) took ~1s
    /// (175-233 MiB/s achieved, i.e. something past the pacer -- FileSync chunking/hashing overhead,
    /// not this constant -- is the next limiting factor), and a 585 MB unpaced image export (the
    /// pacer only ever applies upstream, see the class summary) took ~2.1s (~266 MiB/s). Both
    /// corroborate a real single-stream ceiling in the ~120-260 MiB/s range with this VM/CLI/OS combo.
    /// </para>
    /// <para>
    /// 32 MiB/s was chosen, not a value closer to that ceiling, because it has to hold under both
    /// risk cases the diagnostic run didn't cover (cider-ger.21 comment #102), and this constant is
    /// the only place that reasoning is recorded for the next person to avoid re-deriving it:
    /// </para>
    /// <para>
    /// <b>Concurrent builds sharing the link:</b> covered by construction, not by margin -- per this
    /// class's summary, every concurrent build draws on the same bucket, so the aggregate
    /// daemon-&gt;buildkitd rate never exceeds 32 MiB/s regardless of how many builds are in flight.
    /// N builds sharing the link is the *same* aggregate load the single-build measurements above
    /// already exercised at rates up to 512 MiB/s clean, not an N-times multiple of it.
    /// </para>
    /// <para>
    /// <b>A slow or stalled consumer:</b> the risk a pacer default actually controls is how much
    /// unacknowledged data can pile up in the exec pipe before the stall detector (untouched by this
    /// change; see <see cref="BuilderLinkTracker"/>) notices buildkitd has stopped draining it. That
    /// ceiling is <see cref="DefaultBurstBytes"/> (unchanged at 1 MiB) plus whatever the OS pipe
    /// buffer itself holds -- both independent of the steady rate, since <see cref="Reserve"/> caps
    /// the token balance at <see cref="DefaultBurstBytes"/> no matter how fast it refills. Raising
    /// the steady rate only changes how quickly a *keeping-up* buildkitd receives bytes, not how much
    /// can queue up against a stuck one. What the steady rate does still bound is how much of that
    /// pile-up risk is created *while buildkitd is falling behind but not yet stalled* -- staying at
    /// 32 MiB/s rather than nearer the ~120-260 MiB/s single-stream ceiling leaves a &gt;=4x margin for
    /// buildkitd to be genuinely busy (e.g. mid-solve on another concurrent build) and still drain
    /// this link faster than bytes arrive, without the two ever fully deciding the point through a
    /// destructive test (an intentionally throttled or paused buildkitd) that would risk wedging the
    /// one throwaway link this evidence pass had to measure against.
    /// </para>
    /// <para>
    /// Net: a single 200 MiB context upload measures ~6.2s at 32 MiB/s -- re-measured directly
    /// (cider-ger.21, throwaway daemon, `docker build` via the real buildx/session path, three clean
    /// runs, zero stall/link-recovery lines) rather than assumed from the arithmetic -- against 25s
    /// at the old 8 MiB/s default and ~1s uncommitted-diagnostic-only at 512 MiB/s: most of the
    /// achievable win, at a rate with margin for both risk cases above. If either case ever needs
    /// re-litigating with real concurrent-build or slow-consumer measurements, prefer lowering this
    /// constant over raising it again from a single clean run, per the same evidence bar this value
    /// was held to.
    /// </para>
    /// </summary>
    internal const double DefaultBytesPerSecond = 32 * 1024 * 1024;

    /// <summary>
    /// Burst allowance on top of the steady rate: 1 MiB, unchanged by cider-ger.21's rate increase.
    /// <see cref="ForwardTarget.MaxUpstreamChunk"/> defaults to 32 KiB and does not scale with
    /// <see cref="DefaultBytesPerSecond"/>, so 1 MiB (32x that chunk size) stays an ample burst for
    /// the first several chunks of any call regardless of steady rate -- nothing about raising the
    /// rate makes the first gRPC frame more likely to stall.
    /// </summary>
    internal const double DefaultBurstBytes = 1 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly double _bytesPerSecond;
    private readonly double _burstBytes;
    private readonly BuilderLinkTracker? _tracker;
    private double _tokens;
    private long _lastRefillTicks;

    public TokenBucketPacer(
        double bytesPerSecond = DefaultBytesPerSecond,
        double burstBytes = DefaultBurstBytes,
        BuilderLinkTracker? tracker = null)
    {
        if (bytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, "cider: bytesPerSecond must be positive");
        }

        if (burstBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burstBytes), burstBytes, "cider: burstBytes must be positive");
        }

        _bytesPerSecond = bytesPerSecond;
        _burstBytes = burstBytes;
        _tracker = tracker;
        _tokens = burstBytes;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public async ValueTask AcquireAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount > 0)
        {
            var wait = Reserve(byteCount);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        // A chunk that cleared the bucket (or an empty one) counts as progress either way: an
        // upstream write that is merely paced, not stuck, must not look like a stall to the watchdog.
        _tracker?.RecordProgress();
    }

    /// <summary>
    /// Refills the bucket for elapsed time, then reserves <paramref name="byteCount"/> tokens
    /// immediately -- letting the balance go negative ("debt") rather than capping the request at
    /// whatever is on hand -- and reports how long the caller must sleep before that debt is repaid.
    /// A hard "wait until <c>_tokens &gt;= byteCount</c>" version would never admit a chunk bigger
    /// than the burst at all: refilling is capped at <see cref="_burstBytes"/>, so the balance could
    /// never climb past it to satisfy a larger request, and the caller would spin forever. Reserving
    /// eagerly (and letting concurrent reservations queue up as more debt on the same balance) is the
    /// standard fix and keeps the aggregate rate correct under concurrent writers too.
    /// </summary>
    private TimeSpan Reserve(int byteCount)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastRefillTicks, now);
            _lastRefillTicks = now;
            _tokens = Math.Min(_burstBytes, _tokens + elapsed.TotalSeconds * _bytesPerSecond) - byteCount;

            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
        }
    }
}
