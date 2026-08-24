using System.Text.Json;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Services;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>Docker Engine API build endpoints: <c>POST /build</c>, <c>POST /build/prune</c> (owner: daemon-resources).</summary>
public static class BuildRoutes
{
    public static void MapBuildRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/build", async (HttpRequest request, HttpResponse response, ImageManager images, CancellationToken ct) =>
        {
            var buildRequest = new BuildRequest
            {
                Dockerfile = NullIfEmpty(request.Query["dockerfile"].ToString()) ?? "Dockerfile",
                Tags = request.Query["t"].Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList(),
                BuildArgs = ParseStringMap(request.Query["buildargs"].ToString()),
                Labels = ParseStringMap(request.Query["labels"].ToString()),
                Target = NullIfEmpty(request.Query["target"].ToString()),
                Platform = NullIfEmpty(request.Query["platform"].ToString()),
                NoCache = QueryBool(request, "nocache"),
                Pull = QueryBool(request, "pull"),
                Quiet = QueryBool(request, "q"),
                Remote = NullIfEmpty(request.Query["remote"].ToString()),
            };

            // `X-Registry-Config` (per-registry auth for FROM pulls during the build) is accepted
            // and ignored — Apple `container build` has no way to plumb it through.
            var writer = await DockerResults.BeginNdjsonAsync(response, ct).ConfigureAwait(false);
            var progress = DockerResults.ProgressTo(writer);
            try
            {
                await images.BuildAsync(buildRequest, request.Body, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                var message = ex is DockerApiException dex ? dex.Message : ex.Message;
                await TryWriteErrorAsync(writer, message, ct).ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            return Results.Empty;
        });

        // Apple `container` has no build-cache concept exposed to us; report an always-empty cache.
        app.MapPost("/build/prune", () => DockerResults.Json(new BuildCachePruneResponse()));
    }

    // ---- helpers ------------------------------------------------------

    private static async Task TryWriteErrorAsync(NdjsonWriter writer, string message, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(new JsonMessage { Error = message, ErrorDetail = new JsonError { Message = message } }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best effort — the client may already be gone.
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool QueryBool(HttpRequest request, string key)
    {
        var value = request.Query[key].ToString();
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseStringMap(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw DockerErrors.BadParameter($"invalid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    // Docker's buildargs may carry `{"FOO":null}` to mean "inherit from the environment";
                    // we have no host environment to inherit from, so the arg is simply omitted.
                    continue;
                }

                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }

        return result;
    }
}
