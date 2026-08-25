using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    public async Task<ContainerPruneResponse> PruneAsync(Filters filters, CancellationToken ct)
    {
        // dockerd's containersAcceptedFilters (moby/daemon/prune.go). An unknown key used to be
        // ignored, so a mistyped guard pruned everything it was written to protect.
        filters = (filters ?? Filters.Empty).Validate("label", "label!", "until");

        // Resolved once, up front, the way dockerd's getUntilFromPruneFilters is called before the
        // container loop — an unparseable value used to be swallowed inside the loop and treated as
        // "nothing to exclude", pruning every stopped container instead of rejecting the request.
        var until = filters.ResolveUntil();
        var response = new ContainerPruneResponse();

        foreach (var record in _store.GetAll())
        {
            if (record.State.Running || !record.Managed)
            {
                continue;
            }

            if (!filters.MatchesLabels(record.Request.Labels))
            {
                continue;
            }

            if (until is not null && record.Created > until)
            {
                continue;
            }

            // Captured before RemoveAsync, which deletes the capture file as part of removal.
            var logBytes = _logs.SizeOnDisk(record.Id);

            try
            {
                await RemoveAsync(record.Id, force: false, removeVolumes: false, ct);
                response.ContainersDeleted.Add(record.Id);

                // Apple's runtime (`container system df`) reports only an aggregate containers
                // total, not a per-container size, so there is no honest figure for the
                // container's writable layer here. The log capture file is the one piece of disk
                // usage this daemon can attribute to a specific container with certainty, so it
                // is used as a defensible (if partial/lower-bound) SpaceReclaimed — better than
                // the fabricated-looking always-0 this replaces, but still an undercount for any
                // container with on-disk writes outside its log file.
                response.SpaceReclaimed += logBytes;
            }
            catch (DockerApiException ex)
            {
                _logger.LogDebug(ex, "pruning container {Container} failed", record.Id);
            }
        }

        return response;
    }

    /// <summary>Startup reconciliation: match persisted records against what the engine actually has.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            await _networks.EnsureDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is RuntimeException or DockerApiException)
        {
            _logger.LogWarning(ex, "could not ensure the default network record");
        }

        IReadOnlyList<RuntimeContainer> runtimeContainers;
        try
        {
            runtimeContainers = await _runtime.ListContainersAsync(ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogWarning(ex, "could not list engine containers during reconciliation");
            return;
        }

        // Hidden system containers (the DNS forwarders, Apple's builder VM) are never Docker
        // containers. Kept unfiltered too, so a record already adopted for one of them (upgrade
        // from before this filter existed) can be told apart from one whose runtime container is
        // genuinely gone.
        var byRawRuntimeId = runtimeContainers.ToDictionary(c => c.RuntimeId, StringComparer.Ordinal);
        runtimeContainers = [.. runtimeContainers.Where(container => !IsSystemContainer(container))];
        var byRuntimeId = runtimeContainers.ToDictionary(c => c.RuntimeId, StringComparer.Ordinal);

        foreach (var record in _store.GetAll())
        {
            GetHandle(record.Id);

            if (!byRuntimeId.TryGetValue(record.RuntimeId, out var runtimeContainer))
            {
                // An older daemon adopted this before system containers were filtered (e.g. Apple's
                // builder VM). The engine still has it, but it now classifies as system, so the
                // stale record is dropped outright rather than marked exited.
                if (!record.Managed &&
                    byRawRuntimeId.TryGetValue(record.RuntimeId, out var stillPresent) &&
                    IsSystemContainer(stillPresent))
                {
                    _store.Delete(record.Id);
                    _handles.TryRemove(record.Id, out _);
                    _logger.LogInformation("dropping adopted system container record {Name}", record.Name);
                    continue;
                }

                if (record.State.Running)
                {
                    record.State.Status = "exited";
                    record.State.FinishedAt ??= DateTimeOffset.UtcNow;
                    record.State.Error = "exit code unknown (daemon restarted)";
                    Persist(record);
                }

                continue;
            }

            ReconcileStatus(record, runtimeContainer);
        }

        // Containers created directly with the Apple CLI are surfaced read-only.
        var known = _store.GetAll().Select(r => r.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var runtimeContainer in runtimeContainers)
        {
            if (known.Contains(runtimeContainer.RuntimeId))
            {
                continue;
            }

            AdoptContainer(runtimeContainer);
        }

        // In proxy mode the host-side listeners live in this process and died with the last one, so
        // everything still running has to be published again (its address may have changed too).
        if (ProxyPublishing)
        {
            foreach (var record in _store.GetAll())
            {
                if (record.State.Running && record.Ports.Count > 0)
                {
                    await RefreshNetworkInfoAsync(record, ct);
                }
            }
        }
    }

    /// <summary>
    /// Corrects one present record's status against what the runtime reports for it, exactly the way
    /// the per-container loop in <see cref="ReconcileAsync"/> always has. Shared with
    /// <see cref="StateSynchronizer"/> so an on-demand resync applies the identical rule. Returns
    /// <c>true</c> when the status actually changed (and was persisted).
    /// </summary>
    internal bool ReconcileStatus(ContainerRecord record, RuntimeContainer runtimeContainer)
    {
        var expected = runtimeContainer.State switch
        {
            RuntimeContainerState.Running => "running",
            RuntimeContainerState.Created => "created",
            _ => "exited",
        };

        if (string.Equals(record.State.Status, expected, StringComparison.Ordinal))
        {
            return false;
        }

        record.State.Status = expected;
        if (expected == "exited")
        {
            record.State.FinishedAt ??= DateTimeOffset.UtcNow;
            record.State.Error = "exit code unknown (daemon restarted)";
        }

        Persist(record);
        return true;
    }

    /// <summary>
    /// Surfaces one container the Apple CLI created directly as a read-only record, exactly the way
    /// the adoption loop in <see cref="ReconcileAsync"/> always has. Shared with
    /// <see cref="StateSynchronizer"/>. The caller is expected to have already checked that no
    /// existing record claims <paramref name="runtimeContainer"/>'s <see cref="RuntimeContainer.RuntimeId"/>.
    /// </summary>
    internal ContainerRecord AdoptContainer(RuntimeContainer runtimeContainer)
    {
        var dockerId = ContainerIdentity.ReadDockerId(runtimeContainer.Labels) ?? DockerId.New();
        var name = ContainerIdentity.ReadDockerName(runtimeContainer.Labels) ?? runtimeContainer.RuntimeId;
        var argv = runtimeContainer.Argv;
        var record = new ContainerRecord
        {
            Id = dockerId,
            Name = name,
            RuntimeId = runtimeContainer.RuntimeId,
            Created = runtimeContainer.CreatedAt ?? DateTimeOffset.UtcNow,
            Request = new ContainerCreateRequest
            {
                Image = runtimeContainer.ImageReference,
                Tty = runtimeContainer.Tty,
                WorkingDir = runtimeContainer.WorkingDir ?? "",
                Env = [.. runtimeContainer.Env],
                Cmd = [.. argv],
                Labels = new Dictionary<string, string>(runtimeContainer.Labels, StringComparer.Ordinal),
            },
            ImageRef = runtimeContainer.ImageReference,
            ImageId = runtimeContainer.ImageDigest ?? "",
            Path = argv.Count > 0 ? argv[0] : "",
            Args = argv.Count > 1 ? [.. argv.Skip(1)] : [],
            Managed = ContainerIdentity.ReadDockerId(runtimeContainer.Labels) is not null,
            State = new ContainerState
            {
                Status = runtimeContainer.State == RuntimeContainerState.Running ? "running" : "exited",
                StartedAt = runtimeContainer.StartedAt,
            },
            LogPath = _logs.PathFor(dockerId),
        };

        Persist(record);
        GetHandle(dockerId);
        return record;
    }

    /// <summary>Called by <see cref="Restart.RestartSupervisor"/> right before it restarts a container.</summary>
    internal void MarkRestarting(ContainerRecord record)
    {
        record.State.Status = "restarting";
        record.RestartCount++;
        Persist(record);
    }

    private async Task RemoveAnonymousVolumesAsync(ContainerRecord record, CancellationToken ct)
    {
        foreach (var volume in record.AnonymousVolumes)
        {
            try
            {
                await _volumes.RemoveAsync(volume, force: false, ct);
            }
            catch (Exception ex) when (ex is DockerApiException or RuntimeException)
            {
                _logger.LogDebug(ex, "removing anonymous volume {Volume} failed", volume);
            }
        }
    }

    /// <summary>
    /// Polled right after <see cref="StartAsync"/> starts the runtime process: on Apple container
    /// 1.2.2, <c>container inspect</c> reports <c>status.networks: []</c> for the first ~1-2 s after
    /// start, so a single inspect (the old behaviour) usually finds no address at all and the
    /// container's DNS name/IP never gets registered. This polls every <see cref="NetworkPollInterval"/>
    /// until the runtime reports the container <c>Running</c> and (when it has declared networks) at
    /// least one attachment has an address, up to <see cref="NetworkPollBudget"/> total, and gives up
    /// early if the held process has already exited.
    /// </summary>
    /// <remarks>
    /// Only blocks the caller (<see cref="StartAsync"/>) up to <see cref="StartReturnBudget"/> once
    /// <c>Running</c> is confirmed: Docker semantics say <c>start</c> returns once the process is
    /// running, not once every side effect has settled. If the address is still missing after that,
    /// this returns anyway and <see cref="RefreshNetworkInfoAsync"/> (called from
    /// <see cref="StatePoller"/>) finishes the job on a later tick.
    /// </remarks>
    private async Task AwaitStartupAndRegisterNetworkNamesAsync(ContainerRecord record, IContainerProcess process, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var hardDeadline = start + NetworkPollBudget;
        var returnDeadline = start + StartReturnBudget;
        var runningConfirmed = false;

        while (true)
        {
            if (process.Exited.IsCompleted)
            {
                return;
            }

            RuntimeContainer? inspected;
            try
            {
                inspected = await _runtime.InspectContainerAsync(record.RuntimeId, ct);
            }
            catch (RuntimeException ex)
            {
                _logger.LogDebug(ex, "inspect after start failed for container {Container}", record.Id);
                return;
            }

            if (inspected is { State: RuntimeContainerState.Running })
            {
                runningConfirmed = true;
                if (ApplyNetworkInfo(record, inspected))
                {
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= hardDeadline || (runningConfirmed && now >= returnDeadline) || process.Exited.IsCompleted)
            {
                return;
            }

            try
            {
                await Task.Delay(NetworkPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Belt-and-braces refresh used by <see cref="Services.StatePoller"/>: re-inspects a running
    /// container whose record still shows no address (the initial poll in <see cref="StartAsync"/>
    /// gave up after <see cref="StartReturnBudget"/>) and fills it in once the runtime reports one.
    /// </summary>
    public async Task<bool> RefreshNetworkInfoAsync(ContainerRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        RuntimeContainer? inspected;
        try
        {
            inspected = await _runtime.InspectContainerAsync(record.RuntimeId, ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "network refresh inspect failed for container {Container}", record.Id);
            return false;
        }

        if (inspected is null)
        {
            return false;
        }

        var resolved = ApplyNetworkInfo(record, inspected);
        await EnsurePublishedPortsAsync(record, ct);
        return resolved;
    }

    /// <summary>
    /// Copies IP/gateway/MAC/network id from an inspect result into the record and registers the
    /// container's DNS names for every attachment that already has an address. Returns <c>true</c>
    /// once the record has nothing left to wait for: either it declares no networks, or every one of
    /// the runtime's reported attachments has a non-empty <c>IPv4Address</c>.
    /// </summary>
    private bool ApplyNetworkInfo(ContainerRecord record, RuntimeContainer inspected)
    {
        if (record.Networks.Count == 0)
        {
            return true;
        }

        if (inspected.Networks.Count == 0)
        {
            // Apple has not populated status.networks yet; nothing to do but keep polling/retrying.
            return false;
        }

        var allResolved = true;
        var changed = false;

        foreach (var attachment in inspected.Networks)
        {
            var dockerNetwork = record.Networks.Keys
                .FirstOrDefault(name => string.Equals(_networks.RuntimeNameFor(name), attachment.Network, StringComparison.Ordinal))
                ?? attachment.Network;

            var endpoint = record.Networks.TryGetValue(dockerNetwork, out var existing) ? existing : new EndpointSettings();

            if (string.IsNullOrEmpty(attachment.IPv4Address))
            {
                allResolved = false;
                record.Networks[dockerNetwork] = endpoint;
                continue;
            }

            endpoint.IPAddress = attachment.IPv4Address;
            endpoint.Gateway = attachment.IPv4Gateway ?? "";
            endpoint.MacAddress = attachment.MacAddress;

            // Apple's attachment carries no per-attachment IPv6 gateway (docs/spikes/xpc/
            // 02-apiserver-xpc-protocol.md §2.2's Attachment has no ipv6Gateway field), so
            // Ipv6Gateway stays whatever RuntimeMapper set (currently always null/empty).
            if (!string.IsNullOrEmpty(attachment.Ipv6Address))
            {
                endpoint.GlobalIPv6Address = attachment.Ipv6Address;
                endpoint.IPv6Gateway = attachment.Ipv6Gateway ?? "";
            }

            if (_networks.TryGetCachedRecord(dockerNetwork) is { } networkRecord)
            {
                endpoint.NetworkID = networkRecord.Id;
                var configs = networkRecord.Request.IPAM?.Config;
                var subnet = configs?.FirstOrDefault(c => !IsIpv6Subnet(c.Subnet))?.Subnet;
                if (TryParsePrefixLength(subnet, out var prefixLen))
                {
                    endpoint.IPPrefixLen = prefixLen;
                }

                var subnetV6 = configs?.FirstOrDefault(c => IsIpv6Subnet(c.Subnet))?.Subnet;
                if (TryParsePrefixLength(subnetV6, out var prefixLenV6))
                {
                    endpoint.GlobalIPv6PrefixLen = prefixLenV6;
                }
            }

            if (string.IsNullOrEmpty(endpoint.EndpointID))
            {
                endpoint.EndpointID = DeterministicEndpointId(record.Id, dockerNetwork);
            }

            record.Networks[dockerNetwork] = endpoint;
            changed = true;

            if (IPAddress.TryParse(attachment.IPv4Address, out var ip))
            {
                var names = new List<string> { record.Name };
                if (!string.IsNullOrEmpty(record.Request.Hostname))
                {
                    names.Add(record.Request.Hostname);
                }

                names.AddRange(endpoint.Aliases ?? []);
                if (record.Request.Labels.TryGetValue(ComposeServiceLabel, out var service))
                {
                    names.Add(service);
                }

                _names.Register(dockerNetwork, record.Id, names, ip);
            }
        }

        if (changed)
        {
            Persist(record);
        }

        return allResolved;
    }

    /// <summary>One <see cref="Ipam"/> config entry list mixes IPv4 and IPv6 subnets; this is how they're told apart.</summary>
    private static bool IsIpv6Subnet(string? subnet) => subnet is not null && subnet.Contains(':', StringComparison.Ordinal);

    private static bool TryParsePrefixLength(string? cidr, out int prefixLength)
    {
        prefixLength = 0;
        if (string.IsNullOrEmpty(cidr))
        {
            return false;
        }

        var slash = cidr.IndexOf('/');
        return slash >= 0 && int.TryParse(
            cidr.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out prefixLength);
    }

    private static string DeterministicEndpointId(string containerId, string dockerNetwork) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"cider-endpoint:{containerId}:{dockerNetwork}")));
}
