using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <c>CreateContainerAsync</c>/<c>RemoveContainerAsync</c>/<c>StopContainerAsync</c>/<c>KillContainerAsync</c>
/// over XPC (task cider-ede.6). <see cref="CreateContainerAsync"/> is the one member of
/// <see cref="XpcContainerRuntime"/> that does real client-side work before ever calling the
/// apiserver — resolving the image snapshot, the kernel, the init image and every named volume mount
/// — mirroring what Apple's own CLI does in <c>Utility.containerConfigFromFlags</c>
/// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §3.2) before it ever calls <c>containerCreate</c>.
/// The other three are single XPC calls once the id is at hand.
/// </summary>
internal sealed partial class XpcContainerRuntime
{
    /// <summary>
    /// Resolves every precondition (§8.3: "the image snapshot must already exist... any named volume
    /// must already exist... networks[].network must name an existing network"), builds the
    /// <see cref="ContainerConfiguration"/>, and calls <c>containerCreate</c>. Any client-side
    /// precondition failure that means "the xpc transport cannot do this yet" — the init image is not
    /// present locally and cider-ede.10 has not landed pull support (see
    /// <see cref="InitImageResolver"/>'s own doc comment) — surfaces as
    /// <see cref="RuntimeErrorKind.Unavailable"/> and is treated exactly like an apiserver-unavailable
    /// XPC failure: fall back to the CLI runtime, which still does its own client-side pull/unpack
    /// work (task fix direction §4's Fallback rule). Every other failure (bad id, missing volume,
    /// image not found, the apiserver itself rejecting the call) is a real answer and crosses the seam
    /// unchanged.
    /// </summary>
    public Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Every ContainerManager-merged spec sets Entrypoint = argv[0] (ContainerManager.Spec.cs,
        // Create.cs's own entrypoint/cmd merge): a null/empty Entrypoint here provably means the
        // caller is relying on the image's own entrypoint/cmd — something only the CLI can resolve,
        // since the apiserver never reads an image config (§6) and ContainerConfigurationBuilder's
        // SplitCommand would otherwise silently mis-split (Args[0] becomes the executable) or throw.
        // Fall back before doing any of the client-side precondition work below (task fix direction
        // §1).
        if (string.IsNullOrEmpty(spec.Entrypoint))
        {
            WarnFallback("containerCreate", "spec has no merged entrypoint; relying on the image's own entrypoint/cmd, which only the CLI can resolve");
            await _cliFallback.CreateContainerAsync(spec, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var targetPlatform = ContainerConfigurationBuilder.ResolveTargetPlatform(spec.Platform);

            var image = await _imageSnapshotEnsurer.EnsureAsync(spec.Image, targetPlatform, ct).ConfigureAwait(false);
            var kernel = await _kernelCache.GetAsync(ct).ConfigureAwait(false);
            var initImage = await _initImageResolver.ResolveAsync(ct).ConfigureAwait(false);
            var volumes = await ResolveVolumesAsync(spec, ct).ConfigureAwait(false);
            var dnsDomain = await _dnsDomainResolver.ResolveAsync(ct).ConfigureAwait(false);

            var config = ContainerConfigurationBuilder.Build(
                spec, image, new ContainerConfigurationBuilder.BuildContext(volumes, dnsDomain));

            using var request = new XpcMessage("containerCreate");
            request.SetData("containerConfig", XpcJson.SerializeToUtf8Bytes(config));
            request.SetData("kernel", XpcJson.SerializeToUtf8Bytes(kernel));
            request.SetData("containerOptions", XpcJson.SerializeToUtf8Bytes(ContainerCreateOptions.Default));
            request.SetString("initImage", initImage);

            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerCreate", ex);
            await _cliFallback.CreateContainerAsync(spec, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.Unavailable)
        {
            WarnFallback("containerCreate", ex.Message);
            await _cliFallback.CreateContainerAsync(spec, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"create {spec.RuntimeId}");
        }
    });

    /// <summary><c>containerDelete{id, forceDelete}</c> (§8.9). No client-side timeout (§1.4: this
    /// route blocks until the daemon finishes tearing the container down).</summary>
    public Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        try
        {
            using var request = new XpcMessage("containerDelete");
            request.SetString("id", runtimeId);
            request.SetBool("forceDelete", force);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerDelete", ex);
            await _cliFallback.RemoveContainerAsync(runtimeId, force, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"delete {runtimeId}");
        }
    });

    /// <summary>
    /// <c>containerStop{id, stopOptions:{timeoutInSeconds, signal}}</c> (§8.7) —
    /// <c>stopOptions</c> is mandatory (§8.11 gotcha 7); <paramref name="timeoutSeconds"/> defaults to
    /// 10 when unset (Docker's own default, task fix direction §4). Doc decision (task fix direction
    /// §6, stop-timeout parity): <c>AppleContainerRuntime.StopContainerAsync</c> (the CLI transport)
    /// omits <c>-t</c> entirely when <paramref name="timeoutSeconds"/> is <c>null</c>, letting the CLI
    /// apply whatever default it has; this XPC path always sends <c>10</c> instead, since
    /// <c>stopOptions.timeoutInSeconds</c> is not itself optional on the wire (§8.11 gotcha 7) — there
    /// is no "omit" to fall back to. The two transports agree in practice (10 is also the CLI's own
    /// default) but that agreement is coincidental, not enforced; this divergence is deliberate and
    /// accepted, not a bug to fix later. <paramref name="signal"/> is normalized to <c>SIGxxx</c> when
    /// given, left <c>null</c> when not so the daemon applies its own fallback chain
    /// (<c>configuration.stopSignal</c>, then <c>SIGTERM</c> — §2.2). No client-side timeout (§1.4): a
    /// slow graceful stop must not be torn down by us mid-grace-period, matching the CLI transport's
    /// own generous stop budget.
    /// </summary>
    public Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        try
        {
            using var request = new XpcMessage("containerStop");
            request.SetString("id", runtimeId);

            var options = new ContainerStopOptions
            {
                TimeoutInSeconds = timeoutSeconds ?? 10,
                Signal = string.IsNullOrWhiteSpace(signal) ? null : ContainerConfigurationBuilder.NormalizeSignal(signal, "TERM"),
            };
            request.SetData("stopOptions", XpcJson.SerializeToUtf8Bytes(options));

            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerStop", ex);
            await _cliFallback.StopContainerAsync(runtimeId, timeoutSeconds, signal, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"stop {runtimeId}");
        }
    });

    /// <summary>
    /// <c>containerKill{id, processIdentifier:id, signal}</c> (§8.8) — <paramref name="signal"/>
    /// normalized to <c>SIGxxx</c>, always sent as a string (§8.11 gotcha 6: an int64 signal is
    /// silently read as missing server-side). <c>processIdentifier</c> is the container id itself:
    /// this always targets the init process, never an exec (§8.8, §2.1).
    /// </summary>
    public Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);
        ArgumentException.ThrowIfNullOrEmpty(signal);

        try
        {
            using var request = new XpcMessage("containerKill");
            request.SetString("id", runtimeId);
            request.SetString("processIdentifier", runtimeId);
            request.SetString("signal", ContainerConfigurationBuilder.NormalizeSignal(signal, "KILL"));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.NoTimeout, ct).ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerKill", ex);
            await _cliFallback.KillContainerAsync(runtimeId, signal, ct).ConfigureAwait(false);
            return;
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"kill {runtimeId}");
        }
    });

    /// <summary>
    /// <c>volumeInspect{volumeName}</c> (§2.5) once per distinct named-volume mount in
    /// <paramref name="spec"/> — never <c>volumeCreate</c>: any named volume a create request asks for
    /// is already ensured to exist above this seam (<c>ContainerManager.Spec.cs</c>'s own
    /// <c>_volumes.EnsureAsync</c> call, which runs before a <see cref="ContainerSpec"/> is ever
    /// built), matching this task's non-goal "volume/network creation (X10)". A <c>notFound</c> here
    /// surfaces as <see cref="RuntimeErrorKind.NotFound"/> through the normal <see cref="XpcException"/>
    /// path in <see cref="CreateContainerAsync"/> — a real, if surprising, answer, not a fallback
    /// trigger.
    /// </summary>
    private async Task<Dictionary<string, VolumeConfiguration>> ResolveVolumesAsync(ContainerSpec spec, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var mount in spec.Mounts)
        {
            if (mount.Kind == MountKind.Volume && !names.Contains(mount.Source, StringComparer.Ordinal))
            {
                names.Add(mount.Source);
            }
        }

        if (names.Count == 0)
        {
            return [];
        }

        var result = new Dictionary<string, VolumeConfiguration>(names.Count, StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var request = new XpcMessage("volumeInspect");
            request.SetString("volumeName", name);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

            var bytes = reply.GetData("volume")
                ?? throw new System.Text.Json.JsonException($"volumeInspect reply for '{name}' carried no volume");
            result[name] = XpcJson.Deserialize<VolumeConfiguration>(bytes);
        }

        return result;
    }
}
