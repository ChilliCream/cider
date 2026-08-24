using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;

namespace Cider.Daemon.Hosting;

/// <summary>
/// The result helpers every route group uses so that the wire format stays exactly Docker's:
/// PascalCase JSON with <see cref="DockerJson.Options"/>, NDJSON progress streams flushed per line
/// and the raw/multiplexed stdio content types.
/// </summary>
public static class DockerResults
{
    /// <summary>Content type of a stdio stream for a container/exec that runs on a pty.</summary>
    public const string RawStreamContentType = "application/vnd.docker.raw-stream";

    /// <summary>Content type of a stdcopy-framed stdio stream (no pty).</summary>
    public const string MultiplexedStreamContentType = "application/vnd.docker.multiplexed-stream";

    /// <summary>The content type Docker uses for a stdio stream of the given tty-ness.</summary>
    public static string StreamContentType(bool tty) => tty ? RawStreamContentType : MultiplexedStreamContentType;

    /// <summary>A JSON body serialized with the Docker wire options.</summary>
    public static IResult Json<T>(T value, int statusCode = 200) =>
        Results.Json(value, DockerJson.TypeInfo<T>(), "application/json", statusCode);

    /// <summary>Docker's error envelope for a <see cref="DockerApiException"/>.</summary>
    public static IResult Error(DockerApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.Status == System.Net.HttpStatusCode.NotModified)
        {
            return Results.StatusCode(304);
        }

        return Json(new ErrorResponse { Message = ex.Message }, ex.StatusCode);
    }

    /// <summary>Writes a JSON body straight to the response (for handlers that stream afterwards).</summary>
    public static async Task WriteJsonAsync<T>(HttpResponse response, T value, int statusCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var payload = DockerJson.SerializeToUtf8Bytes(value);
        response.ContentLength = payload.Length;
        await response.Body.WriteAsync(payload, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Starts an NDJSON stream: 200 + <c>application/json</c>. By default the headers are flushed
    /// immediately (every caller — <c>/events</c>, container stats,
    /// <c>/images/load</c>, <c>/build</c>) so a slow first line (waiting on the next stats sample,
    /// the next event) doesn't leave the client wondering whether the request was even accepted.
    /// </summary>
    /// <param name="deferStart">
    /// When true, the headers are left unsent until the returned <see cref="NdjsonWriter"/>'s first
    /// actual write — Kestrel starts the response on the first <c>Body</c> write/flush either way,
    /// this only skips the *eager* flush that would otherwise start it before that. That lets a
    /// handler which throws before writing anything answer with a normal Docker error response
    /// instead of a 200 that immediately dies mid-stream: real dockerd only degrades a failure to an
    /// in-stream error once progress has already reached the client (used by <c>/images/create</c>
    /// and <c>/images/{name}/push</c> — docs/apple-container-notes.md §2 "Errors").
    /// </param>
    public static async Task<NdjsonWriter> BeginNdjsonAsync(HttpResponse response, CancellationToken ct, bool deferStart = false)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode = 200;
        response.ContentType = "application/json";
        // Do NOT set the `Transfer-Encoding` header by hand: Kestrel already chunk-encodes any
        // HTTP/1.1 response body that has no Content-Length, and it does so by writing the real
        // "<hex-length>\r\n...\r\n" chunk framing around every `Body.WriteAsync` call. If the app
        // sets `Transfer-Encoding: chunked` itself, Kestrel takes that as "the app is producing
        // pre-framed chunk bytes" and stops framing — but NdjsonWriter writes plain JSON lines, so
        // clients (docker CLI, HttpClient, curl) fail to parse the body as chunked and abort with
        // "Received chunk header length could not be parsed". Leaving the header unset lets Kestrel
        // frame the stream correctly whether it starts here or lazily on the writer's first write.
        if (!deferStart)
        {
            await response.Body.FlushAsync(ct);
        }

        return new NdjsonWriter(response.Body);
    }

    /// <summary>Adapts an <see cref="NdjsonWriter"/> to <see cref="IProgress{T}"/>, serializing writes.</summary>
    public static IProgress<JsonMessage> ProgressTo(NdjsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new NdjsonProgress(writer);
    }

    /// <summary>Writes the 200 + stdio content type header block of a non-hijacked stream and flushes it.</summary>
    public static async Task WriteStreamHeadersAsync(HttpResponse response, bool tty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode = 200;
        response.ContentType = StreamContentType(tty);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>Writes one output chunk to a stdio stream: raw for a pty, stdcopy-framed otherwise.</summary>
    public static async Task WriteChunkAsync(Stream destination, StdStream stream, ReadOnlyMemory<byte> data, bool tty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (data.IsEmpty)
        {
            return;
        }

        if (tty)
        {
            await destination.WriteAsync(data, ct);
        }
        else
        {
            var frame = new byte[MultiplexedFrame.HeaderSize + data.Length];
            MultiplexedFrame.WriteHeader(frame, stream, data.Length);
            data.CopyTo(frame.AsMemory(MultiplexedFrame.HeaderSize));
            await destination.WriteAsync(frame, ct);
        }

        await destination.FlushAsync(ct);
    }

    private sealed class NdjsonProgress(NdjsonWriter writer) : IProgress<JsonMessage>
    {
        private readonly Lock _gate = new();

        public void Report(JsonMessage value)
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    writer.WriteAsync(value).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The client hung up mid-stream; the operation itself keeps running.
                }
            }
        }
    }
}
