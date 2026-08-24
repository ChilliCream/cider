using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Cider.Core.Time;

namespace Cider.Core.DockerApi;

/// <summary>
/// The <c>filters</c> query parameter of the Docker Engine API. Accepts both wire encodings:
/// the current <c>{"label":{"k=v":true}}</c> map form and the legacy <c>{"label":["k=v"]}</c> array form.
/// </summary>
public sealed class Filters
{
    private static readonly IReadOnlyList<string> NoValues = [];

    private readonly Dictionary<string, IReadOnlyList<string>> _values;

    private Filters(Dictionary<string, IReadOnlyList<string>> values) => _values = values;

    /// <summary>A filter set with no entries — every <c>Match*</c> call returns <c>true</c>.</summary>
    public static Filters Empty { get; } = new([]);

    /// <summary>All filter keys with their (OR-ed) values.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Values => _values;

    /// <summary><c>true</c> when no filter at all was supplied.</summary>
    public bool IsEmpty => _values.Count == 0;

    /// <summary>Parses the <c>filters</c> query value; <c>null</c>/empty yields <see cref="Empty"/>.</summary>
    /// <exception cref="DockerApiException">400 when the value is not valid filter JSON.</exception>
    public static Filters Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw DockerErrors.BadParameter($"invalid filter: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return Empty;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw DockerErrors.BadParameter("invalid filter: expected a JSON object");
            }

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                var values = ReadValues(property.Name, property.Value);
                if (result.TryGetValue(property.Name, out var existing))
                {
                    var merged = new List<string>(existing);
                    merged.AddRange(values);
                    result[property.Name] = merged;
                }
                else
                {
                    result[property.Name] = values;
                }
            }

            return new Filters(result);
        }
    }

    /// <summary>
    /// Rejects any key outside <paramref name="accepted"/>, the way dockerd's
    /// <c>filters.Args.Validate(acceptedFilters)</c> does per endpoint. Returns <c>this</c> so it can
    /// be chained onto a <see cref="Parse(string?)"/>.
    /// </summary>
    /// <remarks>
    /// Without it an unknown key is silently ignored and the request runs unfiltered — which on a
    /// prune endpoint means a typo in the guard that was meant to protect something deletes it
    /// instead.
    /// </remarks>
    /// <exception cref="DockerApiException">400 <c>invalid filter '&lt;name&gt;'</c>.</exception>
    public Filters Validate(params string[] accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        foreach (var key in _values.Keys)
        {
            if (!accepted.Contains(key, StringComparer.Ordinal))
            {
                throw DockerErrors.InvalidFilter(key);
            }
        }

        return this;
    }

    private static List<string> ReadValues(string key, JsonElement element)
    {
        var values = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                break;

            case JsonValueKind.String:
                // Tolerated shorthand: {"label":"k=v"}
                values.Add(element.GetString() ?? "");
                break;

            case JsonValueKind.Array:
                // Legacy encoding: {"label":["k=v"]}
                foreach (var item in element.EnumerateArray())
                {
                    values.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());
                }

                break;

            case JsonValueKind.Object:
                // Current encoding: {"label":{"k=v":true}} — only entries whose value is true count.
                foreach (var member in element.EnumerateObject())
                {
                    if (member.Value.ValueKind != JsonValueKind.False)
                    {
                        values.Add(member.Name);
                    }
                }

                break;

            default:
                throw DockerErrors.BadParameter($"invalid filter '{key}'");
        }

        return values;
    }

    /// <summary>Values of <paramref name="key"/>, or an empty list when the key is absent.</summary>
    public IReadOnlyList<string> Get(string key) =>
        _values.TryGetValue(key, out var values) ? values : NoValues;

    /// <summary><c>true</c> when <paramref name="key"/> was supplied (with at least one value).</summary>
    public bool Contains(string key) => _values.TryGetValue(key, out var values) && values.Count > 0;

    /// <summary>Tries to read the single value of <paramref name="key"/>.</summary>
    public bool TryGetSingle(string key, [NotNullWhen(true)] out string? value)
    {
        if (_values.TryGetValue(key, out var values) && values.Count > 0)
        {
            value = values[0];
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Docker's <c>label</c>/<c>label!</c> filters: every <c>label</c> entry must match (AND), where
    /// <c>k</c> means "label present" and <c>k=v</c> means "label present with this exact value";
    /// then, if any <c>label!</c> entries were supplied, the object is excluded once *all* of them
    /// also match — mirrors moby's <c>matchLabels</c> (<c>daemon/prune.go</c>), including the AND
    /// (not OR) semantics of the negated list.
    /// </summary>
    /// <remarks>
    /// <c>label!</c> used to be accepted by <see cref="Validate"/> on every prune endpoint but had no
    /// effect here, so a caller asking to keep everything carrying a given label instead had it
    /// pruned right along with the rest.
    /// </remarks>
    public bool MatchesLabels(IReadOnlyDictionary<string, string>? labels)
    {
        if (!MatchesLabelEntries(Get("label"), labels))
        {
            return false;
        }

        var negated = Get("label!");
        if (negated.Count > 0 && MatchesLabelEntries(negated, labels))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesLabelEntries(IReadOnlyList<string> entries, IReadOnlyDictionary<string, string>? labels)
    {
        if (entries.Count == 0)
        {
            return true;
        }

        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                if (labels is null || !labels.ContainsKey(entry))
                {
                    return false;
                }

                continue;
            }

            var key = entry[..separator];
            var value = entry[(separator + 1)..];
            if (labels is null || !labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses the <c>until</c> filter the way dockerd's <c>getUntilFromPruneFilters</c> does
    /// (<c>daemon/prune.go</c> for containers/networks, <c>daemon/images/image_prune.go</c> for
    /// images), returning <c>null</c> when no <c>until</c> filter was supplied.
    /// </summary>
    /// <param name="wrapMessage">
    /// Wraps the raw parse-failure detail into the caller's exact wording. Containers and networks
    /// prune pass the detail through unwrapped; images prune prefixes it with
    /// <c>"invalid value for 'until' filter: "</c>. Pass <c>null</c> for the unwrapped form.
    /// </param>
    /// <exception cref="DockerApiException">
    /// 400 when more than one <c>until</c> value was supplied, or the single value does not parse as
    /// an RFC3339 timestamp or a unix timestamp.
    /// </exception>
    /// <remarks>
    /// An unparseable value used to be silently treated as "nothing to exclude", which pruned every
    /// candidate instead of rejecting the request.
    /// </remarks>
    public DateTimeOffset? ResolveUntil(Func<string, string>? wrapMessage = null)
    {
        var values = Get("until");
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count > 1)
        {
            throw DockerErrors.BadParameter("more than one until filter specified");
        }

        if (!DockerTime.TryParse(values[0], out var parsed))
        {
            var detail = DescribeUntilParseFailure(values[0]);
            throw DockerErrors.BadParameter(wrapMessage is null ? detail : wrapMessage(detail));
        }

        return parsed;
    }

    /// <summary>
    /// Approximates the message Go's <c>timestamp.Parse</c> (<c>daemon/internal/timestamp</c>)
    /// produces for a value that is neither a duration, an RFC3339-ish timestamp, nor a unix
    /// timestamp — the two branches its control flow actually takes for the inputs this daemon's own
    /// <see cref="DockerTime.TryParse"/> also rejects (see <c>DockerTimeTests.TryParse_rejects_garbage</c>):
    /// a value containing <c>-</c> is presumed a malformed RFC3339-like timestamp and gets Go's raw
    /// <c>time.Parse</c> error shape; anything else is presumed a malformed unix timestamp.
    /// </summary>
    private static string DescribeUntilParseFailure(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return "failed to parse value as time or duration: value is empty";
        }

        if (text.Contains('-', StringComparison.Ordinal))
        {
            return $"parsing time \"{text}\" as \"2006-01-02\": cannot parse \"{text}\" as \"2006\"";
        }

        return $"failed to parse value as time or duration: invalid seconds \"{text}\": invalid syntax";
    }

    /// <summary>An absent key matches everything; otherwise <paramref name="value"/> must equal one of the filter values.</summary>
    public bool MatchExact(string key, string? value)
    {
        var wanted = Get(key);
        if (wanted.Count == 0)
        {
            return true;
        }

        foreach (var candidate in wanted)
        {
            if (string.Equals(candidate, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An absent key matches everything; otherwise <paramref name="predicate"/> must accept one of the values.</summary>
    public bool MatchAny(string key, Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var wanted = Get(key);
        if (wanted.Count == 0)
        {
            return true;
        }

        foreach (var candidate in wanted)
        {
            if (predicate(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Docker's <c>name</c> filter: substring match against the container name (leading '/' ignored).</summary>
    public bool MatchName(string? name)
    {
        var wanted = Get("name");
        if (wanted.Count == 0)
        {
            return true;
        }

        var actual = (name ?? "").TrimStart('/');
        foreach (var candidate in wanted)
        {
            if (actual.Contains(candidate.TrimStart('/'), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Docker's <c>id</c> filter: prefix match against the full 64-hex id.</summary>
    public bool MatchId(string? id)
    {
        var wanted = Get("id");
        if (wanted.Count == 0)
        {
            return true;
        }

        var actual = id ?? "";
        foreach (var candidate in wanted)
        {
            if (actual.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
