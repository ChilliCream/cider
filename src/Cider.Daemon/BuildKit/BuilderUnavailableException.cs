namespace Cider.Daemon.BuildKit;

/// <summary>
/// Thrown by <see cref="IBuilderConnection.GetAsync"/> when a usable link to buildkitd inside the
/// Apple builder VM cannot be established: BuildKit is turned off (<c>builder.enabled=false</c>), the
/// builder VM could not be started, or the post-dial liveness probe (<c>Control/Info</c>) never
/// answered.
/// <para>
/// Deliberately a plain <see cref="Exception"/>, not a <see cref="Cider.Core.DockerApi.DockerApiException"/>:
/// this type crosses the daemon's ordinary HTTP error path (<c>ErrorMiddleware</c>, which renders a
/// JSON body) as little as possible. A caller that surfaces it over a gRPC connection (the
/// <c>/grpc</c> forwarder target selector — outside this type's own scope) must map it to
/// <c>RpcException(StatusCode.Unavailable)</c> itself; a gRPC client cannot parse a JSON error body.
/// </para>
/// </summary>
public sealed class BuilderUnavailableException : Exception
{
    public BuilderUnavailableException(string message)
        : base(message)
    {
    }

    public BuilderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
