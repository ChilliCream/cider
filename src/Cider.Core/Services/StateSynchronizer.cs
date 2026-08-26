using Cider.Core.DockerApi;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>
/// One-shot, idempotent resync of the daemon's persisted state against what Apple <c>container</c>
/// actually has: containers, networks, volumes and DNS forwarders. Built for the moments cider is
/// wedged — a resource removed by hand with the Apple CLI leaves a record <c>docker … ls</c> keeps
/// showing and later creates fail against; one created directly is invisible to docker until
/// restart. Safe to call while the daemon serves traffic (it takes the same per-resource gates the
/// underlying manager helpers take) and safe to call twice in a row: the second pass reports an empty
/// <see cref="SyncReport"/>. Never calls <c>container delete</c>/<c>stop</c> — it only ever touches
/// cider's own records and cider-owned side processes (DNS forwarders, port proxies).
/// </summary>
public sealed class StateSynchronizer
{
    private readonly IContainerRuntime _runtime;
    private readonly ContainerManager _containers;
    private readonly NetworkManager _networks;
    private readonly VolumeManager _volumes;
    private readonly IDnsForwarderService _dnsForwarder;
    private readonly ILogger<StateSynchronizer> _logger;

    /// <summary>Creates the synchronizer.</summary>
    public StateSynchronizer(
        IContainerRuntime runtime,
        ContainerManager containers,
        NetworkManager networks,
        VolumeManager volumes,
        IDnsForwarderService dnsForwarder,
        ILogger<StateSynchronizer> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _networks = networks ?? throw new ArgumentNullException(nameof(networks));
        _volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        _dnsForwarder = dnsForwarder ?? throw new ArgumentNullException(nameof(dnsForwarder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one full resync pass. Throws <see cref="RuntimeException"/> — never a partial, silently
    /// "successful" report — when the engine cannot be listed; the caller decides how to report that.
    /// </summary>
    public async Task<SyncReport> SyncAsync(CancellationToken ct)
    {
        var report = new SyncReport();

        try
        {
            await SyncContainersAsync(report, ct).ConfigureAwait(false);
            await _networks.ReconcileAsync(report, ct).ConfigureAwait(false);
            await _volumes.ReconcileAsync(report, ct).ConfigureAwait(false);
            await SyncDnsForwardersAsync(report, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            _logger.LogWarning(ex, "state sync could not reach the Apple container engine; aborting the pass");
            throw;
        }

        _logger.LogInformation(
            "state sync complete: containers +{ContainersAdopted}/-{ContainersRemoved}/~{ContainersUpdated}, "
            + "networks +{NetworksAdopted}/-{NetworksRemoved}, volumes +{VolumesAdopted}/-{VolumesRemoved}, "
            + "dns +{DnsEnsured}/-{DnsStopped}, {Warnings} warning(s)",
            report.Containers.Adopted.Count,
            report.Containers.Removed.Count,
            report.Containers.Updated.Count,
            report.Networks.Adopted.Count,
            report.Networks.Removed.Count,
            report.Volumes.Adopted.Count,
            report.Volumes.Removed.Count,
            report.Dns.Adopted.Count,
            report.Dns.Removed.Count,
            report.Warnings.Count);

        return report;
    }

    private async Task SyncContainersAsync(SyncReport report, CancellationToken ct)
    {
        // Nothing below this call may mutate anything: a failure here must leave every record
        // untouched (Rules: never a partial silent success).
        var runtimeContainers = await _runtime.ListContainersAsync(ct).ConfigureAwait(false);

        // The daemon's own hidden containers (the DNS forwarders) are never Docker containers.
        runtimeContainers = [.. runtimeContainers.Where(container => !ContainerManager.IsSystemContainer(container))];
        var byRuntimeId = runtimeContainers.ToDictionary(c => c.RuntimeId, StringComparer.Ordinal);

        // 1. Drop vanished records. Unlike the state poller's consecutive-miss guard, this is an
        // explicit, user-requested resync, so the drop happens on the spot.
        foreach (var record in _containers.AllRecords().ToList())
        {
            if (byRuntimeId.ContainsKey(record.RuntimeId))
            {
                continue;
            }

            // The daemon is holding this container's init process (a `container start -a` it
            // launched), so the runtime not listing it yet is a transient gap, not a removal —
            // mirrors StatePoller.IsHeldByUs.
            if (_containers.HasHeldProcess(record.Id))
            {
                continue;
            }

            try
            {
                if (await _containers.ForgetVanishedAsync(record, ct).ConfigureAwait(false))
                {
                    report.Containers.Removed.Add(record.Name);
                }
            }
            catch (Exception ex) when (ex is DockerApiException or RuntimeException)
            {
                report.Warnings.Add($"could not drop vanished container {record.Name}: {ex.Message}");
            }
        }

        // 2. Fix statuses for the ones still present.
        foreach (var record in _containers.AllRecords())
        {
            if (byRuntimeId.TryGetValue(record.RuntimeId, out var runtimeContainer) &&
                _containers.ReconcileStatus(record, runtimeContainer))
            {
                report.Containers.Updated.Add(record.Name);
            }
        }

        // 3. Adopt containers the Apple CLI created directly.
        var known = _containers.AllRecords().Select(r => r.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var runtimeContainer in runtimeContainers)
        {
            if (known.Contains(runtimeContainer.RuntimeId))
            {
                continue;
            }

            var adopted = await _containers.AdoptContainerAsync(runtimeContainer, ct).ConfigureAwait(false);
            report.Containers.Adopted.Add(adopted.Name);
        }

        // 4. Refresh network info (which also republishes ports) for running containers, publish
        // ports on their own for one with bindings but nothing to refresh, and unpublish for
        // stopped ones. Cheap and idempotent when everything is already up to date.
        foreach (var record in _containers.AllRecords())
        {
            if (!record.State.Running)
            {
                _containers.UnpublishPorts(record.Id);
                continue;
            }

            try
            {
                if (record.Networks.Count > 0)
                {
                    await _containers.RefreshNetworkInfoAsync(record, ct).ConfigureAwait(false);
                }
                else if (record.Ports.Count > 0)
                {
                    await _containers.EnsurePublishedPortsAsync(record, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is RuntimeException or IOException)
            {
                report.Warnings.Add($"could not refresh networking for container {record.Name}: {ex.Message}");
            }
        }
    }

    private async Task SyncDnsForwardersAsync(SyncReport report, CancellationToken ct)
    {
        // Forwarders for a network that no longer exists are already torn down as part of dropping
        // that network's record (NetworkManager.ReconcileAsync); this only has to make sure every
        // network that still has a running container gets one.
        var networksWithRunningContainers = _containers.AllRecords()
            .Where(record => record.State.Running)
            .SelectMany(record => record.Networks.Keys)
            .Distinct(StringComparer.Ordinal);

        foreach (var network in networksWithRunningContainers)
        {
            try
            {
                // A null result is not itself a warning here: it is also what a daemon with DNS
                // turned off by configuration always returns (NullDnsForwarderService), and the real
                // service already logs its own warning when it has one (no gateway yet, the DNS
                // server never bound, …) — duplicating that here would be misleading noise on a
                // daemon that has DNS disabled on purpose. A non-null address does mean cider sync did
                // something observable for this network's DNS (started the forwarder or confirmed it
                // is still up), so it goes in the report (cider-ede.39) — see SyncReport.Dns.
                if (await _dnsForwarder.EnsureAsync(network, ct).ConfigureAwait(false) is not null)
                {
                    report.Dns.Adopted.Add(network);
                }
            }
            catch (Exception ex) when (ex is RuntimeException or IOException)
            {
                report.Warnings.Add($"could not ensure the DNS forwarder for network {network}: {ex.Message}");
            }
        }
    }
}
