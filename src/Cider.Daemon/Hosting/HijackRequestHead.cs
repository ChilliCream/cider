using System.Globalization;
using System.Text.RegularExpressions;

namespace Cider.Daemon.Hosting;

/// <summary>Which Docker endpoint a hijacked request targets.</summary>
public enum HijackKind
{
    /// <summary><c>POST /exec/{id}/start</c>.</summary>
    ExecStart,

    /// <summary><c>POST /containers/{id}/attach</c>.</summary>
    ContainerAttach,
}

/// <summary>
/// The parsed first request head of a connection, as far as the hijack interceptor cares: is this
/// one of the two streaming endpoints, does the client want <c>Upgrade: tcp</c>, and how long is
/// the body it still has to send?
/// </summary>
public sealed partial record HijackRequestHead(HijackKind Kind, string Id, string Query, long ContentLength, bool Upgrade)
{
    [GeneratedRegex(
        @"^POST\s+/(?:v\d+(?:\.\d+)?/)?(?<kind>exec|containers)/(?<id>[^/\s?]+)/(?<verb>start|attach)(?:\?(?<query>\S*))?\s+HTTP/1\.[01]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HijackRegex();

    /// <summary>
    /// Parses <paramref name="head"/> (request line + headers, without the terminating blank line).
    /// Returns <c>null</c> when the request is not hijackable — the connection is then handed to
    /// Kestrel unchanged. Bare-LF line endings are tolerated (some clients emit them).
    /// </summary>
    public static HijackRequestHead? TryParse(string head)
    {
        if (string.IsNullOrEmpty(head))
        {
            return null;
        }

        var lines = head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0)
        {
            return null;
        }

        var match = HijackRegex().Match(lines[0].Trim());
        if (!match.Success)
        {
            return null;
        }

        // `exec/{id}/start` and `containers/{id}/attach` are the two hijack endpoints;
        // `exec/{id}/attach` and `containers/{id}/start` are ordinary requests.
        var isExec = match.Groups["kind"].Value.Equals("exec", StringComparison.OrdinalIgnoreCase);
        var isStart = match.Groups["verb"].Value.Equals("start", StringComparison.OrdinalIgnoreCase);
        if (isExec != isStart)
        {
            return null;
        }

        var upgrade = false;
        long contentLength = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("tcp", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = true;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                     parsed >= 0)
            {
                contentLength = parsed;
            }
        }

        return new HijackRequestHead(
            isExec ? HijackKind.ExecStart : HijackKind.ContainerAttach,
            match.Groups["id"].Value,
            match.Groups["query"].Success ? match.Groups["query"].Value : "",
            contentLength,
            upgrade);
    }
}
