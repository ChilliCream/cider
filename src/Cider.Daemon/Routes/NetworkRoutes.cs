using System.Text.Json;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>Docker Engine API network endpoints (owner: daemon-resources).</summary>
public static class NetworkRoutes
{
    public static void MapNetworkRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/networks", async (HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var filters = Filters.Parse(request.Query["filters"].ToString());
            var list = await networks.ListAsync(filters, ct).ConfigureAwait(false);
            return DockerResults.Json(list);
        });

        app.MapGet("/networks/{id}", async (string id, HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var verbose = QueryBool(request, "verbose");
            var scope = NullIfEmpty(request.Query["scope"].ToString());
            var resource = await networks.InspectAsync(id, verbose, scope, ct).ConfigureAwait(false);
            return DockerResults.Json(resource);
        });

        app.MapPost("/networks/create", async (HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<NetworkCreateRequest>(request, ct).ConfigureAwait(false)
                ?? throw DockerErrors.BadParameter("invalid network create request");
            var response = await networks.CreateAsync(body, ct).ConfigureAwait(false);
            return DockerResults.Json(response, StatusCodes.Status201Created);
        });

        app.MapDelete("/networks/{id}", async (string id, NetworkManager networks, CancellationToken ct) =>
        {
            await networks.RemoveAsync(id, ct).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        // Apple `container` fixes a container's networks at create time, so these only work while
        // the container has never been started (the daemon then re-creates it on the engine with the
        // new network list); NetworkManager answers 501 for anything else.
        app.MapPost("/networks/{id}/connect", async (string id, HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<NetworkConnectRequest>(request, ct).ConfigureAwait(false)
                ?? throw DockerErrors.BadParameter("invalid network connect request");
            await networks.ConnectAsync(id, body, ct).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status200OK);
        });

        app.MapPost("/networks/{id}/disconnect", async (string id, HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<NetworkDisconnectRequest>(request, ct).ConfigureAwait(false)
                ?? throw DockerErrors.BadParameter("invalid network disconnect request");
            await networks.DisconnectAsync(id, body, ct).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status200OK);
        });

        app.MapPost("/networks/prune", async (HttpRequest request, NetworkManager networks, CancellationToken ct) =>
        {
            var filters = Filters.Parse(request.Query["filters"].ToString());
            var response = await networks.PruneAsync(filters, ct).ConfigureAwait(false);
            return DockerResults.Json(response);
        });
    }

    // ---- helpers ------------------------------------------------------

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

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
