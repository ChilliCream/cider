using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Cider.Daemon.Tunnel;
using Grpc.Core;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Copies a streaming HTTP/2 gRPC request straight through to an <see cref="HttpMessageInvoker"/>
/// and streams the response (headers, body, trailers) back -- the generic forwarder every BuildKit
/// method that needs no inspection rides on (see cider-ger.7). Per-method typed stubs are the wrong
/// tool here: the CLI advertises its method list dynamically, and YARP has no net10 target and would
/// bloat the AOT binary.
/// </summary>
public static class GrpcForwarder
{
    private const string GrpcContentTypePrefix = "application/grpc";
    private const string Http2Protocol = "HTTP/2";
    private const int DownstreamChunkSize = 32 * 1024;
    private const string CiderMessagePrefix = "cider: ";

    private static readonly HashSet<string> ExcludedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "transfer-encoding", "keep-alive", "upgrade", "content-type", "content-length",
    };

    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer-encoding", "connection", "content-length",
    };

    // The gRPC trailer fields: when a call fails before any body is written, grpc-dotnet's Kestrel
    // response collapses into a single HEADERS frame ("Trailers-Only" in the gRPC wire spec), so
    // these can show up on the upstream response's regular Headers rather than its TrailingHeaders.
    // Either way they belong on OUR response's trailers, never mixed into its regular headers.
    private static readonly string[] TrailerFieldNames = ["grpc-status", "grpc-message", "grpc-status-details-bin"];

    /// <summary>
    /// Maps a fallback endpoint under <paramref name="endpoints"/> that forwards any
    /// <c>/{service}/{method}</c> call nothing else has claimed to whatever <paramref name="targetSelector"/>
    /// resolves for it, gated to tunnel connections of <paramref name="kind"/>. Runs at
    /// <c>MapFallback</c>'s default (lowest) priority, so an explicit typed endpoint mapped at the
    /// ordinary Order 0 (a real <c>MapGrpcService&lt;T&gt;</c>) always wins for the methods it knows;
    /// only a call to a method nothing else claimed ever reaches here. A <see langword="null"/>
    /// target answers <c>grpc-status 12</c> (Unimplemented), matching a real gRPC server's response
    /// to an unknown method.
    /// </summary>
    public static IEndpointConventionBuilder MapGrpcForwarder(
        this IEndpointRouteBuilder endpoints,
        TunnelKind kind,
        Func<HttpContext, ValueTask<ForwardTarget?>> targetSelector)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(targetSelector);

        var log = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Cider.Daemon.BuildKit.GrpcForwarder");

        return endpoints.MapFallback("/{service}/{method}", (RequestDelegate)(async http =>
        {
            var target = await targetSelector(http).ConfigureAwait(false);
            if (target is null)
            {
                WriteTrailersOnly(http.Response, StatusCode.Unimplemented, CiderMessagePrefix + "not available on this tunnel");
                return;
            }

            await ForwardAsync(http, target, log).ConfigureAwait(false);
        })).RequireTunnel(kind);
    }

    /// <summary>
    /// Forwards <paramref name="http"/> to <paramref name="target"/> and streams the response back.
    /// A non-HTTP/2 or non-gRPC request never reaches the target: it gets <c>grpc-status 12</c>
    /// directly. Any exception raised while talking to the target -- a transport failure, a
    /// cancellation, a bug -- is caught here: if nothing has been sent to the client yet it becomes
    /// <c>grpc-status 14</c> (Unavailable, for a transport-shaped failure) or <c>13</c> (Internal,
    /// otherwise) with a <c>cider:</c>-prefixed message; if the response was already committed the
    /// connection is aborted instead, since a gRPC client cannot be handed a second, conflicting
    /// status after the first has started streaming. Either way <see cref="ForwardTarget.OnFailure"/>
    /// runs first, so a caller (T5) can invalidate whatever link just failed.
    /// </summary>
    public static async Task ForwardAsync(HttpContext http, ForwardTarget target, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);

        if (!string.Equals(http.Request.Protocol, Http2Protocol, StringComparison.Ordinal) || !IsGrpcRequest(http.Request))
        {
            WriteTrailersOnly(http.Response, StatusCode.Unimplemented, CiderMessagePrefix + "expected an HTTP/2 gRPC request");
            return;
        }

        var method = http.Request.Path.Value ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        var status = StatusCode.OK;
        var committed = false;

        try
        {
            using var request = BuildRequest(http, target);

            // See ForwardTarget.HeaderRewrite's doc comment: queued before SendAsync so the target's
            // duplex stream can substitute the HEADERS frame SendAsync is about to write, then
            // released the moment SendAsync returns (or throws) -- never held past that, so it only
            // ever blocks whichever other forward on the same connection is also mid-SendAsync, not
            // this one's body/response.
            var headerScope = target.HeaderRewrite is not null
                ? await target.HeaderRewrite(BuildLiteralFields(http, target), http.RequestAborted).ConfigureAwait(false)
                : null;
            HttpResponseMessage response;
            try
            {
                // HttpMessageInvoker.SendAsync (unlike HttpClient.SendAsync) has no buffering
                // HttpCompletionOption: it hands the request straight to the handler, which already
                // returns as soon as response headers arrive.
                response = await target.Invoker
                    .SendAsync(request, http.RequestAborted)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (headerScope is not null)
                {
                    await headerScope.DisposeAsync().ConfigureAwait(false);
                }
            }

            using (response)
            {
                http.Response.StatusCode = (int)response.StatusCode;
                CopyResponseHeaders(http.Response, response);
                http.Response.DeclareTrailer("grpc-status");
                committed = true;

                await CopyResponseBodyAsync(response.Content, http.Response.Body, http.RequestAborted).ConfigureAwait(false);

                status = CopyTrailers(http.Response, response);
                await http.Response.CompleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            target.OnFailure?.Invoke(ex);
            status = ex is HttpRequestException or IOException ? StatusCode.Unavailable : StatusCode.Internal;

            if (!committed)
            {
                WriteTrailersOnly(http.Response, status, CiderMessagePrefix + ex.Message);
            }
            else
            {
                http.Abort();
            }
        }
        finally
        {
            log.LogDebug("forwarded {Method} -> {Status} in {ElapsedMs}ms", method, status, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static bool IsGrpcRequest(HttpRequest request) =>
        request.ContentType?.StartsWith(GrpcContentTypePrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static HttpRequestMessage BuildRequest(HttpContext http, ForwardTarget target)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://" + target.Authority + http.Request.Path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        foreach (var header in http.Request.Headers)
        {
            if (header.Key.StartsWith(':') || ExcludedRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            // A genuinely repeated header (e.g. FileSync/DiffCopy's followpaths) gets comma-joined
            // into one wire line right here by System.Net.Http.Headers.HttpHeaders regardless of how
            // it is added -- harmless for a target with ForwardTarget.HeaderRewrite set (its own
            // literal-encoded substitute is what actually reaches the wire; see BuildLiteralFields and
            // ForwardAsync), but the one real defect this causes for a target without it.
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var content = new DuplexRequestContent(http.Request.Body, target.MaxUpstreamChunk, target.Pacer);
        if (!string.IsNullOrEmpty(http.Request.ContentType))
        {
            content.Headers.TryAddWithoutValidation("Content-Type", http.Request.ContentType);
        }

        request.Content = content;
        return request;
    }

    /// <summary>
    /// The exact <c>(name, value)</c> pairs <see cref="BuildRequest"/>'s <see cref="HttpRequestMessage"/>
    /// is meant to carry on the wire -- pseudo-headers first (RFC 7540 §8.1.2.1), in the order
    /// <see cref="BuildRequest"/> itself would have implied, then every regular header with its
    /// original multiplicity preserved exactly as <see cref="HttpContext.Request"/> received it, name
    /// lower-cased (RFC 7540 §8.1.2: HTTP/2 field names MUST be lowercase -- ASP.NET Core's
    /// <see cref="IHeaderDictionary"/> re-cases a header it recognizes, e.g. buildkitd's own lowercase
    /// <c>te</c>/<c>user-agent</c> come back as <c>TE</c>/<c>User-Agent</c> here; <c>HttpRequestMessage.Headers</c>
    /// normally lower-cases for the wire on its own, which is why <see cref="BuildRequest"/> never
    /// needed this, but this method feeds a hand-rolled HPACK encoder that does not). Used only for a
    /// <see cref="ForwardTarget.HeaderRewrite"/> substitution -- see its doc comment.
    /// </summary>
    private static List<(string Name, string Value)> BuildLiteralFields(HttpContext http, ForwardTarget target)
    {
        var fields = new List<(string Name, string Value)>
        {
            (":method", "POST"),
            (":scheme", "http"),
            (":authority", target.Authority),
            (":path", http.Request.Path.Value ?? string.Empty),
        };

        if (!string.IsNullOrEmpty(http.Request.ContentType))
        {
            fields.Add(("content-type", http.Request.ContentType));
        }

        foreach (var header in http.Request.Headers)
        {
            if (header.Key.StartsWith(':') || ExcludedRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            var name = header.Key.ToLowerInvariant();
            foreach (var value in header.Value)
            {
                if (value is not null)
                {
                    fields.Add((name, value));
                }
            }
        }

        return fields;
    }

    private static void CopyResponseHeaders(HttpResponse response, HttpResponseMessage upstream)
    {
        foreach (var header in upstream.Headers)
        {
            if (TrailerFieldNames.Any(name => string.Equals(name, header.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // Trailers-only: belongs on our trailers, copied by CopyTrailers instead.
            }

            response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (ExcludedResponseHeaders.Contains(header.Key))
            {
                continue;
            }

            response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static async Task CopyResponseBodyAsync(HttpContent content, Stream destination, CancellationToken cancellationToken)
    {
        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(DownstreamChunkSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, DownstreamChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies every trailer the upstream call actually produced -- whichever of
    /// <see cref="HttpResponseMessage.TrailingHeaders"/> (the common case: a second HEADERS frame
    /// after the body) and the trailer fields on <see cref="HttpResponseMessage.Headers"/>
    /// (Trailers-Only: no body was ever sent) apply -- and returns the parsed <c>grpc-status</c>,
    /// defaulting to <see cref="StatusCode.OK"/> if the upstream never sent one.
    /// </summary>
    private static StatusCode CopyTrailers(HttpResponse response, HttpResponseMessage upstream)
    {
        var status = StatusCode.OK;

        foreach (var name in TrailerFieldNames)
        {
            if (upstream.Headers.TryGetValues(name, out var values))
            {
                status = AppendTrailer(response, name, values.ToArray(), status);
            }
        }

        foreach (var header in upstream.TrailingHeaders)
        {
            status = AppendTrailer(response, header.Key, header.Value.ToArray(), status);
        }

        return status;
    }

    private static StatusCode AppendTrailer(HttpResponse response, string name, string[] values, StatusCode status)
    {
        response.AppendTrailer(name, values);

        if (values.Length > 0 &&
            string.Equals(name, "grpc-status", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return (StatusCode)code;
        }

        return status;
    }

    /// <summary>
    /// Writes a gRPC trailers-only response before anything else has been sent: status 200,
    /// <c>content-type: application/grpc</c>, <c>grpc-status</c>/<c>grpc-message</c> set directly on
    /// the (not-yet-started) response headers -- never a JSON body, which a gRPC client cannot parse.
    /// </summary>
    private static void WriteTrailersOnly(HttpResponse response, StatusCode status, string message)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = GrpcContentTypePrefix;
        response.Headers["grpc-status"] = ((int)status).ToString(CultureInfo.InvariantCulture);
        response.Headers["grpc-message"] = message;
    }
}
