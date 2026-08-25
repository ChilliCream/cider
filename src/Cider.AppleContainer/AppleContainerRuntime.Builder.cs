using System.Globalization;
using System.Text.RegularExpressions;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;

namespace Cider.AppleContainer;

/// <summary>
/// The Apple builder VM: <c>container builder status|start</c> plus the exec seam that opens a raw
/// duplex pipe to buildkitd. The builder's container name/runtimeId is the fixed <c>buildkit</c>
/// (labels <c>com.apple.container.plugin=builder</c>, <c>com.apple.container.resource.role=builder</c>);
/// there is no image override on the CLI, so <see cref="BuilderStatus.Image"/> is whatever Apple's
/// CLI reports.
/// </summary>
public sealed partial class AppleContainerRuntime
{
    /// <summary>How long a plain <c>builder status</c> query is given; it is a local, instant query
    /// on a healthy runtime, unlike <c>builder start</c> which may need to pull the shim image.</summary>
    private static readonly TimeSpan BuilderStatusTimeout = TimeSpan.FromSeconds(30);

    public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var result = await _cli.RunAsync(["builder", "status"], ct, BuilderStatusTimeout);
        if (!result.Succeeded)
        {
            // No builder has ever been created: treat like every other "nothing there" case in this
            // adapter (NotFound/Conflict-shaped stderr) rather than surfacing a hard failure — the
            // caller's contract is "null means no builder", not "throws when none exists yet".
            var kind = CliErrorMapper.Classify(result.Stderr);
            if (kind is RuntimeErrorKind.NotFound or RuntimeErrorKind.Conflict)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, "builder status");
        }

        return ParseBuilderStatus(result.Stdout);
    });

    public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct) => GuardAsync(async () =>
    {
        var args = ArgBuilder.BuilderStart(cpus, memoryBytes);

        // The shim image (ghcr.io/apple/container-builder-shim/builder) may need to be pulled on a
        // fresh machine, so this gets the same generous budget as an image pull rather than the
        // ordinary command timeout — starts against an already-cached image are 0.4-0.8s either way.
        var result = await _cli.RunAsync(args, ct, _options.PullTimeout);
        if (!result.Succeeded && !IsAlreadyRunning(result.Stderr))
        {
            ContainerCli.ThrowIfFailed(result, "builder start");
        }
    });

    public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct) => ExecAsync(
        "buildkit",
        new ExecSpec { Argv = ["buildctl", "dial-stdio"], OpenStdin = true, Tty = false },
        ct);

    private static bool IsAlreadyRunning(string? stderr) =>
        (stderr ?? string.Empty).Contains("already running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>container builder status</c>'s plain-text row:
    /// <c>buildkit  &lt;image&gt;  running|stopped  &lt;ip&gt;/&lt;prefix&gt;  &lt;cpus&gt;  &lt;mem&gt; MB</c>
    /// (columns separated by runs of two or more spaces — a single-space "mem MB" pair inside the last
    /// column survives that split intact). Any header row (first column <c>NAME</c>) is skipped; a row
    /// whose first column is not <c>buildkit</c> is not the builder and is ignored. <c>null</c> when no
    /// such row is present, which is how "no builder exists yet" reads on this CLI too.
    /// </summary>
    internal static BuilderStatus? ParseBuilderStatus(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        foreach (var rawLine in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var columns = ColumnSplitRegex().Split(line);
            if (columns.Length == 0 || string.Equals(columns[0], "NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(columns[0], "buildkit", StringComparison.Ordinal))
            {
                continue;
            }

            var image = columns.Length > 1 ? columns[1] : "";
            var running = columns.Length > 2 &&
                string.Equals(columns[2], "running", StringComparison.OrdinalIgnoreCase);

            string? address = columns.Length > 3 && columns[3].Length > 0 ? columns[3] : null;

            int? cpus = null;
            if (columns.Length > 4 && int.TryParse(columns[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCpus))
            {
                cpus = parsedCpus;
            }

            long? memoryBytes = null;
            if (columns.Length > 5)
            {
                var memoryMatch = MemoryMegabytesRegex().Match(columns[5]);
                if (memoryMatch.Success &&
                    long.TryParse(memoryMatch.Groups["mb"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var megabytes))
                {
                    memoryBytes = megabytes * 1024 * 1024;
                }
            }

            return new BuilderStatus
            {
                Name = columns[0],
                Image = image,
                Running = running,
                Address = address,
                Cpus = cpus,
                MemoryBytes = memoryBytes,
            };
        }

        return null;
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnSplitRegex();

    [GeneratedRegex(@"(?<mb>\d+)\s*MB", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MemoryMegabytesRegex();
}
