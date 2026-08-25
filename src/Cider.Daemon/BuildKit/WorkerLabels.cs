using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Adjusts the worker labels a forwarded <c>ListWorkers</c> response reports, so buildx's own
/// feature detection reads Apple's builder the way it reads a real Docker daemon.
/// <para>
/// buildx's docker driver treats <c>org.mobyproject.buildkit.worker.snapshotter</c> as a signal
/// that unsupported features (cache export, multi-platform, attestations) might work — Apple's
/// worker reports <c>snapshotter=overlayfs</c>, so that label must be stripped or buildx enables
/// paths this proxy does not implement (buildx <c>driver/docker/driver.go:82-104</c>).
/// </para>
/// </summary>
public static class WorkerLabels
{
    /// <summary>Read by buildx's <c>Features()</c> to gate cache-export/multi-platform/attestation support.</summary>
    public const string SnapshotterLabel = "org.mobyproject.buildkit.worker.snapshotter";

    /// <summary>Optional: the host-gateway IP buildx's docker driver reports for <c>host.docker.internal</c> (driver.go:112-140).</summary>
    public const string HostGatewayIpLabel = "org.mobyproject.buildkit.worker.moby.host-gateway-ip";

    /// <summary>
    /// Strips <see cref="SnapshotterLabel"/> from every worker record in <paramref name="response"/>
    /// in place, and — when <paramref name="hostGatewayIp"/> is given and the label is not already
    /// present — sets <see cref="HostGatewayIpLabel"/>.
    /// </summary>
    public static void Strip(ListWorkersResponse response, string? hostGatewayIp = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var worker in response.Record)
        {
            worker.Labels.Remove(SnapshotterLabel);

            if (!string.IsNullOrEmpty(hostGatewayIp) && !worker.Labels.ContainsKey(HostGatewayIpLabel))
            {
                worker.Labels[HostGatewayIpLabel] = hostGatewayIp;
            }
        }
    }
}
