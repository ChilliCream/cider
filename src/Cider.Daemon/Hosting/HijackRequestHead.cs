using System.Globalization;
using System.Text.RegularExpressions;
using Cider.Daemon.BuildKit;

namespace Cider.Daemon.Hosting;

/// <summary>Which Docker endpoint a hijacked request targets.</summary>
public enum HijackKind
{
    /// <summary><c>POST /exec/{id}/start</c>.</summary>
    ExecStart,

    /// <summary><c>POST /containers/{id}/attach</c>.</summary>
    ContainerAttach,

    /// <summary><c>POST /grpc</c> — BuildKit's control-plane connection.</summary>
    Grpc,

    /// <summary><c>POST /session</c> — a CLI session connection.</summary>
    Session,
}

/// <summary>
/// The parsed first request head of a connection, as far as the hijack interceptor cares: which of
/// the four streaming endpoints this is, does the client want the upgrade token the endpoint
/// requires (<c>tcp</c> for exec/attach, <c>h2c</c> for grpc/session), how long is the body it still
/// has to send, and — for grpc/session — the <c>X-Docker-Expose-Session-*</c> headers BuildKit's
/// session dialer sends (session/session.go:20-25, :101-110).
/// </summary>
public sealed partial record HijackRequestHead(
    HijackKind Kind,
    string Id,
    string Query,
    long ContentLength,
    bool Upgrade,
    string? SessionId = null,
    string? SessionSharedKey = null,
    IReadOnlyList<string>? SessionMethods = null)
{
    [GeneratedRegex(
        @"^POST\s+/(?:v\d+(?:\.\d+)?/)?(?<kind>exec|containers)/(?<id>[^/\s?]+)/(?<verb>start|attach)(?:\?(?<query>\S*))?\s+HTTP/1\.[01]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HijackRegex();

    [GeneratedRegex(
        @"^POST\s+/(?:v\d+(?:\.\d+)?/)?(?<kind>grpc|session)\s+HTTP/1\.[01]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TunnelRegex();

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

        var requestLine = lines[0].Trim();

        var tunnelMatch = TunnelRegex().Match(requestLine);
        if (tunnelMatch.Success)
        {
            return ParseTunnel(tunnelMatch, lines);
        }

        var match = HijackRegex().Match(requestLine);
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
            var (name, value) = SplitHeader(lines[i]);
            if (name is null)
            {
                continue;
            }

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

    private static HijackRequestHead ParseTunnel(Match tunnelMatch, string[] lines)
    {
        var kind = tunnelMatch.Groups["kind"].Value.Equals("grpc", StringComparison.OrdinalIgnoreCase)
            ? HijackKind.Grpc
            : HijackKind.Session;

        var upgrade = false;
        long contentLength = 0;
        string? sessionId = null;
        string? sessionSharedKey = null;
        List<string>? methods = null;

        for (var i = 1; i < lines.Length; i++)
        {
            var (name, value) = SplitHeader(lines[i]);
            if (name is null)
            {
                continue;
            }

            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("h2c", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = true;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                     parsed >= 0)
            {
                contentLength = parsed;
            }
            else if (name.Equals(BuildKitMethods.MetadataKeys.SessionUuid, StringComparison.OrdinalIgnoreCase))
            {
                sessionId = value;
            }
            else if (name.Equals(BuildKitMethods.MetadataKeys.SessionSharedKey, StringComparison.OrdinalIgnoreCase))
            {
                sessionSharedKey = value;
            }
            else if (name.Equals(BuildKitMethods.MetadataKeys.SessionGrpcMethod, StringComparison.OrdinalIgnoreCase))
            {
                methods ??= [];
                foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    methods.Add(part);
                }
            }
        }

        return new HijackRequestHead(kind, "", "", contentLength, upgrade, sessionId, sessionSharedKey, methods);
    }

    private static (string? Name, string Value) SplitHeader(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return (null, "");
        }

        return (line[..colon].Trim(), line[(colon + 1)..].Trim());
    }
}
