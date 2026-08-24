using Cider.Core.Time;
using Xunit;

namespace Cider.Tests.Time;

public class DockerTimeTests
{
    private static readonly DateTimeOffset Sample =
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

    [Fact]
    public void Format_uses_rfc3339_with_nine_fractional_digits_and_Z()
    {
        Assert.Equal("2026-08-21T10:00:00.123456700Z", DockerTime.Format(Sample));
        Assert.Equal("2026-08-21T10:00:00.000000000Z", DockerTime.Format(new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Format_converts_to_utc_first()
    {
        var local = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-08-21T10:00:00.000000000Z", DockerTime.Format(local));
    }

    [Fact]
    public void ZeroTime_matches_gos_zero_time()
    {
        Assert.Equal("0001-01-01T00:00:00Z", DockerTime.ZeroTime);
        Assert.Equal(DockerTime.ZeroTime, DockerTime.FormatOrZero(null));
        Assert.Equal("0001-01-01T00:00:00.000000000Z", DockerTime.Format(DockerTime.ZeroTimeValue));
    }

    [Fact]
    public void UnixSeconds_and_UnixNanos()
    {
        var epoch = DateTimeOffset.UnixEpoch;

        Assert.Equal(0, DockerTime.UnixSeconds(epoch));
        Assert.Equal(0, DockerTime.UnixNanos(epoch));
        Assert.Equal(1, DockerTime.UnixSeconds(epoch.AddSeconds(1)));
        Assert.Equal(1_000_000_000, DockerTime.UnixNanos(epoch.AddSeconds(1)));
        Assert.Equal(100, DockerTime.UnixNanos(epoch.AddTicks(1)));

        Assert.Equal(DockerTime.UnixSeconds(Sample) * 1_000_000_000L + 123_456_700L, DockerTime.UnixNanos(Sample));
    }

    [Fact]
    public void FromUnixNanos_round_trips()
    {
        var nanos = DockerTime.UnixNanos(Sample);

        Assert.Equal(Sample.UtcDateTime, DockerTime.FromUnixNanos(nanos).UtcDateTime);
    }

    [Fact]
    public void Parse_reads_rfc3339_with_nanoseconds()
    {
        var parsed = DockerTime.Parse("2026-08-21T10:00:00.123456789Z");

        Assert.Equal(2026, parsed.Year);
        Assert.Equal(8, parsed.Month);
        Assert.Equal(21, parsed.Day);
        Assert.Equal(10, parsed.Hour);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);

        // DateTimeOffset resolves to 100 ns, so the last two nanosecond digits are lost.
        Assert.StartsWith("2026-08-21T10:00:00.12345", DockerTime.Format(parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_round_trips_our_own_format()
    {
        Assert.Equal(Sample.UtcDateTime, DockerTime.Parse(DockerTime.Format(Sample)).UtcDateTime);
    }

    [Fact]
    public void Parse_reads_offsets_and_the_zero_time()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero),
            DockerTime.Parse("2026-08-21T12:00:00+02:00").ToUniversalTime());

        Assert.Equal(DockerTime.ZeroTimeValue, DockerTime.Parse(DockerTime.ZeroTime).ToUniversalTime());
    }

    [Fact]
    public void Parse_reads_unix_seconds_used_by_events_since_and_until()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_755_770_400), DockerTime.Parse("1755770400"));

        var fractional = DockerTime.Parse("1755770400.123456789");
        Assert.Equal(1_755_770_400, fractional.ToUnixTimeSeconds());
        Assert.StartsWith("2025-08-21T", DockerTime.Format(fractional), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_rejects_garbage()
    {
        Assert.False(DockerTime.TryParse(null, out _));
        Assert.False(DockerTime.TryParse("", out _));
        Assert.False(DockerTime.TryParse("not-a-time", out _));
        Assert.Throws<FormatException>(() => DockerTime.Parse("nope"));
    }
}
