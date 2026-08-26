namespace Cider.Core.Runtime;

/// <summary>
/// The seam between cider's Docker emulation and the actual container engine.
/// Implemented by <c>Cider.AppleContainer.AppleContainerRuntime</c> (and by the test fake).
/// </summary>
public interface IContainerRuntime
{
    /// <summary>
    /// <c>true</c> for the XPC transport, whose <c>containerList</c> pass costs ~0.1 ms versus the
    /// CLI's ~19 ms process spawn (docs/spikes/xpc/04-dotnet-xpc-probe-report.md, "Latency" table) —
    /// lets <c>Cider.Core.Services.StatePoller</c> pick a much tighter default poll cadence without
    /// this seam depending on <c>Cider.AppleContainer</c>'s <c>RuntimeCapabilities</c> type (that
    /// would be a back-reference: <c>Cider.AppleContainer</c> already depends on <c>Cider.Core</c>).
    /// Defaults to <c>false</c> (the CLI-spawn cadence) so every implementation that never overrides
    /// this — the CLI runtime, test fakes — keeps today's behaviour unchanged.
    /// </summary>
    bool IsXpcTransport => false;

    /// <summary>Runtime name/version, kernel version and whether the apiserver is up.</summary>
    Task<RuntimeInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>Brings the runtime up if it is not running (e.g. <c>container system start</c>).</summary>
    Task EnsureReadyAsync(CancellationToken ct);

    // ---- containers -------------------------------------------------------

    /// <summary>Creates a container; <c>spec.RuntimeId</c> is the engine-side id chosen by the daemon.</summary>
    Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct);

    /// <summary>Starts a container and returns the held process (stdio of the init process + exit code).</summary>
    Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct);

    Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct);

    Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct);

    Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct);

    /// <summary>All containers, including stopped ones.</summary>
    Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct);

    /// <summary><c>null</c> when the container does not exist.</summary>
    Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct);

    /// <summary>
    /// Blocks until <paramref name="runtimeId"/>'s init process exits, even one the daemon did not
    /// itself start (e.g. after a daemon restart) — the XPC apiserver's own <c>containerWait</c> call
    /// (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.6) blocks the same way. Returns <c>null</c>
    /// when the transport cannot wait at all (the CLI: there is no equivalent command), in which case
    /// callers keep today's "exit code unknown" path.
    /// </summary>
    Task<(int ExitCode, DateTimeOffset ExitedAt)?> WaitContainerAsync(string runtimeId, CancellationToken ct);

    Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct);

    /// <summary>The runtime's own merged log stream — used only as a fallback for our own capture.</summary>
    Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct);

    Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct);

    Task CopyFromContainerAsync(string runtimeId, string containerPath, string localDestinationDir, CancellationToken ct);

    Task CopyToContainerAsync(string runtimeId, string localSourcePath, string containerPath, CancellationToken ct);

    Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct);

    // ---- images -----------------------------------------------------------

    Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct);

    /// <summary><paramref name="reference"/> is <c>name[:tag]</c> or a digest; <c>null</c> when unknown.</summary>
    Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct);

    Task PullImageAsync(string reference, string? platform, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct);

    Task PushImageAsync(string reference, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct);

    Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct);

    Task RemoveImageAsync(string reference, bool force, CancellationToken ct);

    /// <summary>
    /// Runs a store-wide reclaim of blobs no reference points at any more (cider-ede.31 fix direction
    /// §2) — called only from <c>ImageManager.PruneAsync</c> (<c>docker image/system prune</c>), never
    /// per-<c>rmi</c>: a sweep this broad racing a concurrent pull/load that has written blobs but not
    /// yet committed its index entry is exactly what corrupted the store twice in one day
    /// (cider-ede.31's own evidence). Defaults to a no-op so every implementation that has no separate
    /// sweep step to defer — the CLI transport, test fakes — keeps today's behaviour unchanged: the CLI
    /// transport's own <c>RemoveImageAsync</c> already reclaims a deleted image's blobs as an
    /// unavoidable side effect of the underlying <c>container image delete</c> process (Apple's own
    /// <c>ImageDelete.swift</c> sweeps inside that same one-shot invocation; there is no flag to skip
    /// it), so there is nothing left for this call to additionally do there.
    /// </summary>
    Task PruneImagesAsync(CancellationToken ct) => Task.CompletedTask;

    Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct);

    /// <summary>Loads a docker-save tarball; returns the references that were loaded.</summary>
    Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct);

    /// <summary>Builds an image and returns its id (<c>sha256:…</c>).</summary>
    Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct);

    Task LoginAsync(RegistryAuth auth, CancellationToken ct);

    // ---- networks ---------------------------------------------------------

    Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct);

    Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct);

    Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct);

    Task RemoveNetworkAsync(string name, CancellationToken ct);

    // ---- volumes ----------------------------------------------------------

    Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct);

    Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct);

    Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct);

    Task RemoveVolumeAsync(string name, bool force, CancellationToken ct);

    Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct);

    // ---- builder ------------------------------------------------------------

    /// <summary>Current state of the Apple builder VM (<c>container builder status</c>); <c>null</c>
    /// when no builder has ever been started on this machine.</summary>
    Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct);

    /// <summary>
    /// Starts the Apple builder VM (<c>container builder start</c>), tolerating "already running".
    /// <paramref name="cpus"/>/<paramref name="memoryBytes"/> are passed through as <c>-c</c>/<c>-m</c>
    /// only when set; <c>null</c> leaves Apple's own defaults (2 vCPU / 2 GiB) in place.
    /// </summary>
    Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct);

    /// <summary>
    /// Opens a raw duplex byte pipe to buildkitd: <c>container exec -i buildkit buildctl dial-stdio</c>.
    /// The caller must keep <see cref="IContainerProcess.Stderr"/> drained (it is not read here), must
    /// not call <see cref="IContainerProcess.CloseStdinAsync"/> while output is still expected, and
    /// disposing the returned process is what terminates the dial.
    /// </summary>
    Task<IContainerProcess> DialBuilderAsync(CancellationToken ct);
}
