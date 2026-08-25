using System.Text.RegularExpressions;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// The apiserver's <c>ping</c> reply <c>apiServerVersion</c> banner
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.1: <c>"container-apiserver version 1.3.0
/// (build: release, commit: d6de569)"</c>), parsed into a comparable semver plus the build/commit
/// carried alongside it — there is no separate numeric protocol-version field (§7).
/// </summary>
public sealed partial class ApiServerVersion
{
    /// <summary>
    /// The oldest apiserver cider talks XPC to. The wire protocol has been unchanged since 1.2.0
    /// (task cider-ede.4 description: 1.1.0→1.2.2 added only optional <c>kernelDigest</c>,
    /// <c>maskedPaths</c>, <c>readonlyPaths</c>; 1.2.2→1.3.0 changed nothing) and this is planner-1's
    /// binding ruling on cider-ede.4 (comment #27, 2026-08-25): below this, <c>auto</c> falls back to
    /// the CLI and <c>xpc</c> fails fast.
    /// </summary>
    public static readonly Version Minimum = new(1, 2, 0);

    /// <summary>
    /// The newest apiserver cider has actually been exercised against (same ruling). Newer still
    /// proceeds over XPC — it is only ever a warning, never a fallback or a failure.
    /// </summary>
    public static readonly Version Tested = new(1, 3, 0);

    private ApiServerVersion(Version semver, string build, string commit, string rawBanner)
    {
        Semver = semver;
        Build = build;
        Commit = commit;
        RawBanner = rawBanner;
    }

    /// <summary>The parsed <c>major.minor.patch</c>, comparable against <see cref="Minimum"/>/<see cref="Tested"/>.</summary>
    public Version Semver { get; }

    /// <summary><c>"release"</c> or <c>"debug"</c> (§7), or empty when the banner carried none.</summary>
    public string Build { get; }

    /// <summary>The abbreviated commit the banner carries, <c>"unspecified"</c>, or empty when the
    /// banner carried none — the full sha lives in the ping reply's separate <c>apiServerCommit</c>
    /// field (§7), which this type does not see.</summary>
    public string Commit { get; }

    /// <summary>The untouched banner string this was parsed from.</summary>
    public string RawBanner { get; }

    /// <summary><c>true</c> when <see cref="Semver"/> is older than <see cref="Minimum"/>.</summary>
    public bool IsBelowMinimum => Semver < Minimum;

    /// <summary><c>true</c> when <see cref="Semver"/> is newer than <see cref="Tested"/>.</summary>
    public bool IsNewerThanTested => Semver > Tested;

    public override string ToString() => RawBanner;

    /// <summary>
    /// Matches <c>"version 1.3.0"</c> plus an optional trailing <c>"(build: release, commit:
    /// d6de569)"</c> — the parenthesised part is intentionally optional so a banner shape that
    /// dropped it, or lost only its commit, still yields a usable semver rather than failing the
    /// whole parse.
    /// </summary>
    [GeneratedRegex(
        @"version\s+(?<version>\d+\.\d+\.\d+)(?:\s*\(build:\s*(?<build>[^,)]+)(?:,\s*commit:\s*(?<commit>[^)]+))?\))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex BannerRegex();

    /// <summary>Parses <paramref name="banner"/>; <c>false</c> when it does not contain a recognisable
    /// <c>version x.y.z</c>.</summary>
    public static bool TryParse(string? banner, out ApiServerVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(banner))
        {
            return false;
        }

        var match = BannerRegex().Match(banner);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var semver))
        {
            return false;
        }

        var build = match.Groups["build"].Success ? match.Groups["build"].Value.Trim() : "";
        var commit = match.Groups["commit"].Success ? match.Groups["commit"].Value.Trim() : "";
        version = new ApiServerVersion(semver, build, commit, banner);
        return true;
    }

    /// <exception cref="FormatException"><paramref name="banner"/> does not contain a recognisable
    /// <c>version x.y.z</c>.</exception>
    public static ApiServerVersion Parse(string banner)
    {
        if (!TryParse(banner, out var version))
        {
            throw new FormatException($"cannot parse apiserver version banner: '{banner}'");
        }

        return version!;
    }
}
