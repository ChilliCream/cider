using Cider.Core.Runtime;

namespace Cider.AppleContainer.Cli;

/// <summary>The result of one <c>container</c> invocation.</summary>
internal sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Classifies the CLI's stderr text into <see cref="RuntimeErrorKind"/>s and
/// <see cref="RuntimeErrorReason"/>s. Apple's CLI exits with 1 for every failure, so text is the
/// only signal (docs/apple-container-notes.md §12) — and this type is the only place in the daemon
/// allowed to read it. Everything above the <c>IContainerRuntime</c> seam branches on the classified
/// kind/reason instead.
/// </summary>
internal static class CliErrorMapper
{
    /// <summary>What Apple says about a container that is not running, in every phrasing seen so far
    /// (<c>container … is not running</c>, and the same nested inside <c>invalidState: "…"</c>).</summary>
    private const string NotRunningMarker = "is not running";

    /// <summary>The <see cref="NotRunningMarker"/> hits that are about the daemon, not a container.</summary>
    private static readonly string[] DaemonNotRunningMarkers =
    [
        "apiserver is not running",
        "system services are not running",
    ];

    private static readonly string[] UnavailableMarkers =
    [
        .. DaemonNotRunningMarkers,
        "could not connect",
        "connection was invalidated",
        "connection invalidated",
        "connection interrupted",
        "connection refused",
        "xpc",
    ];

    private static readonly string[] ConflictMarkers =
    [
        "already exists",
        NotRunningMarker,
        "is running and can not be deleted",
        "can not be deleted",
        "currently in use",
        "in use",
        "referring containers",
        "invalidstate",
    ];

    private static readonly string[] NotFoundMarkers =
    [
        "not found",
        "no such",
        "does not exist",
        "401 unauthorized",
    ];

    private static readonly string[] InvalidArgumentMarkers =
    [
        "invalidargument",
        "unknown option",
        "unexpected argument",
        "unknown command",
        "missing expected",
        "missing value",
    ];

    /// <summary>
    /// The tail swift-argument-parser prints when it rejects an argument:
    /// <code>
    /// Error: The value 'fd00::/64' is invalid for '--subnet &lt;subnet&gt;'
    /// Usage: container network create [--subnet &lt;subnet&gt;] &lt;name&gt;
    ///   See 'container network create --help' for more information.
    /// </code>
    /// Apple's CLI only prints it for an argument it could not parse, so it is a reliable marker of
    /// a client input error (400, not 500). It is also the reason a raw usage banner used to reach
    /// Docker clients verbatim: the banner is the <em>last</em> line of stderr, so the plain
    /// last-meaningful-line rule below picked it over the <c>Error:</c> line that says what was
    /// actually rejected.
    /// </summary>
    private const string UsageBannerMarker = "--help' for more information";

    private const string UsageLinePrefix = "usage:";

    private static readonly string[] NotSupportedMarkers =
    [
        "unsupported",
        "not supported",
        "not implemented",
    ];

    /// <summary>Maps one stderr blob to a runtime error kind.</summary>
    public static RuntimeErrorKind Classify(string? stderr)
    {
        var text = (stderr ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(text, UnavailableMarkers))
        {
            return RuntimeErrorKind.Unavailable;
        }

        // A usage banner means the CLI never ran the operation — it turned the arguments down.
        // Checked before the conflict/not-found markers because the banner text itself lists
        // options and can contain their words; only the runtime being down outranks it.
        if (HasUsageBanner(text))
        {
            return RuntimeErrorKind.InvalidArgument;
        }

        if (ContainsAny(text, ConflictMarkers))
        {
            return RuntimeErrorKind.Conflict;
        }

        if (ContainsAny(text, NotFoundMarkers))
        {
            return RuntimeErrorKind.NotFound;
        }

        // `network delete`/`volume delete` of a missing name produce a bare
        // "failed to delete one or more networks: [...]" with no "not found" text at all;
        // the conflict variants always carry an invalidState/in-use cause (handled above),
        // so by elimination this is a not-found (notes §12).
        if (text.Contains("failed to delete one or more", StringComparison.Ordinal) ||
            text.Contains("delete failed for one or more", StringComparison.Ordinal))
        {
            return RuntimeErrorKind.NotFound;
        }

        if (ContainsAny(text, InvalidArgumentMarkers))
        {
            return RuntimeErrorKind.InvalidArgument;
        }

        if (ContainsAny(text, NotSupportedMarkers))
        {
            return RuntimeErrorKind.NotSupported;
        }

        return RuntimeErrorKind.Internal;
    }

    /// <summary>
    /// <c>true</c> when a CLI failure text says a <em>container</em> is not running. This single
    /// method is what the <c>is not running</c> wording is worth to the daemon: it is turned into
    /// <see cref="RuntimeErrorReason.ContainerNotRunning"/> here, at the boundary, and nowhere else.
    /// </summary>
    public static bool IsContainerNotRunning(string? text)
    {
        var lower = (text ?? string.Empty).ToLowerInvariant();

        // "apiserver is not running" / "system services are not running" are the runtime itself
        // being down (Unavailable), which is a different thing from a container being stopped.
        return lower.Contains(NotRunningMarker, StringComparison.Ordinal) &&
            !ContainsAny(lower, DaemonNotRunningMarkers);
    }

