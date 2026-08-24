using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.Runtime;

namespace Cider.Core.Services;

/// <summary>Maps <see cref="RuntimeException.Kind"/> to the Docker HTTP status the managers must answer with.</summary>
internal static class RuntimeErrorTranslator
{
    public static DockerApiException ToDockerError(this RuntimeException ex) => ex.Kind switch
    {
        RuntimeErrorKind.NotFound => new DockerApiException(HttpStatusCode.NotFound, ex.Message, ex),
        RuntimeErrorKind.Conflict => new DockerApiException(HttpStatusCode.Conflict, ex.Message, ex),
        RuntimeErrorKind.InvalidArgument => new DockerApiException(HttpStatusCode.BadRequest, ex.Message, ex),
        RuntimeErrorKind.NotSupported => new DockerApiException(HttpStatusCode.NotImplemented, $"cider: {ex.Message}", ex),
        RuntimeErrorKind.Unavailable => new DockerApiException(HttpStatusCode.ServiceUnavailable, ex.Message, ex),
        // A runtime that stopped answering is a failed operation, not a retryable outage: dockerd
        // answers 500 here and clients render it as `Error response from daemon: …`.
        RuntimeErrorKind.Timeout => new DockerApiException(HttpStatusCode.InternalServerError, ex.Message, ex),
        _ => new DockerApiException(HttpStatusCode.InternalServerError, ex.Message, ex),
    };
}
