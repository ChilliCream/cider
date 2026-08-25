using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cider.AppleContainer.Process;
using Cider.AppleContainer.Xpc.Models;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// <see cref="IContainerRuntime"/> over <c>com.apple.container.apiserver</c> XPC (task cider-ede.5).
/// The apiserver is the primary transport; the constructor's <c>cliFallback</c> only ever answers a
/// call when the apiserver reports <see cref="RuntimeErrorKind.Unavailable"/> — the "Fallback rule"
/// in the task's fix direction §4.
/// cider-ede.5 ported the read paths (<see cref="GetInfoAsync"/>, <see cref="EnsureReadyAsync"/>,
/// container list/inspect/stats, disk usage, network/volume list/inspect); cider-ede.6 ports
/// create/delete/stop/kill (<c>XpcContainerRuntime.Create.cs</c>, its own sibling partial file, plus
/// <c>ContainerConfigurationBuilder</c>/<c>KernelCache</c>/<c>InitImageResolver</c>/
/// <c>ImageSnapshotEnsurer</c>/<c>ImagesServiceClient</c>); cider-ede.9 ports <c>OpenLogsAsync</c>
/// (<c>XpcContainerRuntime.Logs.cs</c>, <see cref="FollowingFileStream"/>); cider-ede.11 ports
/// network/volume create/delete (<c>XpcContainerRuntime.Resources.cs</c>); cider-ede.12 ports
/// <c>CopyFromContainerAsync</c>/<c>CopyToContainerAsync</c>/<c>ExportContainerAsync</c>
/// (<c>XpcContainerRuntime.Archive.cs</c>); cider-ede.10 ports every image operation
/// (<c>XpcContainerRuntime.Images.cs</c>, <c>ProgressUpdateListener</c>). Every other
/// <see cref="IContainerRuntime"/> member is listed in the <c>// FALLBACK</c> block at the bottom and
/// delegates straight to the CLI runtime until a later task ports it.
/// Mapping from the wire models to <c>Cider.Core.Runtime</c> types lives in the sibling
/// <c>XpcContainerRuntime.Mapping.cs</c> file of this partial class.
/// </summary>
internal sealed partial class XpcContainerRuntime : IContainerRuntime, IDisposable
{
    /// <summary>How often a repeated apiserver-unavailable fallback on the same route is worth
    /// logging again (task fix direction §4: "log Warning once per minute").</summary>
    private static readonly TimeSpan FallbackWarnInterval = TimeSpan.FromMinutes(1);

