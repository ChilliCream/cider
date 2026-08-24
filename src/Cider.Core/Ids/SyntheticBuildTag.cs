namespace Cider.Core.Ids;

/// <summary>
/// The synthetic tag <c>BuildImageAsync</c> hands Apple's <c>container build</c> when a
/// <c>docker build</c> request carries no <c>-t</c>: Apple's own default tag is a random UUID we
/// could never look back up, so the adapter mints a repo name of its own instead. Docker never
/// shows a build like that as tagged — real Docker leaves it <c>&lt;none&gt;:&lt;none&gt;</c>,
/// dangling and prunable — so every place the API surfaces references (list/inspect/history,
/// dangling filtering, prune) must recognize and hide this one shared shape.
/// </summary>
public static class SyntheticBuildTag
{
    private const string Prefix = "cider-build-";
    private const int SuffixLength = 32;

    /// <summary>Mints a fresh synthetic repo name, e.g. <c>cider-build-3f9a…</c> (32 hex chars).</summary>
    public static string New() => $"{Prefix}{Guid.NewGuid():N}";

    /// <summary>
    /// <c>true</c> when <paramref name="reference"/> names this synthetic repo (any domain/tag/digest
    /// combination Apple or Docker-normalization may have wrapped around it).
    /// </summary>
    public static bool IsSyntheticBuildTag(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        if (!ImageReference.TryParse(reference, out var parsed))
        {
            return false;
        }

        var path = parsed.Path;
        var lastSlash = path.LastIndexOf('/');
        var repoName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

        if (repoName.Length != Prefix.Length + SuffixLength || !repoName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in repoName.AsSpan(Prefix.Length))
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
