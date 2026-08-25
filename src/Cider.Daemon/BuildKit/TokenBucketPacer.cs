using System.Diagnostics;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// The <see cref="IUpstreamPacer"/> attached to a <see cref="BuilderLink"/>'s <see cref="ForwardTarget"/>:
/// a classic token bucket bounding how many bytes may move onto the builder link per second, with a
/// small burst allowance so one gRPC frame slightly over the steady rate does not stall. Explicit
/// pacing is needed here — see <see cref="IUpstreamPacer"/> — because buildkitd's own HTTP/2 receive
/// window is BDP-driven up to 16 MiB and does not bound it on its own.
/// <para>
/// Planner ruling (cider-ger.8, comment #10): ship the conservative defaults below and tune them from
/// later large-context measurements rather than making them configurable now.
/// </para>
/// </summary>
internal sealed class TokenBucketPacer : IUpstreamPacer
{
    /// <summary>Steady-state upstream rate: 8 MiB/s.</summary>
    internal const double DefaultBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>Burst allowance on top of the steady rate: 1 MiB.</summary>
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
