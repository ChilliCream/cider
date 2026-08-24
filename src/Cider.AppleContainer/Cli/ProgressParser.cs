using System.Globalization;
using System.Text.RegularExpressions;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Cli;

/// <summary>
/// Turns <c>--progress plain</c> output into <see cref="ProgressEvent"/>s
/// (docs/apple-container-notes.md §2 for pull, §10 for build).
/// </summary>
internal static partial class ProgressParser
{
    /// <summary><c>[1/2] Fetching image 12% (20 of 56 blobs, 3.6/28.3 MB, 4 KB/s) [10s]</c>.</summary>
    [GeneratedRegex(@"^\[(?<step>\d+)/(?<steps>\d+)\]\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PullLineRegex();

    [GeneratedRegex(@"\((?<current>\d+) of (?<total>\d+) blobs", RegexOptions.CultureInvariant)]
    private static partial Regex BlobCountRegex();

    [GeneratedRegex(@"\s*\[\d+s\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ElapsedSuffixRegex();

    /// <summary><c>#6 exporting manifest list sha256:611305aa… done</c>.</summary>
    [GeneratedRegex(@"exporting manifest list sha256:(?<digest>[0-9a-f]{64})", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestListRegex();

    [GeneratedRegex(@"exporting manifest sha256:(?<digest>[0-9a-f]{64})", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestRegex();

    /// <summary>Maps one line of <c>image pull|push --progress plain</c> output.</summary>
    public static ProgressEvent? ParsePullLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var match = PullLineRegex().Match(text);
        if (!match.Success)
        {
            return new ProgressEvent { Status = text };
        }

        var rest = ElapsedSuffixRegex().Replace(match.Groups["rest"].Value, "").Trim();
        if (rest.Length == 0)
        {
            rest = "Working";
        }

        long? current = null;
        long? total = null;
        var blobs = BlobCountRegex().Match(rest);
        if (blobs.Success &&
            long.TryParse(blobs.Groups["current"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cur) &&
            long.TryParse(blobs.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tot))
        {
            current = cur;
            total = tot;
        }

        return new ProgressEvent
        {
            Status = rest,
            Id = $"{match.Groups["step"].Value}/{match.Groups["steps"].Value}",
            Current = current,
            Total = total,
        };
    }

    /// <summary>The image id a build exported, read off the final <c>exporting manifest list</c> line.</summary>
    public static string? ParseBuiltImageId(string line)
    {
        var match = ManifestListRegex().Match(line);
        if (match.Success)
        {
            return $"sha256:{match.Groups["digest"].Value}";
        }

        match = ManifestRegex().Match(line);
        return match.Success ? $"sha256:{match.Groups["digest"].Value}" : null;
    }
}
