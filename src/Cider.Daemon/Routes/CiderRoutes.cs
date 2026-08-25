using System.Text.Json.Serialization;
using Cider.Core.Services;

namespace Cider.Daemon.Routes;

/// <summary>
/// cider's own private endpoints — not part of the Docker Engine API surface. Every path here lives
/// under <c>/_cider/</c> (mirroring dockerd's own <c>/_ping</c>) so it carries no <c>/v1.xx</c>
/// prefix, passes <see cref="Hosting.VersionPrefixMiddleware"/> untouched, and can never collide
/// with a Docker client path.
/// </summary>
public static class CiderRoutes
{
    /// <summary>Maps every cider-private route onto <paramref name="app"/>.</summary>
    public static void MapCiderRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // On-demand resync of the daemon's persisted state against Apple `container` — see `cider
        // sync`. A RuntimeException from an unreachable engine is left to ErrorMiddleware, which
        // renders it as the same Docker-style `{"message": ...}` envelope every other route uses.
        // SyncReport is not part of the Docker wire format, so it goes through its own tiny
        // source-generated context (below) rather than DockerJsonContext.
        app.MapPost("/_cider/sync", async (StateSynchronizer sync, CancellationToken ct) =>
            Results.Json(await sync.SyncAsync(ct), CiderJsonContext.Default.SyncReport));
    }
}

/// <summary>
/// Source-generated JSON contract for <see cref="SyncReport"/> and <see cref="SyncReportDto"/> — the
/// one cider-private type family that isn't part of the Docker wire format (and so has no entry in
/// <c>DockerJsonContext</c>). Shared by the <c>/_cider/sync</c> handler above, the <c>cider sync</c>
/// CLI verb that deserializes its response, and the E2E suite that talks to the endpoint directly —
/// hence public rather than internal.
/// </summary>
[JsonSerializable(typeof(SyncReport))]
[JsonSerializable(typeof(SyncReportDto))]
public sealed partial class CiderJsonContext : JsonSerializerContext;

/// <summary>
/// A deserialization-round-trippable mirror of <see cref="SyncReport"/>. <see cref="SyncReport"/>'s
/// <c>Containers</c>/<c>Networks</c>/<c>Volumes</c> properties are get-only, which is fine for the
/// serialize-only direction the daemon needs — but System.Text.Json silently leaves a get-only
/// property of a non-collection type untouched on deserialize (its default-constructed value stands,
/// so every nested list reads back empty). Every reader of the wire response — <c>cider sync</c>, the
/// E2E suite — deserializes into this instead.
/// </summary>
public sealed class SyncReportDto
{
    /// <summary>Mirrors <see cref="SyncReport.Containers"/>.</summary>
    public SyncResourceReportDto Containers { get; set; } = new();

    /// <summary>Mirrors <see cref="SyncReport.Networks"/>.</summary>
    public SyncResourceReportDto Networks { get; set; } = new();

    /// <summary>Mirrors <see cref="SyncReport.Volumes"/>.</summary>
    public SyncResourceReportDto Volumes { get; set; } = new();

    /// <summary>Mirrors <see cref="SyncReport.Warnings"/>.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Mirrors <see cref="SyncReport.IsEmpty"/>.</summary>
    public bool IsEmpty =>
        Containers.IsEmpty && Networks.IsEmpty && Volumes.IsEmpty && Warnings.Count == 0;
}

/// <summary>Mirrors <see cref="SyncResourceReport"/>; see <see cref="SyncReportDto"/>.</summary>
public sealed class SyncResourceReportDto
{
    /// <summary>Mirrors <see cref="SyncResourceReport.Removed"/>.</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Mirrors <see cref="SyncResourceReport.Adopted"/>.</summary>
    public List<string> Adopted { get; set; } = [];

    /// <summary>Mirrors <see cref="SyncResourceReport.Updated"/>.</summary>
    public List<string> Updated { get; set; } = [];

    /// <summary>Mirrors <see cref="SyncResourceReport.IsEmpty"/>.</summary>
    public bool IsEmpty => Removed.Count == 0 && Adopted.Count == 0 && Updated.Count == 0;
}
