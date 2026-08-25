using Cider.Core.Ids;
using Google.Protobuf.Collections;
using Grpc.Core;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Rewrites a <see cref="SolveRequest"/>'s <c>moby</c> exporter(s) into <c>docker</c> (a tar,
/// captured server-side through <see cref="SessionBridgeHandle"/> rather than uploaded to a
/// registry) — see cider-ger.10's problem statement for why: stock buildkitd has no
/// <c>moby</c> exporter, and buildx's docker driver sends nothing else.
/// <para>
/// Purely a request mutator plus a manifest of what it did (<see cref="RewriteResult"/>): it never
/// talks to buildkitd, a session, or the runtime. <see cref="ControlProxyService.Solve"/> uses the
/// result to arm <see cref="SessionBridgeHandle.CaptureExporterIds"/> and, after the real Solve
/// completes, to drive <see cref="ExportLoader"/> and rewrite the response.
/// </para>
/// </summary>
public static class SolveRewriter
{
    private const string MobyExporterType = "moby";
    private const string DockerExporterType = "docker";
    private const string NameAttr = "name";
    private const string TarAttr = "tar";
    private const string PlatformFrontendAttr = "platform";

    /// <summary>Exporter attrs the moby exporter accepts but the docker (tar) exporter does not understand — dropped.</summary>
    private static readonly string[] AttrsToDrop = ["push", "push-by-digest", "unpack", "buildinfo-attrs"];

    /// <summary>
    /// Rewrites every <c>moby</c>-typed exporter in <paramref name="request"/> to <c>docker</c> +
    /// <c>tar=true</c> in place, and returns what it did. A no-op (empty result) for an
    /// <see cref="SolveRequest.Internal"/> request (the BuildOpts probe, <c>--call</c>) or one that
    /// carries no <c>moby</c> exporter at all.
    /// </summary>
    /// <exception cref="RpcException">
    /// The request asks for a multi-platform build alongside a <c>moby</c>/<c>docker</c> exporter —
    /// the docker exporter cannot produce a manifest list (buildkit <c>exporter/export.go:135-137</c>).
    /// </exception>
    public static RewriteResult Rewrite(SolveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rewritten = new List<RewrittenExporter>();

        if (!request.Internal)
        {
            for (var i = 0; i < request.Exporters.Count; i++)
            {
                var exporter = request.Exporters[i];
                if (!string.Equals(exporter.Type, MobyExporterType, StringComparison.Ordinal))
                {
                    continue;
                }

                var entry = RewriteAttrs(exporter.Attrs, i);
                exporter.Type = DockerExporterType;
                rewritten.Add(entry);

                // buildx's docker driver duplicates Exporters[0] into the deprecated singular
                // fields for older-server compatibility; mirror only the first rewritten exporter
                // there too, since ExporterDeprecated/ExporterAttrsDeprecated have no index of
                // their own to address a second one by.
                if (rewritten.Count == 1)
                {
                    request.ExporterDeprecated = DockerExporterType;
                    request.ExporterAttrsDeprecated.Clear();
                    foreach (var kvp in exporter.Attrs)
                    {
                        request.ExporterAttrsDeprecated[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (request.Exporters.Count == 0 &&
                string.Equals(request.ExporterDeprecated, MobyExporterType, StringComparison.Ordinal))
            {
                var entry = RewriteAttrs(request.ExporterAttrsDeprecated, index: 0);
                request.ExporterDeprecated = DockerExporterType;
                rewritten.Add(entry);
            }
        }

        if (rewritten.Count > 0 && IsMultiPlatform(request))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "docker exporter does not currently support exporting manifest lists"));
        }

        return new RewriteResult(rewritten);
    }

    private static bool IsMultiPlatform(SolveRequest request) =>
        request.FrontendAttrs.TryGetValue(PlatformFrontendAttr, out var platforms) &&
        platforms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1;

    /// <summary>
    /// Mutates <paramref name="attrs"/> in place (drops <see cref="AttrsToDrop"/>, normalizes
    /// <c>name</c>'s comma-separated tags, sets <c>tar=true</c>) and reports the normalized tags and
    /// any synthetic tag minted for an empty <c>name</c>. Every other attr (<c>source-date-epoch</c>,
    /// <c>compression*</c>, <c>oci-mediatypes</c>, <c>annotation*</c>, <c>rewrite-timestamp</c>, ...)
    /// is left exactly as the caller sent it.
    /// </summary>
    private static RewrittenExporter RewriteAttrs(MapField<string, string> attrs, int index)
    {
        foreach (var key in AttrsToDrop)
        {
            attrs.Remove(key);
        }

        var (tags, synthetic) = NormalizeName(attrs.TryGetValue(NameAttr, out var name) ? name : null);

        var allNames = synthetic is null ? tags : [.. tags, synthetic];
        attrs[NameAttr] = string.Join(',', allNames);
        attrs[TarAttr] = "true";

        return new RewrittenExporter { Index = index, Tags = tags, SyntheticTag = synthetic };
    }

    /// <summary>
    /// Splits a comma-separated <c>name</c> attr and normalizes each tag exactly the way
    /// <c>ImageManager.BuildAsync</c> normalizes <c>-t</c> tags before handing them to the runtime.
    /// An empty/missing name (no <c>-t</c> at all) mints a fresh <see cref="SyntheticBuildTag"/>
    /// instead, normalized the same way <c>ImageManager</c>'s own commit/import paths do.
    /// </summary>
    private static (IReadOnlyList<string> Tags, string? SyntheticTag) NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            var synthetic = ImageReference.Parse(SyntheticBuildTag.New()).Normalize().ToString();
            return ([], synthetic);
        }

        var tags = name
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => ImageReference.TryParse(tag, out var parsed) ? parsed.Normalize().ToString() : tag)
            .ToList();

        return (tags, null);
    }
}

/// <summary>One <c>moby</c> exporter <see cref="SolveRewriter.Rewrite"/> turned into <c>docker</c>.</summary>
public sealed class RewrittenExporter
{
    /// <summary>Index into <see cref="SolveRequest.Exporters"/> (or 0 for the deprecated-only shape).</summary>
    public required int Index { get; init; }

    /// <summary>The normalized <c>-t</c> tags the caller actually asked for — excludes <see cref="SyntheticTag"/>.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>
    /// The <see cref="Cider.Core.Ids.SyntheticBuildTag"/> minted for an untagged build (<c>name</c>
    /// was empty), normalized; <see langword="null"/> when the caller supplied at least one tag.
    /// Still applied to the image on load (so it is dangling-visible, matching classic
    /// <c>docker build</c> with no <c>-t</c>) but never shown back to the caller.
    /// </summary>
    public string? SyntheticTag { get; init; }
}

/// <summary>What <see cref="SolveRewriter.Rewrite"/> did to a <see cref="SolveRequest"/>.</summary>
public sealed class RewriteResult
{
    public RewriteResult(IReadOnlyList<RewrittenExporter> exporters)
    {
        ArgumentNullException.ThrowIfNull(exporters);
        Exporters = exporters;
        CaptureExporterIds = exporters.Select(e => e.Index).ToHashSet();
    }

    /// <summary>Every exporter rewritten from <c>moby</c> to <c>docker</c>, in the order encountered.</summary>
    public IReadOnlyList<RewrittenExporter> Exporters { get; }

    /// <summary><see cref="RewrittenExporter.Index"/> of every rewritten exporter, for <see cref="SessionBridgeHandle.CaptureExporterIds"/>.</summary>
    public IReadOnlySet<int> CaptureExporterIds { get; }
}
