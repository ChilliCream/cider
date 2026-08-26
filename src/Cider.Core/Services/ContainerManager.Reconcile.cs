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

            // task cider-ede.7 fix direction §4: a record the runtime still reports Running survived
            // the restart with its real exit code recoverable — wait for it for real instead of
            // settling for "exit code unknown (daemon restarted)" the way ReconcileStatus's own
            // exited branch above has to. IsXpcTransport gates this because WaitContainerAsync always
            // answers null on the CLI transport (no such call exists there) — nothing to wait for.
            if (runtimeContainer.State == RuntimeContainerState.Running && _runtime.IsXpcTransport)
            {
                ReconcileWaitForExitAsync(record);
            }
        }

        // Containers created directly with the Apple CLI are surfaced read-only.
        var known = _store.GetAll().Select(r => r.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var runtimeContainer in runtimeContainers)
        {
            if (known.Contains(runtimeContainer.RuntimeId))
            {
                continue;
            }

            await AdoptContainerAsync(runtimeContainer, ct);
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
    internal async Task<ContainerRecord> AdoptContainerAsync(RuntimeContainer runtimeContainer, CancellationToken ct)
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
            ImageId = await ResolveAdoptedImageIdAsync(runtimeContainer, ct),
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

    /// <summary>
    /// cider-ede.29: <paramref name="runtimeContainer"/>.ImageDigest is the raw digest Apple's engine
    /// reports for an adopted container — the *index* digest on both Apple transports, not the
    /// content-addressed config digest that <see cref="RuntimeImage.Id"/> has been since cider-ger.19
    /// (and that a container created through <c>ContainerManager.CreateAsync</c> stores directly as
    /// <c>image.Id</c>). Docker's contract is that a container's <c>Image</c> IS the id
    /// <c>docker images</c> shows, so this resolves the engine digest to that id the same way
    /// <c>ImageManager</c>'s own <c>IsBoundTo</c> in-use guard matches a container to its image: by
    /// checking <see cref="RuntimeImage.IndexDigests"/> (falling back to a direct <see cref="RuntimeImage.Id"/>
    /// equality, in case the engine ever reports the config digest there too). When no image matches —
    /// deleted underneath a running container, or the store could not be listed — this keeps the raw
    /// engine digest rather than losing it to an empty string, and never lets the lookup fail adoption.
    /// </summary>
    private async Task<string> ResolveAdoptedImageIdAsync(RuntimeContainer runtimeContainer, CancellationToken ct)
    {
        var digest = runtimeContainer.ImageDigest;
        if (string.IsNullOrEmpty(digest))
        {
            return "";
        }

        IReadOnlyList<RuntimeImage> images;
        try
        {
            images = await _runtime.ListImagesAsync(ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "could not list images to resolve the adopted image id for {Container}; keeping the raw engine digest", runtimeContainer.RuntimeId);
            return digest;
        }

        var match = images.FirstOrDefault(image =>
            string.Equals(image.Id, digest, StringComparison.Ordinal) ||
            image.IndexDigests.Contains(digest, StringComparer.Ordinal));

        return match?.Id ?? digest;
    }

    /// <summary>
    /// task cider-ede.7 fix direction §4: for a record <see cref="ReconcileAsync"/> just found the
    /// runtime still reports <see cref="RuntimeContainerState.Running"/> for, waits for its real exit
    /// (<see cref="IContainerRuntime.WaitContainerAsync"/> — the XPC apiserver's own
    /// <c>containerWait</c>, which blocks even for a container this daemon did not itself bootstrap)
    /// and, once it lands, applies the same exit accounting <c>HandleExitAsync</c> applies for a
    /// container the daemon started itself: real exit code, "die" event, restart supervisor,
    /// auto-remove. Fired detached from <see cref="ReconcileAsync"/> on purpose — its own <c>ct</c> is
    /// startup-scoped and must not cancel a wait that can legitimately run for the rest of the
    /// container's life — so this uses <see cref="CancellationToken.None"/> throughout and re-reads
    /// the record from <see cref="_store"/> once the wait completes rather than trusting the snapshot
    /// captured at reconcile time: by then a user-issued stop/remove, or a restart-supervisor cycle
    /// that already replaced <see cref="ContainerRecord.RuntimeId"/>, may have already accounted for
    /// this container's exit through a different path, in which case this is a no-op.
    /// </summary>
    private void ReconcileWaitForExitAsync(ContainerRecord record) => _ = Task.Run(async () =>
    {
        (int ExitCode, DateTimeOffset ExitedAt)? result;
        try
        {
            result = await _runtime.WaitContainerAsync(record.RuntimeId, CancellationToken.None);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "post-restart wait for container {Container} failed", record.Id);
            return;
        }

        if (result is not { } exit)
        {
            // The transport could not wait, or the runtime says the process is already gone/never
            // started — leave whatever ReconcileStatus already recorded (running or "exit code
            // unknown") alone rather than guess.
            return;
        }

        var current = _store.Get(record.Id);
        if (current is null ||
            !string.Equals(current.RuntimeId, record.RuntimeId, StringComparison.Ordinal) ||
            !current.State.Running)
        {
            return;
        }

        current.State.Status = "exited";
        current.State.ExitCode = exit.ExitCode;
        current.State.FinishedAt = exit.ExitedAt;
        current.State.Error = null;
        current.State.Pid = 0;
        if (current.State.Health is { } health)
        {
            health.Status = "unhealthy";
        }

        Persist(current);

        // Same gap cider-ede.33 closed in StatePoller: this is a third observer of a container's
        // exit, no held process for HandleExitAsync to run off of either, and without this call it
        // would leave a pending `docker wait` blocked forever even though the record now correctly
        // says exited with the real exit code this path exists to recover.
        CompleteExitWait(current.Id, exit.ExitCode);

        _names.Unregister(current.Id);
        UnpublishPorts(current.Id);
        Publish(current, "die", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exitCode"] = exit.ExitCode.ToString(CultureInfo.InvariantCulture),
        });
        RaiseStateChanged(current, "die");

        if (current.AutoRemove)
        {
            try
            {
                await RemoveAsync(current.Id, force: true, removeVolumes: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is DockerApiException or RuntimeException)
            {
                _logger.LogDebug(ex, "auto-removing container {Container} failed", current.Id);
            }
        }
    }, CancellationToken.None);

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
    /// Launched detached, right after <see cref="StartAsync"/> has already returned to its caller
    /// (cider-ede.26 — it used to run inline, on <see cref="StartAsync"/>'s own return path): on
    /// Apple container 1.2.2, <c>container inspect</c> reports <c>status.networks: []</c> for the
    /// first ~1-2 s after start, so a single inspect (the old behaviour, before cider-ede.7) usually
    /// finds no address at all and the container's DNS name/IP never gets registered. This polls
    /// every <see cref="NetworkPollInterval"/> until the runtime reports the container
    /// <c>Running</c> and (when it has declared networks) at least one attachment has an address, up
    /// to <see cref="NetworkPollBudget"/> total, and gives up early if the held process has already
    /// exited.
    /// </summary>
    /// <remarks>
    /// Does not block <see cref="StartAsync"/>'s caller at all any more: Docker semantics say
    /// <c>start</c> returns once the process is running, not once every side effect has settled, and
    /// waiting on an address here used to cost every caller up to <see cref="StartReturnBudget"/> on
    /// top of the VM boot <see cref="StartAsync"/> already waits out (cider-ede.18's own criterion —
    /// <c>docker start</c> returns in &lt;= 200 ms on XPC, excluding VM boot — was neither implemented
    /// nor verified while this call sat on the return path). Once <c>Running</c> is confirmed it still
    /// gives itself only <see cref="StartReturnBudget"/> more before giving up on the address
    /// specifically, so a container whose address never resolves does not poll for the full
    /// <see cref="NetworkPollBudget"/>; either way, <see cref="RefreshNetworkInfoAsync"/> (called from
    /// <see cref="StatePoller"/>) finishes the job on a later tick if this gives up first.
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
    /// the runtime's reported attachments has a non-empty <c>IPv4Address</c>, or (cider-ede.27) the
    /// container has already exited by the time this runs — <see cref="HandleExitAsync"/> has already
    /// unregistered its DNS names, and this must not race it and re-register them for a container
    /// that is no longer running. Both callers (this file's own detached
    /// <see cref="AwaitStartupAndRegisterNetworkNamesAsync"/>, and <see cref="RefreshNetworkInfoAsync"/>
    /// via <see cref="StatePoller"/>) can legitimately still be mid-flight when the container exits;
    /// <see cref="StatePoller"/> already gates its call on <c>record.State.Running</c>, but the
    /// detached post-start continuation has no equivalent gate around this specific call, so it lives
    /// here instead of being duplicated at every call site.
    /// </summary>
    private bool ApplyNetworkInfo(ContainerRecord record, RuntimeContainer inspected)
    {
        if (!record.State.Running)
        {
            return true;
        }

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
