using System.Text.Json;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>Docker Engine API volume endpoints (owner: daemon-resources).</summary>
public static class VolumeRoutes
{
    public static void MapVolumeRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/volumes", async (HttpRequest request, VolumeManager volumes, CancellationToken ct) =>
        {
            var filters = Filters.Parse(request.Query["filters"].ToString());
            var response = await volumes.ListAsync(filters, ct).ConfigureAwait(false);
            return DockerResults.Json(response);
        });

        app.MapGet("/volumes/{name}", async (string name, VolumeManager volumes, CancellationToken ct) =>
        {
            var volume = await volumes.InspectAsync(name, ct).ConfigureAwait(false);
            return DockerResults.Json(volume);
        });

        app.MapPost("/volumes/create", async (HttpRequest request, VolumeManager volumes, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<VolumeCreateRequest>(request, ct).ConfigureAwait(false) ?? new VolumeCreateRequest();
            var volume = await volumes.CreateAsync(body, ct).ConfigureAwait(false);
            return DockerResults.Json(volume, StatusCodes.Status201Created);
        });

        app.MapDelete("/volumes/{name}", async (string name, HttpRequest request, VolumeManager volumes, CancellationToken ct) =>
        {
            var force = QueryBool(request, "force");
            await volumes.RemoveAsync(name, force, ct).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        app.MapPost("/volumes/prune", async (HttpRequest request, VolumeManager volumes, CancellationToken ct) =>
        {
            var filters = Filters.Parse(request.Query["filters"].ToString());
            var response = await volumes.PruneAsync(filters, ct).ConfigureAwait(false);
            return DockerResults.Json(response);
        });

        // Cluster volumes (swarm CSI) — Apple `container` has no such concept.
        app.MapPut("/volumes/{name}", (string name) =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: cluster volumes are not supported")));
    }

    // ---- helpers ------------------------------------------------------

    private static bool QueryBool(HttpRequest request, string key)
    {
        var value = request.Query[key].ToString();
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await DockerJson.DeserializeAsync<T>(request.Body, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw DockerErrors.BadParameter($"invalid JSON: {ex.Message}");
        }
    }
}
