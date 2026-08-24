using System.Net;
using System.Text.Json;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Images;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>
/// Docker Engine API image endpoints (owner: daemon-resources). Image references may contain
/// '/' and ':' (e.g. <c>docker.io/library/alpine:latest</c>), which ASP.NET Core route parameters
/// cannot capture except via a catch-all — so everything under <c>/images/{name...}</c> is a single
/// catch-all route (<see cref="MapImageRoutes"/>) that dispatches on the trailing path segment.
/// </summary>
public static class ImageRoutes
{
    public static void MapImageRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/images/json", async (HttpContext context, ImageManager images, CancellationToken ct) =>
        {
            var request = context.Request;
            var all = QueryBool(request, "all");
            var digests = QueryBool(request, "digests");
            var filters = Filters.Parse(request.Query["filters"].ToString());

            // The pre-1.25 singular `filter=<name>`, still sent by docker-py and old clients; it is
            // a repository-name match applied on top of `filters=`.
            var nameFilter = NullIfEmpty(request.Query["filter"].ToString());

            // `shared-size=true` is accepted and has no effect: SharedSize needs per-layer byte
            // sizes, which Apple's `container` never reports, so the -1 "not computed" sentinel is
            // the honest answer. See ImageSummary.SharedSize.
            var list = await images.ListAsync(all, filters, digests, ct, nameFilter).ConfigureAwait(false);
            foreach (var summary in list)
            {
                ApplyVirtualSize(context, summary);
            }

            return DockerResults.Json(list);
        });

        app.MapGet("/images/search", () =>
            DockerResults.Error(DockerErrors.NotImplemented("cider: image search is not supported by Apple container")));

        app.MapGet("/images/get", async (HttpRequest request, HttpResponse response, ImageManager images, CancellationToken ct) =>
        {
            var names = request.Query["names"].Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            if (names.Count == 0)
            {
                throw DockerErrors.BadParameter("names cannot be empty");
            }

            response.ContentType = "application/x-tar";
            response.StatusCode = StatusCodes.Status200OK;
            await images.SaveAsync(names, response.Body, ct).ConfigureAwait(false);
            return Results.Empty;
        });

        app.MapPost("/images/create", async (HttpRequest request, HttpResponse response, ImageManager images, CancellationToken ct) =>
        {
            var fromImage = NullIfEmpty(request.Query["fromImage"].ToString());
            var fromSrc = NullIfEmpty(request.Query["fromSrc"].ToString());
            var tag = NullIfEmpty(request.Query["tag"].ToString());
            var platform = NullIfEmpty(request.Query["platform"].ToString());

            if (fromImage is null)
            {
                if (fromSrc is null)
                {
                    throw DockerErrors.BadParameter("fromImage or fromSrc must be provided");
                }

                // `docker import -` streams the rootfs tar as the request body; any other value is a
                // URL the daemon would have to fetch itself, which cider deliberately does not.
                if (!string.Equals(fromSrc, "-", StringComparison.Ordinal))
                {
                    throw DockerErrors.NotImplemented(
                        $"cider: importing an image from a URL is not supported (fromSrc={fromSrc}); pipe the tarball to `docker import -` instead");
                }

                var importRepo = NullIfEmpty(request.Query["repo"].ToString());
                var message = NullIfEmpty(request.Query["message"].ToString());
                var changes = ImageChanges.Split(request.Query["changes"].AsEnumerable());
                // Lazy start for the same reason as the pull branch below: an unsupported `changes`
                // directive is a 400 raised before any progress line exists.
                var importWriter = await DockerResults.BeginNdjsonAsync(response, ct, deferStart: true).ConfigureAwait(false);
                var importProgress = DockerResults.ProgressTo(importWriter);
                return await RunNdjsonAsync(
                    response,
                    importWriter,
                    () => images.ImportAsync(request.Body, importRepo, tag, message, changes, importProgress, ct),
                    ct).ConfigureAwait(false);
            }

            var auth = TryParseRegistryAuth(request);
            // Started lazily: a missing manifest/tag is discovered by ImageManager.PullAsync before
            // it ever reports progress, so nothing has reached the client yet and RunNdjsonAsync
            // below can still answer with a normal 404 instead of a 200 that dies mid-stream.
            var writer = await DockerResults.BeginNdjsonAsync(response, ct, deferStart: true).ConfigureAwait(false);
            var progress = DockerResults.ProgressTo(writer);
            return await RunNdjsonAsync(
                response,
                writer,
                () => images.PullAsync(fromImage, tag, platform, auth, progress, ct),
                ct).ConfigureAwait(false);
        });

        app.MapPost("/images/load", async (HttpRequest request, HttpResponse response, ImageManager images, CancellationToken ct) =>
        {
            var writer = await DockerResults.BeginNdjsonAsync(response, ct).ConfigureAwait(false);
            var progress = DockerResults.ProgressTo(writer);
            try
            {
                await images.LoadAsync(request.Body, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await TryWriteErrorAsync(writer, Message(ex), ct).ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            return Results.Empty;
        });

        // `docker commit`. Not under /images despite producing one — Docker's own route is top-level.
        // Apple `container` has no commit primitive, so ImageManager emulates it by exporting the
        // container's root filesystem into a fresh one-layer OCI image (see ImageManager.CommitAsync).
        app.MapPost("/commit", async (HttpRequest request, ContainerManager containers, ImageManager images, CancellationToken ct) =>
        {
            var container = NullIfEmpty(request.Query["container"].ToString())
                ?? throw DockerErrors.BadParameter("Bad parameter: \"container\" cannot be empty");
            var repo = NullIfEmpty(request.Query["repo"].ToString());
            var tag = NullIfEmpty(request.Query["tag"].ToString());
            var comment = NullIfEmpty(request.Query["comment"].ToString());
            var author = NullIfEmpty(request.Query["author"].ToString());
            var changes = ImageChanges.Split(request.Query["changes"].AsEnumerable());

            var record = await containers.ResolveAsync(container, ct).ConfigureAwait(false);
            var id = await images.CommitAsync(record, repo, tag, comment, author, changes, ct).ConfigureAwait(false);
            return DockerResults.Json(new IdResponse { Id = id }, StatusCodes.Status201Created);
        });

        app.MapPost("/images/prune", async (HttpRequest request, ImageManager images, CancellationToken ct) =>
        {
            var filters = Filters.Parse(request.Query["filters"].ToString());
            var response = await images.PruneAsync(filters, ct).ConfigureAwait(false);
            return DockerResults.Json(response);
        });

        // Everything else lives under `/images/{name}` where `name` may itself contain '/' and ':'
        // (repo path + tag) — dispatch by HTTP method and trailing path segment, the same ambiguity
        // Docker's own gorilla/mux routes carry (`/images/{name:.*}/json` etc).
        app.MapMethods("/images/{**rest}", ["GET", "POST", "DELETE"], async (HttpContext context, string rest, ImageManager images, CancellationToken ct) =>
        {
            var request = context.Request;
            var response = context.Response;
            var method = request.Method;

            if (HttpMethods.IsGet(method) && TryStripSuffix(rest, "json", out var inspectName))
            {
                var detail = await images.InspectAsync(inspectName, ct).ConfigureAwait(false);
                ApplyVirtualSize(context, detail);
                return DockerResults.Json(detail);
            }

            if (HttpMethods.IsGet(method) && TryStripSuffix(rest, "history", out var historyName))
            {
                var items = await images.HistoryAsync(historyName, ct).ConfigureAwait(false);
                return DockerResults.Json(items);
            }

            if (HttpMethods.IsGet(method) && TryStripSuffix(rest, "get", out var getName))
            {
                response.ContentType = "application/x-tar";
                response.StatusCode = StatusCodes.Status200OK;
                await images.SaveAsync([getName], response.Body, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            if (HttpMethods.IsPost(method) && TryStripSuffix(rest, "push", out var pushName))
            {
                var tag = NullIfEmpty(request.Query["tag"].ToString());
                var auth = TryParseRegistryAuth(request);
                // Same lazy-start reasoning as /images/create: a missing source image is discovered
                // by ImageManager.PushAsync before it reports anything.
                var writer = await DockerResults.BeginNdjsonAsync(response, ct, deferStart: true).ConfigureAwait(false);
                var progress = DockerResults.ProgressTo(writer);
                return await RunNdjsonAsync(
                    response,
                    writer,
                    () => images.PushAsync(pushName, tag, auth, progress, ct),
                    ct).ConfigureAwait(false);
            }

            if (HttpMethods.IsPost(method) && TryStripSuffix(rest, "tag", out var tagName))
            {
                var repo = request.Query["repo"].ToString();
                var tag = NullIfEmpty(request.Query["tag"].ToString());
                if (string.IsNullOrEmpty(repo))
                {
                    throw DockerErrors.BadParameter("repo cannot be empty");
                }

                await images.TagAsync(tagName, repo, tag, ct).ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status201Created);
            }

            if (HttpMethods.IsDelete(method))
            {
                var force = QueryBool(request, "force");
                var noPrune = QueryBool(request, "noprune");
                var items = await images.RemoveAsync(rest, force, noPrune, ct).ConfigureAwait(false);
                return DockerResults.Json(items);
            }

            throw new DockerApiException(HttpStatusCode.NotFound, "page not found");
        });
    }

    // ---- helpers ------------------------------------------------------

    /// <summary>
    /// dockerd dropped <c>VirtualSize</c> — the deprecated alias of <c>Size</c> — from the image
    /// list and inspect responses in API 1.44, and still emits it below that. Both routes therefore
    /// gate on the same rule, reading the version <see cref="VersionPrefixMiddleware"/> stashes in
    /// <c>HttpContext.Items</c> before it strips the <c>/v1.xx</c> prefix; an unversioned request
    /// counts as the newest API and so omits the field. The managers leave the member null, which
    /// the DTO's <c>WhenWritingNull</c> turns into an omitted key rather than <c>"VirtualSize":null</c>.
    /// </summary>
    private static void ApplyVirtualSize(HttpContext context, ImageSummary summary) =>
        summary.VirtualSize = VersionPrefixMiddleware.IsAtLeast(context, 1, 44) ? null : summary.Size;

    /// <inheritdoc cref="ApplyVirtualSize(HttpContext, ImageSummary)"/>
    private static void ApplyVirtualSize(HttpContext context, ImageInspectResponse detail) =>
        detail.VirtualSize = VersionPrefixMiddleware.IsAtLeast(context, 1, 44) ? null : detail.Size;

    /// <summary>Strips a trailing <c>/{suffix}</c> segment; <c>false</c> when <paramref name="rest"/> has no name before it.</summary>
    private static bool TryStripSuffix(string rest, string suffix, out string name)
    {
        var marker = "/" + suffix;
        if (rest.Length > marker.Length && rest.EndsWith(marker, StringComparison.Ordinal))
        {
            name = rest[..^marker.Length];
            return true;
        }

        name = "";
        return false;
    }

    /// <summary>
    /// Runs an NDJSON progress operation started via <see cref="DockerResults.BeginNdjsonAsync"/> with
    /// <c>deferStart: true</c>. A <see cref="DockerApiException"/> raised before <paramref name="writer"/>
    /// ever wrote anything (so <c>response.HasStarted</c> is still false) becomes a normal Docker error
    /// response; any other failure — including one raised once progress has already streamed — is
    /// reported in-line, matching real dockerd only degrading to an in-stream error once the response
    /// is already committed to 200.
    /// </summary>
    private static async Task<IResult> RunNdjsonAsync(HttpResponse response, NdjsonWriter writer, Func<Task> operation, CancellationToken ct)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DockerApiException ex) when (!response.HasStarted)
        {
            return DockerResults.Error(ex);
        }
        catch (Exception ex)
        {
            // The manager already reports most errors through the progress stream before rethrowing
            // once it has started; this is a last-resort net so the stream never ends silently on an
            // unexpected failure.
            await TryWriteErrorAsync(writer, Message(ex), ct).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }

        return Results.Empty;
    }

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

    private static string Message(Exception ex) => ex is DockerApiException dex ? dex.Message : ex.Message;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool QueryBool(HttpRequest request, string key)
    {
        var value = request.Query[key].ToString();
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes the <c>X-Registry-Auth</c> header: base64url (or plain base64) JSON <see cref="AuthConfig"/>.</summary>
    private static RegistryAuth? TryParseRegistryAuth(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("X-Registry-Auth", out var values))
        {
            return null;
        }

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var json = DecodeBase64(raw);
            var auth = DockerJson.Deserialize<AuthConfig>(json);
            if (auth is null)
            {
                return null;
            }

            return new RegistryAuth
            {
                Username = auth.Username,
                Password = auth.Password,
                ServerAddress = auth.ServerAddress,
                IdentityToken = auth.IdentityToken,
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string DecodeBase64(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder != 0)
        {
            padded += new string('=', 4 - remainder);
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