    /// <summary>Matches the kernel version out of a <c>getDefaultKernel</c> reply's <c>kernel.path</c>
    /// file name, e.g. <c>"vmlinux-6.18.15-186"</c> → <c>"6.18.15"</c> — the same shape
    /// <c>AppleContainerRuntime.KernelRegex</c> extracts from <c>container system property list</c>,
    /// kept here so <see cref="GetInfoAsync"/> reports the identical value over either transport.</summary>
    [GeneratedRegex(@"vmlinux-(?<kernel>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex KernelVersionRegex();

    private readonly IContainerRuntime _cliFallback;
    private readonly XpcClient _apiserver;
    private readonly XpcClient _images;
    private readonly RuntimeCapabilities _capabilities;
    private readonly AppleContainerOptions _options;
    private readonly ILogger _logger;

    /// <summary>The three-route images-service wrapper create's preconditions need (task cider-ede.6);
    /// held here (not just inside <see cref="_imageSnapshotEnsurer"/>/<see cref="_initImageResolver"/>)
    /// so cider-ede.10 can reuse the same instance instead of re-plumbing <see cref="_images"/>.</summary>
    private readonly ImagesServiceClient _imagesClient;

    /// <summary><c>getDefaultKernel</c>, cached for this runtime's lifetime — see <see cref="KernelCache"/>.</summary>
    private readonly KernelCache _kernelCache;

    /// <summary>Resolves + unpacks the container's own image snapshot before <c>containerCreate</c> —
    /// see <see cref="ImageSnapshotEnsurer"/>.</summary>
    private readonly ImageSnapshotEnsurer _imageSnapshotEnsurer;

    /// <summary>Resolves + unpacks the vminit init image, once, cached — see <see cref="InitImageResolver"/>.</summary>
    private readonly InitImageResolver _initImageResolver;

    /// <summary>Resolves <c>containerSystemConfig.dns.domain</c> for the attachment FQDN rule, once,
    /// cached — see <see cref="SystemDnsDomainResolver"/>.</summary>
    private readonly SystemDnsDomainResolver _dnsDomainResolver;

    /// <summary>Last time a fallback warning was logged for a given route, keyed by XPC route name —
    /// backs the "once per minute" throttle in <see cref="WarnFallback"/>.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFallbackWarnAt = new(StringComparer.Ordinal);

    /// <param name="cliFallback">The CLI-backed runtime every unported member (the <c>// FALLBACK</c>
    /// block) delegates to outright, and that reads fall back to on
    /// <see cref="RuntimeErrorKind.Unavailable"/>.</param>
    /// <param name="apiserver">One <see cref="XpcClient"/> for <c>com.apple.container.apiserver</c> —
    /// owned by this instance, disposed with it.</param>
    /// <param name="images">One <see cref="XpcClient"/> for
    /// <c>com.apple.container.core.container-core-images</c>, wrapped as <see cref="_imagesClient"/> —
    /// used by both container-creation preconditions (cider-ede.6) and every image operation
    /// (cider-ede.10, <c>XpcContainerRuntime.Images.cs</c>).</param>
    public XpcContainerRuntime(
        IContainerRuntime cliFallback,
        XpcClient apiserver,
        XpcClient images,
        RuntimeCapabilities capabilities,
        AppleContainerOptions options,
        ILogger<XpcContainerRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(cliFallback);
        ArgumentNullException.ThrowIfNull(apiserver);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _cliFallback = cliFallback;
        _apiserver = apiserver;
        _images = images;
        _capabilities = capabilities;
        _options = options;
        _logger = logger;

        _imagesClient = new ImagesServiceClient(images, options.PullTimeout);
        _kernelCache = new KernelCache(apiserver);
        _imageSnapshotEnsurer = new ImageSnapshotEnsurer(_imagesClient);
        _initImageResolver = new InitImageResolver(options, _imagesClient, logger);
        _dnsDomainResolver = new SystemDnsDomainResolver(options, logger);
    }

    /// <summary>
    /// Always <c>true</c> in practice — <see cref="RuntimeTransportSelector"/> only ever constructs
    /// this class once it has settled on <see cref="RuntimeTransportKind.Xpc"/> — but reads
    /// <see cref="_capabilities"/> rather than hardcoding that so it stays correct if that ever
    /// changes (task cider-ede.19's fix direction: the poller's transport-aware default).
    /// </summary>
    public bool IsXpcTransport => _capabilities.Transport == RuntimeTransportKind.Xpc;

    // ---- system -------------------------------------------------------------------------------

    /// <summary>
    /// <c>ping</c> → <see cref="RuntimeInfo.Version"/> (the <c>apiServerVersion</c> banner's semver)
    /// and <see cref="RuntimeInfo.AppRoot"/> (the <c>appRoot</c> file URL, decoded to a local path);
    /// <c>getDefaultKernel</c> → <see cref="RuntimeInfo.KernelVersion"/> from the kernel path's file
    /// name (best-effort: a failure here does not fail the whole call, matching
    /// <c>AppleContainerRuntime.GetInfoAsync</c>'s own "kernel version unknown is fine" tolerance).
    /// Falls back to <c>cliFallback.GetInfoAsync</c> entirely when <c>ping</c> itself reports
    /// <see cref="RuntimeErrorKind.Unavailable"/> (task fix direction §4).
    /// </summary>
    public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        string version;
        string? appRoot;
        try
        {
            using var request = new XpcMessage("ping");
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

            var banner = reply.GetString("apiServerVersion");
            version = ApiServerVersion.TryParse(banner, out var parsed) ? parsed!.Semver.ToString() : banner ?? "";
            appRoot = DecodeFileUrl(reply.GetString("appRoot"));
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("ping", ex);
            return await _cliFallback.GetInfoAsync(ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException("get runtime info");
        }

        return new RuntimeInfo
        {
            Name = "apple-container",
            Version = version,
            KernelVersion = await TryGetKernelVersionAsync(ct).ConfigureAwait(false),
            Ready = true,
            AppRoot = appRoot,
        };
    });

    /// <summary>Best-effort <c>getDefaultKernel</c>: any failure (transport, apiserver, or a reply
    /// that does not parse) is logged at Debug and answered as "unknown", exactly like the CLI
    /// transport's own <c>TryReadKernelVersionAsync</c> — this is metadata, not a readiness signal,
    /// so it must never turn a healthy <see cref="GetInfoAsync"/> into a failure or a CLI fallback.</summary>
    private async Task<string?> TryGetKernelVersionAsync(CancellationToken ct)
    {
        try
        {
            using var request = new XpcMessage("getDefaultKernel");
            request.SetData("systemPlatform", XpcJson.SerializeToUtf8Bytes(SystemPlatform.Current));
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

            var bytes = reply.GetData("kernel");
            if (bytes is null)
            {
                return null;
            }

            var kernel = XpcJson.Deserialize<Kernel>(bytes);
            var match = KernelVersionRegex().Match(kernel.Path);
            return match.Success ? match.Groups["kernel"].Value : null;
        }
        catch (XpcException ex)
        {
            _logger.LogDebug(ex, "getDefaultKernel failed; kernel version will be reported as unknown");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "getDefaultKernel reply did not parse; kernel version will be reported as unknown");
            return null;
        }
    }

