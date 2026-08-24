using System.Diagnostics;
using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;

namespace Cider.Daemon.Hosting;

/// <summary>
/// Turns every exception a route can throw into Docker's error envelope (<c>{"message": "..."}</c>)
/// with the right status code, logs the request at Debug and answers unmatched paths the way
/// dockerd does (<c>404 {"message":"page not found"}</c>).
/// </summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    /// <summary>Runs the rest of the pipeline and renders whatever comes back out of it.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);

            if (context.Response is { HasStarted: false, StatusCode: 404 or 405 } response && response.ContentLength is null or 0)
            {
                await WriteAsync(context, 404, "page not found");
            }
        }
        catch (DockerApiException ex)
        {
            await WriteErrorAsync(context, ex);
        }
        catch (RuntimeException ex)
        {
            await WriteErrorAsync(context, Translate(ex));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-request; nothing to report.
        }
        catch (BadHttpRequestException ex)
        {
            logger.LogDebug(ex, "malformed request {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, ex.StatusCode, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "unhandled error in {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, 500, $"cider: {ex.Message}");
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                logger.LogDebug(
                    "{Method} {Path}{Query} -> {Status} ({Elapsed:F1} ms)",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Request.QueryString.Value,
                    context.Response.StatusCode,
                    elapsed.TotalMilliseconds);
            }
        }
    }

    /// <summary>Maps a runtime error class to the Docker status code the API promises.</summary>
    public static DockerApiException Translate(RuntimeException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex.Kind switch
        {
            RuntimeErrorKind.NotFound => new DockerApiException(HttpStatusCode.NotFound, ex.Message, ex),
            RuntimeErrorKind.Conflict => new DockerApiException(HttpStatusCode.Conflict, ex.Message, ex),
            RuntimeErrorKind.InvalidArgument => new DockerApiException(HttpStatusCode.BadRequest, ex.Message, ex),
            RuntimeErrorKind.NotSupported => new DockerApiException(HttpStatusCode.NotImplemented, $"cider: {ex.Message}", ex),
            RuntimeErrorKind.Unavailable => new DockerApiException(HttpStatusCode.ServiceUnavailable, ex.Message, ex),
            // A runtime that stopped answering is a failed operation, not a retryable outage.
            RuntimeErrorKind.Timeout => new DockerApiException(HttpStatusCode.InternalServerError, ex.Message, ex),
            _ => new DockerApiException(HttpStatusCode.InternalServerError, ex.Message, ex),
        };
    }

    private async Task WriteErrorAsync(HttpContext context, DockerApiException ex)
    {
        if (ex.Status == HttpStatusCode.NotModified)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 304;
            }

            return;
        }

        if (ex.StatusCode >= 500)
        {
            logger.LogWarning(ex, "{Method} {Path} failed", context.Request.Method, context.Request.Path);
        }

        await WriteAsync(context, ex.StatusCode, ex.Message);
    }

    private async Task WriteAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            logger.LogDebug("cannot report '{Message}': the response has already started", message);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = DockerJson.SerializeToUtf8Bytes(new ErrorResponse { Message = message });
        context.Response.ContentLength = payload.Length;

        try
        {
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }
}
