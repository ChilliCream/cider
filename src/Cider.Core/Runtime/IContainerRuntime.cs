namespace Cider.Core.Runtime;

/// <summary>
/// The seam between cider's Docker emulation and the actual container engine.
/// Implemented by <c>Cider.AppleContainer.AppleContainerRuntime</c> (and by the test fake).
/// </summary>
public interface IContainerRuntime
{
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
}
