using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer;

public sealed partial class AppleContainerRuntime
{
    // ---- networks ---------------------------------------------------------

    public Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var networks = await _cli.RunJsonAsync<List<AppleNetworkJson>>(["network", "ls", "--format", "json"], ct);
        if (networks is null)
        {
            return (IReadOnlyList<RuntimeNetwork>)Array.Empty<RuntimeNetwork>();
        }

        var mapped = new List<RuntimeNetwork>(networks.Count);
        foreach (var network in networks)
        {
            mapped.Add(RuntimeMapper.ToNetwork(network));
        }

        return mapped;
    });

    public Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var result = await _cli.RunAsync(["network", "inspect", name], ct);
        if (!result.Succeeded)
        {
            if (CliErrorMapper.Classify(result.Stderr) == RuntimeErrorKind.NotFound)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, $"network inspect {name}");
        }

        var networks = ParseOneOrMany<AppleNetworkJson>(result.Stdout, "container network inspect");
        return networks.Count > 0 ? RuntimeMapper.ToNetwork(networks[0]) : null;
    });

    /// <summary>
    /// Network and volume create/delete run on <see cref="AppleContainerOptions.ResourceTimeout"/>
    /// (30 s) instead of the general five-minute <c>CommandTimeout</c>: dockerd answers these in
    /// milliseconds, and every client above us reads a multi-minute stall as a dead daemon. The
    /// list/inspect paths still use the general budget — bounding those is the general timeout
    /// audit this ticket deliberately leaves open.
    /// </summary>
    public Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        var result = await _cli.RunAsync(ArgBuilder.CreateNetwork(spec), ct, _options.ResourceTimeout);
        ContainerCli.ThrowIfFailed(result, $"network create {spec.Name}");
    });

    public Task RemoveNetworkAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var result = await _cli.RunAsync(["network", "delete", name], ct, _options.ResourceTimeout);
        if (!result.Succeeded)
        {
            // A missing network and a network still in use produce the same outer message;
            // only the in-use variant carries an invalidState/referring-containers cause (notes §12).
            throw CliErrorMapper.ToException(result, $"network delete {name}");
        }
    });

    // ---- volumes ----------------------------------------------------------

    public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var volumes = await _cli.RunJsonAsync<List<AppleVolumeJson>>(["volume", "ls", "--format", "json"], ct);
        if (volumes is null)
        {
            return (IReadOnlyList<RuntimeVolume>)Array.Empty<RuntimeVolume>();
        }

        var mapped = new List<RuntimeVolume>(volumes.Count);
        foreach (var volume in volumes)
        {
            mapped.Add(RuntimeMapper.ToVolume(volume));
        }

        return mapped;
    });

    public Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var result = await _cli.RunAsync(["volume", "inspect", name], ct);
        if (!result.Succeeded)
        {
            if (CliErrorMapper.Classify(result.Stderr) == RuntimeErrorKind.NotFound)
            {
                return null;
            }

            throw CliErrorMapper.ToException(result, $"volume inspect {name}");
        }

        var volumes = ParseOneOrMany<AppleVolumeJson>(result.Stdout, "container volume inspect");
        return volumes.Count > 0 ? RuntimeMapper.ToVolume(volumes[0]) : null;
    });

    public Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        var result = await _cli.RunAsync(ArgBuilder.CreateVolume(spec), ct, _options.ResourceTimeout);
        ContainerCli.ThrowIfFailed(result, $"volume create {spec.Name}");
    });

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (force)
        {
            // `container volume delete` has no force flag on 1.2.2.
            _logger.LogDebug("ignoring force on volume delete: the container CLI has no such flag");
        }

        var result = await _cli.RunAsync(["volume", "delete", name], ct, _options.ResourceTimeout);
        if (!result.Succeeded)
        {
            throw CliErrorMapper.ToException(result, $"volume delete {name}");
        }
    });

    // ---- disk usage -------------------------------------------------------

    public Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var usage = await _cli.RunJsonAsync<AppleDiskUsage>(["system", "df", "--format", "json"], ct);
        return usage is null ? new RuntimeDiskUsage() : RuntimeMapper.ToDiskUsage(usage);
    });

    /// <summary>The CLI prints inspect results as an array, but tolerate a bare object too.</summary>
    private static List<T> ParseOneOrMany<T>(string json, string context)
    {
        var text = json.TrimStart();
        if (text.StartsWith('['))
        {
            return ContainerCli.ParseJson<List<T>>(json, context) ?? [];
        }

        var single = ContainerCli.ParseJson<T>(json, context);
        return single is null ? [] : [single];
    }
}
