using System.Globalization;
using Cider.Core.DockerApi;

namespace Cider.Daemon.Hosting;

/// <summary>Reads Docker's query parameters exactly the way <c>httputils.BoolValue</c> and friends do.</summary>
public static class QueryValues
{
    /// <summary>Docker's boolean query semantics: anything but <c>""</c>/<c>0</c>/<c>no</c>/<c>false</c>/<c>none</c> is true.</summary>
    public static bool Bool(HttpRequest request, string name, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Query[name].ToString();
        if (string.IsNullOrEmpty(value))
        {
            return request.Query.ContainsKey(name) ? false : fallback;
        }

        return !value.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An optional string parameter; empty values are treated as absent.</summary>
    public static string? Text(HttpRequest request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Query[name].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>An optional integer parameter; a non-numeric value is a 400.</summary>
    public static int? Int(HttpRequest request, string name)
    {
        var value = Text(request, name);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw DockerErrors.BadParameter($"invalid value for {name}: {value}");
        }

        return parsed;
    }

    /// <summary>Docker's <c>tail</c> parameter: <c>all</c> (or absent) means everything.</summary>
    public static int? Tail(HttpRequest request)
    {
        var value = Text(request, "tail");
        if (value is null || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    /// <summary>Parses the <c>filters</c> query parameter (both JSON encodings Docker uses).</summary>
    public static Filters Filters(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Core.DockerApi.Filters.Parse(request.Query["filters"].ToString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            throw DockerErrors.BadParameter($"invalid filters: {ex.Message}");
        }
    }
}
