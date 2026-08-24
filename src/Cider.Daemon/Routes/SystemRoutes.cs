using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Services;
using Cider.Core.Time;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>The system endpoints: <c>/_ping</c>, <c>/version</c>, <c>/info</c>, <c>/events</c>, <c>/system/df</c>, <c>/auth</c>.</summary>
public static class SystemRoutes
{
    /// <summary>Maps every system route onto <paramref name="app"/>.</summary>
    public static void MapSystemRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/_ping", (HttpContext http, SystemManager system) =>
        {
            WritePingHeaders(http, system.Ping());
            return Results.Text("OK", "text/plain");
        });

        app.MapMethods("/_ping", ["HEAD"], (HttpContext http, SystemManager system) =>
        {
            WritePingHeaders(http, system.Ping());
            http.Response.ContentType = "text/plain";
            http.Response.ContentLength = 0;
            return Task.CompletedTask;
        });

        app.MapGet("/version", async (SystemManager system, CancellationToken ct) =>
            DockerResults.Json(await system.VersionAsync(ct)));

        app.MapGet("/info", async (SystemManager system, CancellationToken ct) =>
            DockerResults.Json(await system.InfoAsync(ct)));

        app.MapGet("/system/df", async (SystemManager system, CancellationToken ct) =>
            DockerResults.Json(await system.DiskUsageAsync(ct)));

        app.MapPost("/auth", async (HttpContext http, ImageManager images, CancellationToken ct) =>
        {
            var request = await DockerJson.DeserializeAsync<AuthConfig>(http.Request.Body, ct) ?? new AuthConfig();
            return DockerResults.Json(await images.LoginAsync(request, ct));
        });

        app.MapGet("/events", EventsAsync);

        // dockerd answers the bare root (and anything unrouted) with this exact body.
        app.MapGet("/", () => DockerResults.Json(new ErrorResponse { Message = "page not found" }, 404));
    }

    private static async Task EventsAsync(HttpContext http, EventBus events)
    {
        var query = http.Request.Query;
        var filters = Filters.Parse(query["filters"]);
        var until = ParseTimestamp(query["until"], "until");

        // The subscription only starts once the response headers are on the wire, so a client that
        // acts the moment it sees them could race past an event. Replaying from the arrival instant
        // closes that window (the bus keeps a ring buffer) and matches "only new events" semantics.
        var since = ParseTimestamp(query["since"], "since") ?? DateTimeOffset.UtcNow;

        var ct = http.RequestAborted;
        await using var writer = await DockerResults.BeginNdjsonAsync(http.Response, ct);

        try
        {
            await foreach (var message in events.Subscribe(filters, since, until, ct))
            {
                await writer.WriteAsync(message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The client disconnected; that is how `docker events` normally ends.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The client's socket died mid-stream.
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DockerTime.TryParse(value, out var parsed))
        {
            throw DockerErrors.BadParameter($"invalid value for {name}: {value}");
        }

        return parsed;
    }

    private static void WritePingHeaders(HttpContext http, PingInfo ping)
    {
        var headers = http.Response.Headers;
        headers["API-Version"] = ping.ApiVersion;
        headers["Builder-Version"] = ping.BuilderVersion;
        headers["Docker-Experimental"] = ping.Experimental ? "true" : "false";
        headers["Ostype"] = ping.OsType;
        headers["Swarm"] = ping.Swarm;
        headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "Thu, 01 Jan 1970 00:00:00 GMT";
    }
}
