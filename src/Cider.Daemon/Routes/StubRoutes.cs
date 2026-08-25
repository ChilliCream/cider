using System.Net;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Daemon.Hosting;

namespace Cider.Daemon.Routes;

/// <summary>
/// Docker Engine API surface that Apple <c>container</c> has no equivalent for: swarm and everything
/// under it, BuildKit's session/grpc endpoints, and the distribution inspector (owner: daemon-resources).
/// Every route answers Docker-shaped errors so clients fail with a clear message instead of a raw 404.
/// </summary>
public static class StubRoutes
{
    private static readonly string[] AllMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];
    private static readonly string[] MutatingMethods = ["POST", "PUT", "PATCH", "DELETE", "HEAD"];

    // dockerd's exact wording (daemon/cluster/errors.go `errNoSwarm`) for "there is no cluster at
    // all" — the answer to leaving a swarm that was never joined.
    private const string NotPartOfSwarm = "This node is not part of a swarm";

    // dockerd's exact wording (daemon/cluster/cluster.go `errNoManager`, the `st.swarmNode == nil`
    // branch) for every manager-gated call on a node that never joined a swarm.
    private const string NotSwarmManager =
        "This node is not a swarm manager. Use \"docker swarm init\" or \"docker swarm join\" to connect this node to swarm and try again.";

    public static void MapStubRoutes(this IEndpointRouteBuilder app)
    {
        // `docker info`/`docker system` probe swarm state with a plain GET first — answer the way a
        // non-swarm daemon does: `Cluster.Inspect` goes through the same manager gate as every other
        // read below, so it gets the same 406 + manager message.
        app.MapGet("/swarm", () => DockerError(HttpStatusCode.NotAcceptable, NotSwarmManager));

        // On a real non-swarm node these mutating /swarm verbs don't 404/501 — dockerd's cluster
        // backend runs and returns real errors, which docker-py's swarm-teardown helpers (used in
        // every test class that touches networks/services/nodes/secrets/configs) tolerate by exact
        // status code. `leave` is the special case: `Cluster.Leave` short-circuits before the
        // manager gate with `errNoSwarm`, so it answers "not part of a swarm", not "not a manager".
        // `update`/`unlockkey`/`unlock` all run through the same manager gate as the GET above.
        app.MapPost("/swarm/leave", () => DockerError(HttpStatusCode.NotAcceptable, NotPartOfSwarm));
        app.MapPost("/swarm/update", () => DockerError(HttpStatusCode.ServiceUnavailable, NotSwarmManager));
        app.MapGet("/swarm/unlockkey", () => DockerError(HttpStatusCode.ServiceUnavailable, NotSwarmManager));
        app.MapPost("/swarm/unlock", () => DockerError(HttpStatusCode.ServiceUnavailable, NotSwarmManager));

        // `init`/`join` are different in kind: on a real non-swarm node they *succeed* (that's how a
        // swarm gets created), so there's no "not part of a swarm" error to mirror. This daemon does
        // not implement swarm mode at all (non-goal), so they — and anything else under /swarm —
        // fall into the generic "not supported" 501 sweep.
        MapUnsupportedTree(app, "/swarm", "cider: swarm mode is not supported by Apple container", MutatingMethods);

        // Services/tasks/nodes/secrets/configs sit behind the exact same manager gate as
        // `/swarm/update` in dockerd (`Cluster.GetServices`, `GetNodes`, `GetSecrets`, `GetConfigs`
        // all call the same `lockedManagerAction`), so a non-swarm node answers every verb here with
        // the same 503 + manager message — not a blanket 501.
        MapDockerErrorTree(app, "/services", HttpStatusCode.ServiceUnavailable, NotSwarmManager, AllMethods);
        MapDockerErrorTree(app, "/tasks", HttpStatusCode.ServiceUnavailable, NotSwarmManager, AllMethods);
        MapDockerErrorTree(app, "/nodes", HttpStatusCode.ServiceUnavailable, NotSwarmManager, AllMethods);
        MapDockerErrorTree(app, "/secrets", HttpStatusCode.ServiceUnavailable, NotSwarmManager, AllMethods);
        MapDockerErrorTree(app, "/configs", HttpStatusCode.ServiceUnavailable, NotSwarmManager, AllMethods);
        MapUnsupportedTree(app, "/plugins", "cider: plugins are not supported by Apple container", AllMethods);

        app.MapGet("/distribution/{**name}", (string name) =>
            Unsupported("cider: registry distribution inspection is not supported"));

        // BuildKit's gRPC-over-HTTP2 session. A real upgrade request (Connection: Upgrade,
        // Upgrade: h2c) never reaches this Kestrel stub at all — DaemonHost's hijack middleware
        // answers it (or 404s when BuildKit is disabled) before Kestrel's own routing runs; see
        // MapFallback in DaemonHost. What lands here is only a plain, non-upgrade POST, from a
        // client that either doesn't speak the h2c upgrade or hit the daemon with BuildKit off.
        // When BuildKit is enabled, tell such a client what it's missing instead of a bare 404;
        // when disabled, `Builder-Version: 1` on /_ping already steered it to legacy /build, so a
        // plain 404 (as if the route didn't exist) is the right answer.
        app.MapPost("/session", (CiderOptions options) => SessionOrGrpcResult(options));
        app.MapPost("/grpc", (CiderOptions options) => SessionOrGrpcResult(options));
    }

    private static IResult SessionOrGrpcResult(CiderOptions options) => options.BuildKitEnabled
        ? DockerResults.Error(new DockerApiException(
            HttpStatusCode.BadRequest, "cider: /grpc and /session require Connection: Upgrade, Upgrade: h2c"))
        : DockerResults.Error(new DockerApiException(HttpStatusCode.NotFound, "page not found"));

    // ---- helpers ------------------------------------------------------

    private static void MapUnsupportedTree(IEndpointRouteBuilder app, string prefix, string message, string[] exactMethods)
    {
        app.MapMethods(prefix, exactMethods, () => Unsupported(message));
        app.MapMethods(prefix + "/{**rest}", AllMethods, (string rest) => Unsupported(message));
    }

    private static void MapDockerErrorTree(
        IEndpointRouteBuilder app, string prefix, HttpStatusCode status, string message, string[] exactMethods)
    {
        app.MapMethods(prefix, exactMethods, () => DockerError(status, message));
        app.MapMethods(prefix + "/{**rest}", AllMethods, (string rest) => DockerError(status, message));
    }

    private static IResult Unsupported(string message) => throw DockerErrors.NotImplemented(message);

    private static IResult DockerError(HttpStatusCode status, string message) =>
        DockerResults.Error(new DockerApiException(status, message));
}
