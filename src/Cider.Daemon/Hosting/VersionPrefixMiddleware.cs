using System.Text.RegularExpressions;

namespace Cider.Daemon.Hosting;

/// <summary>
/// Strips Docker's <c>/v1.xx</c> API-version prefix from the request path, exactly like dockerd:
/// any version is accepted (never rejected) and the routes below only ever see unprefixed paths.
/// </summary>
public sealed partial class VersionPrefixMiddleware(RequestDelegate next)
{
    /// <summary><c>HttpContext.Items</c> key holding the <c>/v1.xx</c> version the client asked for.</summary>
    public const string VersionItemKey = "cider.api-version";

    [GeneratedRegex(@"^/v(\d+)(?:\.(\d+))?(/.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    /// <summary>Rewrites the path and remembers the requested API version in <c>HttpContext.Items</c>.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? "";
        if (TryStrip(path, out var stripped, out var version))
        {
            context.Request.Path = stripped;
            context.Items[VersionItemKey] = version;
        }

        return next(context);
    }

    /// <summary>
    /// The <c>/v1.xx</c> version this request carried, or <c>null</c> when the client sent an
    /// unversioned path. Handlers use it for the handful of fields dockerd gates on the API version.
    /// </summary>
    public static string? RequestedVersion(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(VersionItemKey, out var value) ? value as string : null;
    }

    /// <summary>
    /// True when the requested version is at least <paramref name="major"/>.<paramref name="minor"/>.
    /// An unversioned (or unparseable) request counts as the newest API, which is what dockerd does:
    /// its version middleware defaults <c>api-version</c> to the daemon's own current version.
    /// </summary>
    public static bool IsAtLeast(HttpContext context, int major, int minor)
    {
        var version = RequestedVersion(context);
        if (string.IsNullOrEmpty(version))
        {
            return true;
        }

        var separator = version.IndexOf('.', StringComparison.Ordinal);
        var majorText = separator < 0 ? version : version[..separator];
        var minorText = separator < 0 ? "0" : version[(separator + 1)..];
        if (!int.TryParse(majorText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var actualMajor) ||
            !int.TryParse(minorText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var actualMinor))
        {
            return true;
        }

        return actualMajor != major ? actualMajor > major : actualMinor >= minor;
    }

    /// <summary>Removes a leading <c>/v1.47</c>-style prefix; returns <c>false</c> when there is none.</summary>
    public static bool TryStrip(string path, out string stripped, out string version)
    {
        stripped = path;
        version = "";

        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[0] != '/' || (path[1] != 'v' && path[1] != 'V'))
        {
            return false;
        }

        var match = PrefixRegex().Match(path);
        if (!match.Success)
        {
            return false;
        }

        version = match.Groups[2].Success
            ? $"{match.Groups[1].Value}.{match.Groups[2].Value}"
            : match.Groups[1].Value;
        stripped = match.Groups[3].Success && match.Groups[3].Value.Length > 0 ? match.Groups[3].Value : "/";
        return true;
    }
}