    /// <summary><c>"file:///Users/…/appRoot/"</c> → <c>"/Users/…/appRoot/"</c> (percent-decoded).</summary>
    private static string? DecodeFileUrl(string? url) =>
        !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : url;

    /// <summary>
    /// Sweeps orphaned held CLI processes unconditionally — task fix direction §2: "keep the orphan
    /// sweep call" — since those can exist (a prior daemon run under <c>RuntimeTransport=cli</c>, or a
    /// held <c>container start -a</c> child from <see cref="_cliFallback"/> use) regardless of which
    /// transport this run selected. Then: <c>ping</c> succeeds → no-op (the apiserver is already up,
    /// nothing to start); any <c>ping</c> failure → the CLI runtime's own <see cref="IContainerRuntime.EnsureReadyAsync"/>
    /// (<c>container system start</c>), which the daemon needs regardless of transport since the
    /// apiserver and the CLI are the same underlying <c>container</c> service.
    /// </summary>
    public Task EnsureReadyAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var reaped = new OrphanReaper(_logger, _options.CliPath).ReapOrphanedHeldProcesses();
        if (reaped > 0)
        {
            _logger.LogInformation("startup sweep reaped {Count} orphaned held process(es)", reaped);
        }

        try
        {
            using var request = new XpcMessage("ping");
            (await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false)).Dispose();
            return;
        }
        catch (XpcException ex)
        {
            _logger.LogInformation(
                "apiserver did not respond to ping ({Reason}); starting Apple container services via the CLI",
                ex.Message);
        }

        await _cliFallback.EnsureReadyAsync(ct).ConfigureAwait(false);
    });

    // ---- containers: read paths ----------------------------------------------------------------

    /// <summary><c>containerList</c> with the "no filter" payload (§8.2) — every container, including
    /// stopped ones and cider's own hidden containers (filtering those out is
    /// <c>ContainerManager.IsSystemContainer</c>'s job above this seam, exactly as for the CLI
    /// transport).</summary>
    public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "containerList",
            async () =>
            {
                using var request = new XpcMessage("containerList");
                request.SetData("listFilters", XpcJson.SerializeToUtf8Bytes(ContainerListFilters.All));
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.List, ct).ConfigureAwait(false);

                var bytes = reply.GetData("containers");
                var snapshots = bytes is null ? [] : XpcJson.Deserialize<List<ContainerSnapshot>>(bytes);
                return (IReadOnlyList<RuntimeContainer>)[.. snapshots.Select(ToContainer)];
            },
            () => _cliFallback.ListContainersAsync(ct)));

    /// <summary><c>containerList</c> with <c>ids:[runtimeId]</c> (task fix direction §2) — server-side
    /// filtered, so an empty reply means "does not exist", never an error.</summary>
    public Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        return await XpcReadAsync(
            "containerList",
            async () =>
            {
                using var request = new XpcMessage("containerList");
                var filters = new ContainerListFilters { Ids = [runtimeId], Labels = [] };
                request.SetData("listFilters", XpcJson.SerializeToUtf8Bytes(filters));
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.List, ct).ConfigureAwait(false);

                var bytes = reply.GetData("containers");
                var snapshots = bytes is null ? [] : XpcJson.Deserialize<List<ContainerSnapshot>>(bytes);
                return snapshots.Count > 0 ? ToContainer(snapshots[0]) : null;
            },
            () => _cliFallback.InspectContainerAsync(runtimeId, ct)).ConfigureAwait(false);
    });

    /// <summary>
    /// <c>containerStats</c>. A <c>notFound</c>/<c>invalidState</c> apiserver error (no container, or
    /// one not running yet) answers <c>null</c> — the same "no stats yet" contract
    /// <c>AppleContainerRuntime.GetStatsAsync</c> gives the CLI transport — rather than surfacing as
    /// an exception.
    /// </summary>
    public Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeId);

        try
        {
            using var request = new XpcMessage("containerStats");
            request.SetString("id", runtimeId);
            using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

            var bytes = reply.GetData("statistics");
            return bytes is null ? null : ToStats(XpcJson.Deserialize<ContainerStats>(bytes), DateTimeOffset.UtcNow);
        }
        catch (XpcException ex) when (XpcErrorMapper.ToRuntimeErrorKind(ex) is RuntimeErrorKind.NotFound or RuntimeErrorKind.Conflict)
        {
            return null;
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback("containerStats", ex);
            return await _cliFallback.GetStatsAsync(runtimeId, ct).ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException($"stats {runtimeId}");
        }
    });

    // ---- system: disk usage ---------------------------------------------------------------------

    /// <summary><c>systemDiskUsage</c>; no request payload (§2.6).</summary>
    public Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "systemDiskUsage",
            async () =>
            {
                using var request = new XpcMessage("systemDiskUsage");
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

                var bytes = reply.GetData("diskUsageStats")
                    ?? throw new JsonException("systemDiskUsage reply carried no diskUsageStats");
                return ToDiskUsage(XpcJson.Deserialize<DiskUsageStats>(bytes));
            },
            () => _cliFallback.GetDiskUsageAsync(ct)));

    // ---- networks: read paths -------------------------------------------------------------------

    /// <summary><c>networkList</c>; no request payload (§2.4).</summary>
    public Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "networkList",
            async () =>
            {
                using var request = new XpcMessage("networkList");
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

                var bytes = reply.GetData("networkResources");
                var resources = bytes is null ? [] : XpcJson.Deserialize<List<NetworkResource>>(bytes);
                return (IReadOnlyList<RuntimeNetwork>)[.. resources.Select(ToNetwork)];
            },
            () => _cliFallback.ListNetworksAsync(ct)));

    /// <summary>No <c>networkInspect</c> route exists (§2.4) — filtered client-side over
    /// <see cref="ListNetworksAsync"/>, exactly like the real CLI's own <c>NetworkClient.get(id:)</c>.</summary>
    public Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var networks = await ListNetworksAsync(ct).ConfigureAwait(false);
        return networks.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
    });

    // ---- volumes: read paths --------------------------------------------------------------------

    /// <summary><c>volumeList</c>; no request payload (§2.5). The reply carries bare
    /// <c>VolumeConfiguration</c> entries, not <c>VolumeResource</c>.</summary>
    public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct) => GuardAsync(() =>
        XpcReadAsync(
            "volumeList",
            async () =>
            {
                using var request = new XpcMessage("volumeList");
                using var reply = await _apiserver.SendAsync(request, XpcCallOptions.Default, ct).ConfigureAwait(false);

                var bytes = reply.GetData("volumes");
                var configs = bytes is null ? [] : XpcJson.Deserialize<List<VolumeConfiguration>>(bytes);
                return (IReadOnlyList<RuntimeVolume>)[.. configs.Select(ToVolume)];
            },
            () => _cliFallback.ListVolumesAsync(ct)));

    /// <summary>
    /// There is a <c>volumeInspect</c> route (§2.5), but the task's fix direction calls for the same
    /// "list, filter client-side" shape as networks ("the CLI does the same") — one fewer route to
    /// keep the fallback rule consistent across, and <see cref="ListVolumesAsync"/> already carries
    /// every field <c>volumeInspect</c> would answer.
    /// </summary>
    public Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct) => GuardAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var volumes = await ListVolumesAsync(ct).ConfigureAwait(false);
        return volumes.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
    });

    // ---- fallback plumbing ----------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="xpc"/>; on an apiserver <see cref="RuntimeErrorKind.Unavailable"/> logs
    /// (throttled) and answers from <paramref name="cliFallback"/> instead (task fix direction §4);
    /// any other <see cref="XpcException"/> — a real answer from the apiserver, including one it
    /// rejected — crosses the seam via <see cref="XpcException.ToRuntimeException"/> and is never
    /// masked by a fallback.
    /// </summary>
    private async Task<T> XpcReadAsync<T>(string route, Func<Task<T>> xpc, Func<Task<T>> cliFallback)
    {
        try
        {
            return await xpc().ConfigureAwait(false);
        }
        catch (XpcException ex) when (IsUnavailable(ex))
        {
            WarnFallback(route, ex);
            return await cliFallback().ConfigureAwait(false);
        }
        catch (XpcException ex)
        {
            throw ex.ToRuntimeException(route);
        }
    }

    private static bool IsUnavailable(XpcException ex) => XpcErrorMapper.ToRuntimeErrorKind(ex) == RuntimeErrorKind.Unavailable;

    /// <summary>Logs the apiserver-unavailable fallback for <paramref name="route"/> at most once per
    /// <see cref="FallbackWarnInterval"/> — a poller hitting this every few seconds while the
    /// apiserver is down must not flood the log.</summary>
    private void WarnFallback(string route, XpcException ex) => WarnFallback(route, ex.Message);

    /// <summary>Same throttled-once-per-minute warning as the <see cref="XpcException"/> overload, for
    /// a client-side precondition failure (kernel/image-snapshot/init-image resolution) that never
    /// produced one of its own — <see cref="CreateContainerAsync"/>'s Fallback rule treats both the
    /// same way (task fix direction §4).</summary>
    private void WarnFallback(string route, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var last = _lastFallbackWarnAt.GetOrAdd(route, DateTimeOffset.MinValue);
        if (now - last < FallbackWarnInterval)
        {
            return;
        }

        _lastFallbackWarnAt[route] = now;
        _logger.LogWarning("xpc {Route} unavailable ({Reason}); falling back to the CLI", route, reason);
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeException(RuntimeErrorKind.Internal, ex.Message, ex);
        }
    }

    private static async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (RuntimeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeException(RuntimeErrorKind.Internal, ex.Message, ex);
        }
    }

    public void Dispose()
    {
        _apiserver.Dispose();
        _images.Dispose();
    }

    // ---- FALLBACK -----------------------------------------------------------------------------
    // Every IContainerRuntime member cider-ede.5 does not port. Each delegates straight to the CLI
    // runtime — no XPC attempted, no fallback warning (there is nothing to fall back *from*). Listed
    // explicitly, one line each, so a later task (write paths X6, process model X6, images X10) can
    // find and remove its own entries here as it ports them, without having to re-audit the whole
    // interface.

    // CreateContainerAsync/RemoveContainerAsync/StopContainerAsync/KillContainerAsync are ported —
    // see XpcContainerRuntime.Create.cs (task cider-ede.6). OpenLogsAsync is ported — see
    // XpcContainerRuntime.Logs.cs (task cider-ede.9). CreateNetworkAsync/RemoveNetworkAsync/
    // CreateVolumeAsync/RemoveVolumeAsync are ported — see XpcContainerRuntime.Resources.cs (task
    // cider-ede.11). CopyFromContainerAsync/CopyToContainerAsync/ExportContainerAsync are ported —
    // see XpcContainerRuntime.Archive.cs (task cider-ede.12). StartContainerAsync/WaitContainerAsync
    // are ported — see XpcContainerRuntime.Process.cs/XpcContainerProcess.cs (task cider-ede.7).
    // ListImagesAsync/InspectImageAsync/PullImageAsync/PushImageAsync/TagImageAsync/
    // RemoveImageAsync/SaveImagesAsync/LoadImagesAsync are ported — see XpcContainerRuntime.Images.cs
    // (task cider-ede.10). BuildImageAsync stays on the CLI (classic builder, task's non-goals) and
    // LoginAsync stays on the CLI (registry login stores credentials the images service reads, fix
    // direction §2) — neither is this task's job.

    public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct) =>
        _cliFallback.ExecAsync(runtimeId, spec, ct);

    public Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct) =>
        _cliFallback.BuildImageAsync(spec, progress, ct);

    public Task LoginAsync(RegistryAuth auth, CancellationToken ct) => _cliFallback.LoginAsync(auth, ct);

    public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct) => _cliFallback.GetBuilderStatusAsync(ct);

    public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct) =>
        _cliFallback.StartBuilderAsync(cpus, memoryBytes, ct);

    public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct) => _cliFallback.DialBuilderAsync(ct);
}
