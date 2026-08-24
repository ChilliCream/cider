using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>
/// The <c>/exec</c> endpoints. <c>POST /exec/{id}/start</c> normally never reaches this route:
/// when the client sends <c>Upgrade: tcp</c> the connection-level <see cref="HijackInterceptor"/>
/// owns it (Kestrel cannot upgrade a request with a body). The route still handles clients that
/// do not upgrade — they get a plain 200 stream, output only.
/// </summary>
public static class ExecRoutes
{
    /// <summary>Maps every exec route onto <paramref name="app"/>.</summary>
    public static void MapExecRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/exec/{id}/start", StartAsync);

        app.MapPost("/exec/{id}/resize", async (string id, HttpContext http, ExecManager execs, CancellationToken ct) =>
        {
            var rows = QueryValues.Int(http.Request, "h") ?? 0;
            var cols = QueryValues.Int(http.Request, "w") ?? 0;
            await execs.ResizeAsync(id, cols, rows, ct);
            return Results.Empty;
        });

        app.MapGet("/exec/{id}/json", async (string id, ExecManager execs, CancellationToken ct) =>
            DockerResults.Json(await execs.InspectAsync(id, ct)));
    }

    private static async Task StartAsync(string id, HttpContext http, ExecManager execs, ILoggerFactory loggerFactory)
    {
        var ct = http.RequestAborted;
        ExecStartRequest request;
        try
        {
            request = await DockerJson.DeserializeAsync<ExecStartRequest>(http.Request.Body, ct) ?? new ExecStartRequest();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw DockerErrors.BadParameter($"cider: invalid exec start body: {ex.Message}");
        }

        if (request.Detach)
        {
            await execs.StartDetachedAsync(id, ct);
            http.Response.StatusCode = 200;
            http.Response.ContentLength = 0;
            return;
        }

        var logger = loggerFactory.CreateLogger("Cider.Daemon.Exec");
        var session = await execs.StartAsync(id, request.Tty, request.ConsoleSize?.ToArray(), ct);

        try
        {
            await DockerResults.WriteStreamHeadersAsync(http.Response, session.Tty, ct);
            await StdioPump.RunAsync(null, http.Response.Body, session.Tty, session.Output, null, null, logger, ct);
            await session.Exited.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex) || ex is TimeoutException)
        {
            // Client gone, or the process outlived our patience.
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
