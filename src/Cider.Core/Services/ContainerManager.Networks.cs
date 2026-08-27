using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager : IContainerNetworkAttachments
{
    /// <summary>
    /// Container half of <c>POST /networks/{id}/connect</c>: adds <paramref name="dockerNetworkName"/>
    /// to a container that was created but never started, and re-creates it on the engine with the
    /// extended network list. Apple <c>container</c> fixes a container's networks at create time and
    /// the daemon runs <c>container create</c> already at Docker-create time, so the record update
    /// alone would not reach the engine. A container already attached to the network answers
    /// dockerd's 403 in every state; only a genuinely new attachment is gated on never-started.
    /// </summary>
    public async Task<ContainerRecord> AttachToNetworkAsync(
        string containerIdOrName,
        string dockerNetworkName,
        EndpointSettings? endpointConfig,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dockerNetworkName);

        var record = Resolve(containerIdOrName);
        var handle = GetHandle(record.Id);
        await handle.Gate.WaitAsync(ct);
        try
        {
            // Already attached wins over every state check (cider-qj4): dockerd answers this with
            // 403 "endpoint with name <container> already exists in network <network>" regardless of
            // the container's state (moby daemon/libnetwork/network.go createEndpoint:
            // `types.ForbiddenErrorf("endpoint with name %s already exists in network %s", ...)`,
            // where the endpoint name is the container name without its leading slash). Aspire's DCP
            // POSTs connect for a container it created through us on that very network and treats a
            // 403 as terminal — the 501 below looked transient and was retried every ~8s forever.
            if (record.Networks.ContainsKey(dockerNetworkName))
            {
                throw new DockerApiException(
                    HttpStatusCode.Forbidden,
                    $"endpoint with name {record.Name} already exists in network {dockerNetworkName}");
            }

            RequireNeverStarted(record, NetworkManager.ConnectNotSupported);

            EndpointIpam.Validate(
                dockerNetworkName,
                endpointConfig,
                await _networks.SubnetOfAsync(dockerNetworkName, ct).ConfigureAwait(false));

            var previous = SnapshotNetworks(record);

            // Appended, never prepended: the first network decides which DNS forwarder the container
            // is handed, and connecting a second network must not move a created container off it.
            List<string> networks = [.. previous.Keys, dockerNetworkName];
            record.Networks[dockerNetworkName] = BuildEndpoint(record, endpointConfig);

            await RecreateForNetworksAsync(record, networks, previous, ct);
            return record;
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    /// <summary>
    /// Container half of <c>POST /networks/{id}/disconnect</c>: the mirror image of
    /// <see cref="AttachToNetworkAsync"/>, likewise only for a container that was never started.
    /// </summary>
    public async Task<ContainerRecord> DetachFromNetworkAsync(
        string containerIdOrName,
        string dockerNetworkName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dockerNetworkName);

        var record = Resolve(containerIdOrName);
        var handle = GetHandle(record.Id);
        await handle.Gate.WaitAsync(ct);
        try
        {
            RequireNeverStarted(record, NetworkManager.DisconnectNotSupported);

            if (!record.Networks.ContainsKey(dockerNetworkName))
            {
                throw new DockerApiException(
                    HttpStatusCode.Forbidden,
                    $"container {record.Id} is not connected to network {dockerNetworkName}");
            }

            // `--network none` is now accepted at create time on the XPC transport (cider-ede.35);
            // disconnect still refuses to drop the last attachment because Apple fixes networks at
            // create time and the record must not claim a state the engine cannot re-create. A
            // disconnect down to zero would need a delete-and-recreate with an empty network list —
            // exactly the create-time path, not something this route can retrofit onto a running
            // container after the fact.
            if (record.Networks.Count == 1)
            {
                throw DockerErrors.NotImplemented(
                    $"cider: container {record.Name} cannot be disconnected from its only network " +
                    $"{dockerNetworkName}: Apple container always attaches a container to at least one network");
            }

            var previous = SnapshotNetworks(record);
            List<string> networks = [.. previous.Keys.Where(name => !string.Equals(name, dockerNetworkName, StringComparison.Ordinal))];
            record.Networks.Remove(dockerNetworkName);

            await RecreateForNetworksAsync(record, networks, previous, ct);
            return record;
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    /// <summary>
    /// Re-creates the engine container with <paramref name="networks"/>. On failure the record's
    /// endpoints are rolled back to <paramref name="previous"/> and the engine container is restored
    /// with the old network list, so a failed connect leaves the container exactly as it was.
    /// </summary>
    private async Task RecreateForNetworksAsync(
        ContainerRecord record,
        List<string> networks,
        Dictionary<string, EndpointSettings> previous,
        CancellationToken ct)
    {
        var spec = await BuildSpecForNetworksAsync(record, networks, ct);

        try
        {
            await _runtime.RemoveContainerAsync(record.RuntimeId, force: false, ct);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
        {
            // Already gone on the engine side (removed behind our back): the create below is
            // exactly the repair that needs.
        }
        catch (RuntimeException ex)
        {
            record.Networks = previous;
            throw Translate(ex);
        }

        // Past the point of no return: the engine container is gone, so the re-create runs with
        // `CancellationToken.None` (exactly as the restore below already does). Letting an
        // `OperationCanceledException` out of here would leave the record pointing at a container
        // that no longer exists on the engine — unstartable, and beyond repair from the API.
        try
        {
            await _runtime.CreateContainerAsync(spec, CancellationToken.None);
        }
        catch (RuntimeException ex)
        {
            record.Networks = previous;
            await TryRestoreAsync(record, CancellationToken.None);
            throw Translate(ex);
        }

        Persist(record);
    }

    /// <summary>Best-effort re-create with the rolled-back network list after a failed re-create.</summary>
    private async Task TryRestoreAsync(ContainerRecord record, CancellationToken ct)
    {
        try
        {
            var spec = await BuildSpecForNetworksAsync(record, [.. record.Networks.Keys], ct);
            await _runtime.CreateContainerAsync(spec, CancellationToken.None);
        }
        catch (Exception ex) when (ex is RuntimeException or OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "container {Container} could not be restored after a failed network change; it no longer exists on the engine",
                record.Id);
        }
    }

    private async Task<ContainerSpec> BuildSpecForNetworksAsync(
        ContainerRecord record,
        List<string> networks,
        CancellationToken ct)
    {
        var hostConfig = record.Request.HostConfig ?? new HostConfig();

        // Same call `CreateAsync` makes: it starts the DNS forwarder of the container's first
        // network when there is none yet and returns the servers the container is created with.
        var dnsServers = await ResolveDnsServersAsync(networks, hostConfig, ct);
        return BuildSpecFromRecord(record, networks, dnsServers);
    }

    /// <summary>Copy of the record's endpoints, used to roll back a failed network change.</summary>
    private static Dictionary<string, EndpointSettings> SnapshotNetworks(ContainerRecord record) =>
        new(record.Networks, StringComparer.Ordinal);

    /// <summary>
    /// Builds the endpoint the record stores for a newly connected network, in exactly the shape
    /// <c>CreateAsync</c> produces (the client's endpoint config, plus the compose service alias and
    /// the DNS names), so <c>inspect</c>/<c>ps</c> see no difference between a connected and a
    /// created attachment. The address fields stay empty until the container starts.
    /// </summary>
    private static EndpointSettings BuildEndpoint(ContainerRecord record, EndpointSettings? endpointConfig)
    {
        var settings = endpointConfig ?? new EndpointSettings();

        var aliases = new List<string>(settings.Aliases ?? []);
        if (record.Request.Labels.TryGetValue(ComposeServiceLabel, out var service) &&
            !aliases.Contains(service, StringComparer.Ordinal))
        {
            aliases.Add(service);
        }

        settings.Aliases = aliases.Count > 0 ? aliases : null;
        settings.DNSNames = [record.Name, string.IsNullOrEmpty(record.Request.Hostname) ? record.Name : record.Request.Hostname];
        return settings;
    }

    /// <summary>
    /// A container's networks can only be changed while it has never been started: the change is a
    /// re-create on the engine, which a running (or already run) container cannot survive.
    /// </summary>
    private static void RequireNeverStarted(ContainerRecord record, Func<string, string> notSupported)
    {
        if (!record.Managed)
        {
            throw DockerErrors.NotImplemented(
                $"cider: container {record.Name} was not created by cider, so its networks cannot be changed");
        }

        if (!string.Equals(record.State.Status, "created", StringComparison.Ordinal) || record.State.StartedAt is not null)
        {
            throw DockerErrors.NotImplemented(notSupported(record.State.Status));
        }
    }
}
