using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Logs;
using Cider.Core.Services;
using Cider.Core.Time;
using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace Cider.Daemon.Routes;

/// <summary>Every <c>/containers</c> endpoint of the Docker Engine API.</summary>
public static class ContainerRoutes
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    /// <summary>Maps every container route onto <paramref name="app"/>.</summary>
    public static void MapContainerRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/containers/json", async (HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var list = await containers.ListAsync(
                QueryValues.Bool(http.Request, "all"),
                QueryValues.Int(http.Request, "limit"),
                QueryValues.Bool(http.Request, "size"),
                QueryValues.Filters(http.Request),
                ct);
            return DockerResults.Json(list);
        });

        app.MapPost("/containers/create", async (HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var request = await ReadBodyAsync<ContainerCreateRequest>(http, ct) ?? new ContainerCreateRequest();
            var created = await containers.CreateAsync(
                request,
                QueryValues.Text(http.Request, "name"),
                QueryValues.Text(http.Request, "platform"),
                ct);
            return DockerResults.Json(created, 201);
        });

        app.MapPost("/containers/prune", async (HttpContext http, ContainerManager containers, CancellationToken ct) =>
            DockerResults.Json(await containers.PruneAsync(QueryValues.Filters(http.Request), ct)));

        app.MapGet("/containers/{id}/json", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
            DockerResults.Json(await containers.InspectAsync(id, QueryValues.Bool(http.Request, "size"), ct)));

        app.MapGet("/containers/{id}/top", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
            DockerResults.Json(await containers.TopAsync(id, QueryValues.Text(http.Request, "ps_args"), ct)));

        app.MapGet("/containers/{id}/logs", LogsAsync);
        app.MapGet("/containers/{id}/stats", StatsAsync);

        app.MapPost("/containers/{id}/resize", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var rows = QueryValues.Int(http.Request, "h") ?? 0;
            var cols = QueryValues.Int(http.Request, "w") ?? 0;
            await containers.ResizeAsync(id, cols, rows, ct);
            return Results.Empty;
        });

        app.MapPost("/containers/{id}/start", async (string id, ContainerManager containers, CancellationToken ct) =>
        {
            await containers.StartAsync(id, ct);
            return Results.StatusCode(204);
        });

        app.MapPost("/containers/{id}/stop", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            await containers.StopAsync(id, QueryValues.Int(http.Request, "t"), QueryValues.Text(http.Request, "signal"), ct);
            return Results.StatusCode(204);
        });

        app.MapPost("/containers/{id}/restart", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            await containers.RestartAsync(id, QueryValues.Int(http.Request, "t"), ct);
            return Results.StatusCode(204);
        });

        app.MapPost("/containers/{id}/kill", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            await containers.KillAsync(id, QueryValues.Text(http.Request, "signal"), ct);
            return Results.StatusCode(204);
        });

        app.MapPost("/containers/{id}/update", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var request = await ReadBodyAsync<ContainerUpdateRequest>(http, ct) ?? new ContainerUpdateRequest();
            await containers.UpdateAsync(id, request, ct);
            return DockerResults.Json(new ContainerUpdateResponse());
        });

        app.MapPost("/containers/{id}/rename", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var name = QueryValues.Text(http.Request, "name")
                ?? throw DockerErrors.BadParameter("Neither Container nor Name are specified");
            await containers.RenameAsync(id, name, ct);
            return Results.StatusCode(204);
        });

        app.MapPost("/containers/{id}/pause", (string id) =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: pause is not supported by Apple container")));

        app.MapPost("/containers/{id}/unpause", (string id) =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: unpause is not supported by Apple container")));

        app.MapPost("/containers/{id}/attach", AttachAsync);

        app.MapGet("/containers/{id}/attach/ws", (string id) =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: websocket attach is not supported")));

        app.MapPost("/containers/{id}/wait", WaitAsync);

        app.MapDelete("/containers/{id}", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            await containers.RemoveAsync(id, QueryValues.Bool(http.Request, "force"), QueryValues.Bool(http.Request, "v"), ct);
            return Results.StatusCode(204);
        });

        app.MapMethods("/containers/{id}/archive", ["HEAD"], async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var path = RequiredPath(http);
            var stat = await containers.StatPathAsync(id, path, ct);
            http.Response.Headers["X-Docker-Container-Path-Stat"] =
                Convert.ToBase64String(DockerJson.SerializeToUtf8Bytes(stat));
            http.Response.ContentLength = 0;
        });

        app.MapGet("/containers/{id}/archive", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var path = RequiredPath(http);
            var stat = await containers.StatPathAsync(id, path, ct);
            http.Response.Headers["X-Docker-Container-Path-Stat"] =
                Convert.ToBase64String(DockerJson.SerializeToUtf8Bytes(stat));
            http.Response.StatusCode = 200;
            http.Response.ContentType = "application/x-tar";
            await http.Response.Body.FlushAsync(ct);
            await containers.GetArchiveAsync(id, path, http.Response.Body, ct);
        });

        app.MapPut("/containers/{id}/archive", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            var path = RequiredPath(http);
            await containers.PutArchiveAsync(id, path, http.Request.Body, QueryValues.Bool(http.Request, "noOverwriteDirNonDir"), ct);
            return Results.StatusCode(200);
        });

        app.MapGet("/containers/{id}/export", async (string id, HttpContext http, ContainerManager containers, CancellationToken ct) =>
        {
            http.Response.StatusCode = 200;
            http.Response.ContentType = "application/octet-stream";
            await http.Response.Body.FlushAsync(ct);
            await containers.ExportAsync(id, http.Response.Body, ct);
        });

        app.MapGet("/containers/{id}/changes", (string id) =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: container filesystem changes are not supported by Apple container")));

        app.MapPost("/containers/{id}/exec", async (string id, HttpContext http, ExecManager execs, CancellationToken ct) =>
        {
            var request = await ReadBodyAsync<ExecCreateRequest>(http, ct)
                ?? throw DockerErrors.BadParameter("No exec command specified");
            return DockerResults.Json(await execs.CreateAsync(id, request, ct), 201);
        });
    }

    private static async Task LogsAsync(string id, HttpContext http, ContainerManager containers)
    {
        var request = http.Request;
        var stdout = QueryValues.Bool(request, "stdout");
        var stderr = QueryValues.Bool(request, "stderr");
        if (!stdout && !stderr)
        {
            throw DockerErrors.BadParameter("Bad parameters: you must choose at least one stream");
        }

        var options = new LogReadOptions
        {
            Follow = QueryValues.Bool(request, "follow"),
            Stdout = stdout,
            Stderr = stderr,
            Tail = QueryValues.Tail(request),
            Since = ParseTime(QueryValues.Text(request, "since"), "since"),
            Until = ParseTime(QueryValues.Text(request, "until"), "until"),
        };

        var timestamps = QueryValues.Bool(request, "timestamps");
        var tty = containers.IsTty((await containers.ResolveAsync(id, http.RequestAborted)).Id);
        var ct = http.RequestAborted;

        await DockerResults.WriteStreamHeadersAsync(http.Response, tty, ct);

        try
        {
            await foreach (var entry in containers.LogsAsync(id, options, ct))
            {
                var payload = timestamps ? Prefix(entry) : entry.Data;
                await DockerResults.WriteChunkAsync(http.Response.Body, entry.Stream, payload, tty, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // `docker logs -f` ends by hanging up.
        }

        static ReadOnlyMemory<byte> Prefix(LogEntry entry)
        {
            var stamp = Encoding.UTF8.GetBytes(DockerTime.Format(entry.Time) + " ");
            var buffer = new byte[stamp.Length + entry.Data.Length];
            stamp.CopyTo(buffer, 0);
            entry.Data.CopyTo(buffer.AsMemory(stamp.Length));
            return buffer;
        }
    }

    private static async Task StatsAsync(string id, HttpContext http, ContainerManager containers)
    {
        var stream = QueryValues.Bool(http.Request, "stream", fallback: true) && !QueryValues.Bool(http.Request, "one-shot");
        var ct = http.RequestAborted;

        await using var writer = await DockerResults.BeginNdjsonAsync(http.Response, ct);

        try
        {
            while (true)
            {
                var sample = await containers.StatsAsync(id, ct);
                await writer.WriteAsync(sample, ct);

                if (!stream)
                {
                    return;
                }

                await Task.Delay(StatsInterval, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client stopped reading.
        }
    }

    private static async Task WaitAsync(string id, HttpContext http, ContainerManager containers)
    {
        var condition = QueryValues.Text(http.Request, "condition") ?? "not-running";
        var ct = http.RequestAborted;

        // The headers must reach the client before we block: `docker run` waits on this.
        http.Response.StatusCode = 200;
        http.Response.ContentType = "application/json";
        await http.Response.Body.FlushAsync(ct);

        var result = await containers.WaitAsync(id, condition, ct);
        await http.Response.Body.WriteAsync(DockerJson.SerializeToUtf8Bytes(result), ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static async Task AttachAsync(string id, HttpContext http, ContainerManager containers, ILoggerFactory loggerFactory)
    {
        var request = http.Request;
        var options = new AttachOptions
        {
            Stdin = QueryValues.Bool(request, "stdin"),
            Stdout = QueryValues.Bool(request, "stdout"),
            Stderr = QueryValues.Bool(request, "stderr"),
            Logs = QueryValues.Bool(request, "logs"),
            Stream = QueryValues.Bool(request, "stream", fallback: true),
            DetachKeys = QueryValues.Text(request, "detachKeys"),
        };

        if (!options.Stdout && !options.Stderr && !options.Stdin)
        {
            options = options with { Stdout = true, Stderr = true };
        }

        var logger = loggerFactory.CreateLogger("Cider.Daemon.Attach");
        var ct = http.RequestAborted;
        var attachment = await containers.AttachAsync(id, options, ct);

        try
        {
            var upgrade = http.Features.Get<IHttpUpgradeFeature>();
            if (upgrade is { IsUpgradableRequest: true })
            {
                http.Response.Headers["Content-Type"] = DockerResults.StreamContentType(attachment.Tty);
                http.Response.Headers["Upgrade"] = "tcp";
                var socket = await upgrade.UpgradeAsync();

                // The 101 head has to reach the client as its own socket write, or a buffered HTTP
                // reader swallows the first stream frames along with the headers.
                // Kestrel writes and flushes that head itself here, and a flush only queues the
                // bytes for the connection's send loop, so give that loop its turn before the first
                // frame is written behind the head. The hijack interceptor — which owns every real
                // `Upgrade: tcp` attach and is the reason this branch is only a fallback — makes the
                // boundary properly, by writing the head to the connection socket itself.
                await Task.Yield();
                await Task.Delay(1, CancellationToken.None);

                await using (socket)
                {
                    // Not RequestAborted: Kestrel ends the "request" as soon as the client
                    // half-closes its write side (which `docker run` without -i does right after
                    // sending the attach), while the hijacked stream must keep flowing until the
                    // container exits. A broken connection surfaces as a write failure instead.
                    await StdioPump.RunAsync(
                        options.Stdin ? socket : null,
                        socket,
                        attachment.Tty,
                        attachment.Output,
                        attachment.WriteStdinAsync,
                        attachment.CloseStdinAsync,
                        logger,
                        CancellationToken.None);
                }

                return;
            }

            // Legacy clients (no Upgrade headers) get a plain 200 + stream, output only.
            await DockerResults.WriteStreamHeadersAsync(http.Response, attachment.Tty, ct);
            await StdioPump.RunAsync(null, http.Response.Body, attachment.Tty, attachment.Output, null, null, logger, ct);
        }
        finally
        {
            await attachment.DisposeAsync();
        }
    }

    private static string RequiredPath(HttpContext http) =>
        QueryValues.Text(http.Request, "path")
        ?? throw DockerErrors.BadParameter("Bad parameters: not enough parameters (path)");

    private static DateTimeOffset? ParseTime(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value == "0")
        {
            return null;
        }

        if (!DockerTime.TryParse(value, out var parsed))
        {
            throw DockerErrors.BadParameter($"invalid value for {name}: {value}");
        }

        return parsed;
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken ct)
        where T : class
    {
        try
        {
            return await DockerJson.DeserializeAsync<T>(http.Request.Body, ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw DockerErrors.BadParameter($"cider: invalid JSON body: {ex.Message}");
        }
    }
}
