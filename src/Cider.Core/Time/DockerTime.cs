using System.Globalization;

namespace Cider.Core.Time;

/// <summary>
/// Docker's timestamp formats: RFC3339 with nanosecond precision (Go's <c>time.RFC3339Nano</c> as
/// emitted by the daemon, always 9 fractional digits and a <c>Z</c> zone) plus unix seconds/nanos.
/// </summary>
public static class DockerTime
{
    /// <summary>Go's zero <c>time.Time</c>, which Docker prints for "never".</summary>
    public const string ZeroTime = "0001-01-01T00:00:00Z";

    /// <summary><see cref="ZeroTime"/> as a value.</summary>
    public static readonly DateTimeOffset ZeroTimeValue = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string BaseFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    /// <summary>Formats as RFC3339 with 9 fractional digits and a <c>Z</c> zone (<c>2026-08-21T10:00:00.123456700Z</c>).</summary>
    public static string Format(DateTimeOffset value)
    {
        // DateTimeOffset resolves to 100 ns ticks -> 7 digits; Docker always prints 9.
        var utc = value.ToUniversalTime();
        return string.Concat(utc.ToString(BaseFormat, CultureInfo.InvariantCulture), "00Z");
    }

    /// <summary>Formats a nullable timestamp, falling back to <see cref="ZeroTime"/>.</summary>
    public static string FormatOrZero(DateTimeOffset? value) =>
        value is null ? ZeroTime : Format(value.Value);

    /// <summary>Seconds since the unix epoch.</summary>
    public static long UnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    /// <summary>Nanoseconds since the unix epoch (100 ns resolution, so the last two digits are always 0).</summary>
    public static long UnixNanos(DateTimeOffset value) =>
        (value.UtcTicks - DateTime.UnixEpoch.Ticks) * 100L;

    /// <summary>Builds a <see cref="DateTimeOffset"/> from unix nanoseconds.</summary>
    public static DateTimeOffset FromUnixNanos(long nanos) =>
        new(DateTime.UnixEpoch.Ticks + (nanos / 100L), TimeSpan.Zero);

    /// <summary>
    /// Parses an RFC3339 timestamp (any number of fractional digits), a unix seconds value
    /// (<c>1755770400</c>) or unix seconds with a fraction (<c>1755770400.123456789</c>) — the forms
    /// Docker accepts in <c>since</c>/<c>until</c> query parameters.
    /// </summary>
    public static DateTimeOffset Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new FormatException($"invalid timestamp: {value}");
        }

        return result;
    }

    /// <summary>Non-throwing <see cref="Parse"/>.</summary>
    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (IsUnixNumber(text))
        {
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var secondsPart = dot < 0 ? text : text[..dot];
            if (!long.TryParse(secondsPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            result = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (dot >= 0)
            {
                var fraction = text[(dot + 1)..].PadRight(9, '0')[..9];
                if (!long.TryParse(fraction, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
                {
                    return false;
                }

                result = result.AddTicks(nanos / 100L);
            }

            return true;
        }

        // DateTimeOffset rejects RoundtripKind; AssumeUniversal covers timestamps written without a zone.
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out result);
    }

    private static bool IsUnixNumber(string text)
    {
        var seenDot = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.')
            {
                if (seenDot || i == 0 || i == text.Length - 1)
                {
                    return false;
                }

                seenDot = true;
                continue;
            }

            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
