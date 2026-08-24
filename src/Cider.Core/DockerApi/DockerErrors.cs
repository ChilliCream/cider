using System.Net;

namespace Cider.Core.DockerApi;

/// <summary>Factory for <see cref="DockerApiException"/>s using Docker's exact wording.</summary>
public static class DockerErrors
{
    /// <summary>404 <c>No such container: &lt;id&gt;</c>.</summary>
    public static DockerApiException NoSuchContainer(string id) =>
        new(HttpStatusCode.NotFound, $"No such container: {id}");

    /// <summary>404 <c>No such image: &lt;ref&gt;</c>.</summary>
    public static DockerApiException NoSuchImage(string reference) =>
        new(HttpStatusCode.NotFound, $"No such image: {reference}");

    /// <summary>
    /// 404 <c>manifest for &lt;ref&gt; not found: manifest unknown</c> — dockerd's wording when a
    /// pull's registry lookup fails (unknown tag, or a registry that answers a whole repository as
    /// missing) before any progress has reached the client.
    /// </summary>
    public static DockerApiException ManifestUnknown(string reference) =>
        new(HttpStatusCode.NotFound, $"manifest for {reference} not found: manifest unknown");

    /// <summary>404 <c>network &lt;name&gt; not found</c>.</summary>
    public static DockerApiException NoSuchNetwork(string name) =>
        new(HttpStatusCode.NotFound, $"network {name} not found");

    /// <summary>404 <c>get &lt;name&gt;: no such volume</c>.</summary>
    public static DockerApiException NoSuchVolume(string name) =>
        new(HttpStatusCode.NotFound, $"get {name}: no such volume");

    /// <summary>404 <c>No such exec instance: &lt;id&gt;</c>.</summary>
    public static DockerApiException NoSuchExec(string id) =>
        new(HttpStatusCode.NotFound, $"No such exec instance: {id}");

    /// <summary>409 with a caller-supplied message.</summary>
    public static DockerApiException Conflict(string message) =>
        new(HttpStatusCode.Conflict, message);

    /// <summary>400 with a caller-supplied message.</summary>
    public static DockerApiException BadParameter(string message) =>
        new(HttpStatusCode.BadRequest, message);

    /// <summary>501 with a caller-supplied message.</summary>
    public static DockerApiException NotImplemented(string message) =>
        new(HttpStatusCode.NotImplemented, message);

    /// <summary>304 — start of a running container / stop of a stopped one. Carries no body.</summary>
    public static DockerApiException NotModified() =>
        new(HttpStatusCode.NotModified, "");

    /// <summary>500 with a caller-supplied message.</summary>
    public static DockerApiException Internal(string message) =>
        new(HttpStatusCode.InternalServerError, message);

    /// <summary>409 name clash on <c>POST /containers/create</c>, in Docker's exact wording.</summary>
    public static DockerApiException ContainerNameConflict(string name, string existingId) =>
        Conflict($"Conflict. The container name \"/{name.TrimStart('/')}\" is already in use by container \"{existingId}\". " +
                 "You have to remove (or rename) that container to be able to reuse that name.");

    /// <summary>409 removal of a running container without <c>force</c>, in Docker's exact wording.</summary>
    public static DockerApiException ContainerRunning(string nameOrId) =>
        Conflict($"cannot remove container \"{nameOrId}\": container is running: stop the container before removing or force remove");

    /// <summary>409 a paused container cannot be removed without force.</summary>
    public static DockerApiException ContainerPaused(string nameOrId) =>
        Conflict($"cannot remove container \"{nameOrId}\": container is paused and must be unpaused first");

    /// <summary>500 host port already taken, in Docker's wording.</summary>
    public static DockerApiException PortAlreadyAllocated(string hostIp, int port) =>
        Internal($"driver failed programming external connectivity on endpoint: Bind for {hostIp}:{port} failed: port is already allocated");

    /// <summary>404 with a caller-supplied message.</summary>
    public static DockerApiException NotFound(string message) =>
        new(HttpStatusCode.NotFound, message);

    /// <summary>
    /// 400 for a log driver dockerd does not know either — verbatim
    /// <c>moby/daemon/logger/factory.go</c>'s <c>"logger: no log driver named '%s' is registered"</c>,
    /// raised from <c>ValidateLogOpts</c> at container-create time.
    /// </summary>
    public static DockerApiException NoSuchLogDriver(string name) =>
        BadParameter($"logger: no log driver named '{name}' is registered");

    /// <summary>
    /// 404 for an unknown volume driver, composed from dockerd's three fragments:
    /// <c>volume/service/errors.go</c>'s <c>OpErr</c> (<c>"&lt;op&gt; &lt;name&gt;: &lt;err&gt;"</c>),
    /// <c>volume/drivers/extpoint.go</c>'s <c>"error looking up volume plugin "+name</c> and
    /// <c>plugin/errors.go</c>'s <c>plugin %q not found</c>. dockerd answers 404 here, not 400,
    /// because the plugin lookup is what fails.
    /// </summary>
    public static DockerApiException NoSuchVolumeDriver(string volumeName, string driver) =>
        NotFound($"create {volumeName}: error looking up volume plugin {driver}: plugin \"{driver}\" not found");

    /// <summary>
    /// 400 for a bad <c>IPAMConfig</c> on an endpoint. dockerd (v25+, which API 1.47 is) wraps the
    /// inner reason twice — <c>daemon/create.go</c>'s <c>"invalid config for network %s: %w"</c>
    /// around <c>daemon/container_operations.go</c>'s <c>"invalid endpoint settings:\n%w"</c>.
    /// </summary>
    public static DockerApiException InvalidEndpointSettings(string network, string reason) =>
        BadParameter($"invalid config for network {network}: invalid endpoint settings:\n{reason}");

    /// <summary>
    /// 400 <c>invalid filter '&lt;name&gt;'</c> — verbatim <c>moby/api/types/filters/errors.go</c>,
    /// what <c>filters.Args.Validate</c> returns for a key an endpoint does not accept.
    /// </summary>
    public static DockerApiException InvalidFilter(string name) =>
        BadParameter($"invalid filter '{name}'");

    /// <summary>
    /// 400 <c>invalid filter '&lt;name&gt;=[&lt;values&gt;]'</c> — <c>moby/daemon/internal/filters</c>'s
    /// <c>invalidFilter{Filter, Value}.Error()</c>, formatting the value slice with Go's default
    /// <c>%s</c> verb (space-separated, bracketed, no quoting). What
    /// <c>filters.Args.GetBoolOrDefault</c> returns for a boolean-typed filter — e.g. images prune's
    /// <c>dangling</c> — whose values are neither unambiguously true nor false.
    /// </summary>
    public static DockerApiException InvalidFilterValue(string name, IReadOnlyList<string> values) =>
        BadParameter($"invalid filter '{name}=[{string.Join(' ', values)}]'");
}