    /// <summary>Maps one stderr blob to the finer cause within <paramref name="kind"/>, if there is one.</summary>
    public static RuntimeErrorReason ClassifyReason(string? text, RuntimeErrorKind kind) =>
        kind == RuntimeErrorKind.Conflict && IsContainerNotRunning(text)
            ? RuntimeErrorReason.ContainerNotRunning
            : RuntimeErrorReason.None;

    /// <summary>
    /// The human-readable part of a CLI failure: the last meaningful stderr line, minus the
    /// <c>Error: </c> prefix — where "meaningful" excludes the usage banner, whose lines carry no
    /// information a Docker client can act on and used to be all a client saw
    /// (<see cref="UsageBannerMarker"/>).
    /// </summary>
    public static string ExtractMessage(string? stderr, string? stdout = null)
    {
        var line = LastMeaningfulLine(stderr) ?? LastMeaningfulLine(stdout);
        if (line is null)
        {
            return HasUsageBanner((stderr ?? string.Empty).ToLowerInvariant()) ||
                HasUsageBanner((stdout ?? string.Empty).ToLowerInvariant())
                ? "the container CLI rejected the arguments"
                : "container CLI failed";
        }

        if (line.StartsWith("Error: ", StringComparison.Ordinal))
        {
            line = line["Error: ".Length..];
        }

        return line.Trim();
    }

    /// <summary>The prefix Apple's store uses for a dangling content reference it cannot resolve —
    /// distinct from every other failure this mapper classifies (cider-ede.24, verified live: a
    /// listing fails with exactly <c>Error: content with digest sha256:&lt;hex&gt;</c> while the blob
    /// file itself is missing from the store, though <c>state.json</c> still names it).</summary>
    private const string DanglingContentMarker = "content with digest";

    /// <summary>
    /// True when a failure is Apple's own store reporting one dangling content reference among
    /// otherwise-good entries, not a genuine runtime failure. This is deliberately narrower than the
    /// generic <see cref="NotFoundMarkers"/> check: those mean "the one thing asked for was not
    /// found"; this means "the whole listing call itself is poisoned by one bad row", which a caller
    /// must degrade (return what it can enumerate) rather than fail outright (cider-ede.24).
    /// </summary>
    public static bool IsDanglingContent(string? stderr) =>
        (stderr ?? string.Empty).Contains(DanglingContentMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>sha256:…</c> digest named by an <see cref="IsDanglingContent"/> failure, for
    /// the operator-facing warning — <c>null</c> when the text has no recognizable digest.</summary>
    public static string? ExtractDanglingDigest(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        const string Prefix = "sha256:";
        var start = stderr.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var hexStart = start + Prefix.Length;
        var end = hexStart;
        while (end < stderr.Length && char.IsAsciiHexDigit(stderr[end]))
        {
            end++;
        }

        return end > hexStart ? stderr[start..end] : null;
    }

    /// <summary>
    /// Shared operator-facing text for a dangling/unresolvable content reference — used verbatim by
    /// both the CLI transport's <c>image ls</c> failure (this file) and the XPC transport's per-digest
    /// <c>contentGet</c> failure (<c>XpcContainerRuntime.Images.cs</c>), so an operator sees identical
    /// guidance regardless of which transport served the request (cider-ede.24 fix direction item 3:
    /// "carrying the same operator remedy text as the CLI path").
    /// </summary>
    public static string DanglingContentRemedy(string digest) =>
        $"the Apple container image store has a dangling content reference ({digest}) that could not be " +
        "resolved; `docker images` output may be incomplete until it is repaired with Apple's own " +
        "tooling (`container image prune`, or `container image delete <ref>` for the offending image) " +
        "-- cider does not modify Apple's store";

    /// <summary>Builds the exception for a failed invocation.</summary>
    public static RuntimeException ToException(CliResult result, string context)
    {
        var message = ExtractMessage(result.Stderr, result.Stdout);
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        var kind = Classify(text);
        var reason = ClassifyReason(text, kind);
        return new RuntimeException(kind, string.IsNullOrEmpty(context) ? message : $"{context}: {message}", reason);
    }

    private static string? LastMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Everything from the usage synopsis onwards is banner: the synopsis line, its wrapped
        // continuations, and the "See '… --help'" pointer. Stop before it, so what is reported is
        // the `Error:` line naming what the CLI rejected.
        var end = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsBannerLine(lines[i]))
            {
                end = i;
                break;
            }
        }

        for (var i = end - 1; i >= 0; i--)
        {
            var candidate = lines[i].Trim();
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary><c>true</c> when the text carries swift-argument-parser's usage banner.</summary>
    private static bool HasUsageBanner(string lowerText) =>
        lowerText.Contains(UsageBannerMarker, StringComparison.Ordinal);

    /// <summary>
    /// <c>true</c> for the line that opens the usage banner. Only the synopsis line and the
    /// <c>See '… --help'</c> pointer are recognised; the synopsis' wrapped continuation lines are
    /// dropped by virtue of following it.
    /// </summary>
    private static bool IsBannerLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith(UsageLinePrefix, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(UsageBannerMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
