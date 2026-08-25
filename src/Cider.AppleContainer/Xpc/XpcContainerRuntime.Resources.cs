using System.Globalization;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <c>CreateNetworkAsync</c>/<c>RemoveNetworkAsync</c>/<c>CreateVolumeAsync</c>/<c>RemoveVolumeAsync</c>
/// over XPC (task cider-ede.11); list/inspect for both resources were already ported in cider-ede.5
/// (<c>XpcContainerRuntime.cs</c>'s "networks: read paths"/"volumes: read paths" regions). Routes:
/// <c>networkCreate</c>/<c>networkDelete</c>/<c>volumeCreate</c>/<c>volumeDelete</c>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §2.4-2.5).
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    // ---- networks: write paths ------------------------------------------------------------------

    /// <summary>
    /// <c>networkCreate{networkConfig}</c> (§2.4). <c>networkCreate</c> is registered only on
    /// macOS 26+ (<c>APIServer+Start.swift:351-355</c>) — on an older host the route does not exist
    /// at all and a call would simply hang with no reply (§1.6's "unknown route" rule), so the
    /// fallback decision is made up front from <see cref="RuntimeCapabilities.NetworkCreate"/>
    /// instead of racing a client-side timeout against a route that was never going to answer.
    /// </summary>
    public Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!_capabilities.NetworkCreate)
        {
            await _cliFallback.CreateNetworkAsync(spec, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var config = BuildNetworkConfiguration(spec);
            using var request = new XpcMessage("networkCreate");
            request.SetData("networkConfig", XpcJson.SerializeToUtf8Bytes(config));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("networkCreate", ex);
            await _cliFallback.CreateNetworkAsync(spec, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"network create {spec.Name}");
        }
    });

    /// <summary>
    /// <c>networkDelete{networkId}</c> (§2.4) — no client-side timeout, matching the container
    /// delete/stop/kill routes (<c>XpcContainerRuntime.Create.cs</c>). A missing network and one
    /// still referenced by a running container are already distinct apiserver codes
    /// (<c>notFound</c> vs. <c>invalidState</c>, <c>NetworksService.swift:243,266</c>), so the
    /// generic <see cref="XpcErrorMapper"/> table (<c>notFound</c> → <see cref="RuntimeErrorKind.NotFound"/>,
    /// <c>invalidState</c> → <see cref="RuntimeErrorKind.Conflict"/>) already gives the right answer
    /// with no extra text-sniffing needed here — unlike volumes (see <see cref="ToVolumeRuntimeException"/>).
    /// </summary>
    public Task RemoveNetworkAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        try
        {
            using var request = new XpcMessage("networkDelete");
            request.SetString("networkId", name);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("networkDelete", ex);
            await _cliFallback.RemoveNetworkAsync(name, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"network delete {name}");
        }
    });

    /// <summary>
    /// <see cref="NetworkSpec"/> → the request's <c>NetworkConfiguration</c> (§2.4's request row,
    /// <c>NetworkConfiguration.swift:59-68</c>'s canonical encode shape). <see cref="NetworkSpec.Internal"/>
    /// selects <c>NetworkMode</c> exactly the way the real CLI does it
    /// (<c>NetworkCreate.swift:68</c>: <c>hostOnly ? .hostOnly : .nat</c>); <c>plugin</c> is always
    /// <c>"container-network-vmnet"</c>, the same default the CLI's own <c>--plugin</c> option carries
    /// (<c>NetworkCreate.swift:39</c>) and the only plugin cider ever asks for.
    /// </summary>
    internal static NetworkConfiguration BuildNetworkConfiguration(NetworkSpec spec) => new()
    {
        Name = spec.Name,
        CreationDate = DateTimeOffset.UtcNow,
        Mode = spec.Internal ? "hostOnly" : "nat",
        Ipv4Subnet = string.IsNullOrEmpty(spec.Subnet) ? null : spec.Subnet,
        Ipv6Subnet = string.IsNullOrEmpty(spec.SubnetV6) ? null : spec.SubnetV6,
        Labels = spec.Labels.Count > 0 ? new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal) : [],
        Plugin = "container-network-vmnet",
        Options = spec.Options.Count > 0 ? new Dictionary<string, string>(spec.Options, StringComparer.Ordinal) : [],
    };

    // ---- volumes: write paths -------------------------------------------------------------------

    /// <summary>
    /// <c>volumeCreate{volumeName, volumeDriver, volumeDriverOpts, volumeLabels}</c> (§2.5) — the
    /// request carries plain <c>[String:String]</c> dictionaries for <c>volumeDriverOpts</c>/
    /// <c>volumeLabels</c>, not a <see cref="VolumeConfiguration"/> (unlike <c>containerConfig</c>).
    /// </summary>
    public Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        try
        {
            using var request = new XpcMessage("volumeCreate");
            request.SetString("volumeName", spec.Name);
            request.SetString("volumeDriver", "local");
            request.SetData("volumeDriverOpts", XpcJson.SerializeToUtf8Bytes(BuildVolumeDriverOpts(spec)));
            request.SetData("volumeLabels", XpcJson.SerializeToUtf8Bytes(new Dictionary<string, string>(spec.Labels, StringComparer.Ordinal)));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("volumeCreate", ex);
            await _cliFallback.CreateVolumeAsync(spec, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ToVolumeRuntimeException(ex, $"volume create {spec.Name}");
        }
    });

    /// <summary><c>volumeDelete{volumeName}</c> (§2.5). <c>force</c> has no apiserver equivalent —
    /// same "the route has no such flag" gap the CLI transport already tolerates
    /// (<c>AppleContainerRuntime.Resources.cs</c>'s own <c>RemoveVolumeAsync</c>), so it is logged and
    /// otherwise ignored rather than failing the call.</summary>
    public Task RemoveVolumeAsync(string name, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (force)
        {
            _logger.LogDebug("ignoring force on volume delete: the apiserver's volumeDelete route has no such option");
        }

        try
        {
            using var request = new XpcMessage("volumeDelete");
            request.SetString("volumeName", name);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("volumeDelete", ex);
            await _cliFallback.RemoveVolumeAsync(name, force, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ToVolumeRuntimeException(ex, $"volume delete {name}");
        }
    });

    /// <summary>
    /// <c>--size</c> travels as <c>driverOpts["size"]</c>, never <c>XPCKeys.volumeSize</c>
    /// (<c>VolumeCreate.swift:48-52</c>) — a raw byte-count string, passed through verbatim with no
    /// K/M/G/T/P suffix formatting, exactly like the CLI transport's own
    /// <c>ArgBuilder.CreateVolume</c> (<c>-s {SizeBytes}</c>), so both transports hand the apiserver's
    /// <c>parseSize</c> the identical string.
    /// </summary>
    internal static Dictionary<string, string> BuildVolumeDriverOpts(VolumeSpec spec)
    {
        var opts = new Dictionary<string, string>(spec.Options, StringComparer.Ordinal);
        if (spec.SizeBytes is > 0)
        {
            opts["size"] = spec.SizeBytes.Value.ToString(CultureInfo.InvariantCulture);
        }

        return opts;
    }

    /// <summary>
    /// Every <c>VolumeError</c> case (not found, already exists, in use, invalid name —
    /// <c>VolumeConfiguration.swift:110-134</c>) is a plain Swift <c>Error</c>, not a
    /// <c>ContainerizationError</c>; the apiserver's generic catch-all wraps it as apiserver code
    /// <c>invalidArgument</c> carrying only the human message (docs/spikes/xpc/02-apiserver-xpc-protocol.md
    /// §2.5: "VolumeError cases surface as .invalidArgument via the XPCServer string sniff"). The
    /// generic <see cref="XpcErrorMapper"/> code table can't tell those apart by code alone, so — the
    /// same as <see cref="XpcErrorMapper.ToRuntimeErrorReason"/> already does for a container's
    /// <c>invalidState</c>/"not running", and exactly what <c>Cli.CliErrorMapper</c> does for the CLI
    /// transport's own volume messages — this reads the message text once, here, below the
    /// <c>IContainerRuntime</c> seam. Any other code (e.g. a real <c>invalidArgument</c> for a bad
    /// name) falls through to the ordinary generic mapping unchanged.
    /// </summary>
    internal static RuntimeException ToVolumeRuntimeException(XpcException ex, string context)
    {
        if (ex.ErrorClass == XpcErrorClass.ApiServer && ex.Code == "invalidArgument")
        {
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeException(RuntimeErrorKind.NotFound, $"{context}: {ex.Message}", ex);
            }

            if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeException(RuntimeErrorKind.Conflict, $"{context}: {ex.Message}", ex);
            }
        }

        return ex.ToRuntimeException(context);
    }
}
