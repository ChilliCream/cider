using System.Net;

namespace Cider.Core.DockerApi;

/// <summary>An error that maps 1:1 to a Docker Engine API response (<c>{"message": "..."}</c> plus a status code).</summary>
public sealed class DockerApiException : Exception
{
    public DockerApiException(HttpStatusCode status, string message)
        : base(message)
    {
        Status = status;
    }

    public DockerApiException(HttpStatusCode status, string message, Exception? innerException)
        : base(message, innerException)
    {
        Status = status;
    }

    /// <summary>HTTP status to answer with.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Numeric status code, for convenience in minimal-API handlers.</summary>
    public int StatusCode => (int)Status;
}
