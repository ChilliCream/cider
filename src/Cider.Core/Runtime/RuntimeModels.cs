namespace Cider.Core.Runtime;

/// <summary>Coarse lifecycle state as reported by the engine.</summary>
public enum RuntimeContainerState
{
    Created,
    Running,
    Stopping,
    Stopped,
    Unknown,
}

/// <summary>A container as the engine sees it.</summary>
public sealed record RuntimeContainer
{
    public required string RuntimeId { get; init; }
    public RuntimeContainerState State { get; init; } = RuntimeContainerState.Unknown;
    public string ImageReference { get; init; } = "";
    public string? ImageDigest { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<RuntimeNetworkAttachment> Networks { get; init; } = [];
    public IReadOnlyList<PortSpec> PublishedPorts { get; init; } = [];
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];
    public string? Platform { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = [];
    public IReadOnlyList<string> Env { get; init; } = [];
    public string? WorkingDir { get; init; }
    public bool Tty { get; init; }
    public double? Cpus { get; init; }
    public long? MemoryBytes { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>A container's presence on one network.</summary>
public sealed record RuntimeNetworkAttachment
{
    public required string Network { get; init; }
    public string? Hostname { get; init; }
    public string? IPv4Address { get; init; }
    public string? IPv4Gateway { get; init; }
    public string? MacAddress { get; init; }
}

/// <summary>An image as listed by the engine.</summary>
public record RuntimeImage
{
    /// <summary>Content id, <c>sha256:…</c>.</summary>
    public required string Id { get; init; }

    /// <summary>All references (name:tag / name@digest) that point at this image.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    public long Size { get; init; }
    public DateTimeOffset? Created { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>An image plus the configuration needed to derive container defaults.</summary>
public sealed record RuntimeImageDetail : RuntimeImage
{
    public ImageConfig Config { get; init; } = new();
    public string Architecture { get; init; } = "";
    public string Os { get; init; } = "";
    public string? Variant { get; init; }

    /// <summary>Layer diff ids.</summary>
    public IReadOnlyList<string> Layers { get; init; } = [];

    public string? Author { get; init; }
    public IReadOnlyList<string> RepoDigests { get; init; } = [];

    /// <summary>
    /// The image config's build history, oldest first — what <c>docker history</c> is made of.
    /// Empty when the engine carries none.
    /// </summary>
    public IReadOnlyList<RuntimeImageHistory> History { get; init; } = [];
}

/// <summary>One entry of an image config's <c>history</c> array.</summary>
public sealed record RuntimeImageHistory
{
    public DateTimeOffset? Created { get; init; }

    /// <summary>The build instruction, e.g. <c>CMD ["/bin/sh"]</c>.</summary>
    public string CreatedBy { get; init; } = "";

    public string Comment { get; init; } = "";

    /// <summary><c>true</c> for an instruction that produced no filesystem layer.</summary>
    public bool EmptyLayer { get; init; }
}

/// <summary>The OCI image config fields the daemon needs when creating a container.</summary>
public sealed record ImageConfig
{
    public IReadOnlyList<string> Env { get; init; } = [];
    public IReadOnlyList<string> Cmd { get; init; } = [];
    public IReadOnlyList<string> Entrypoint { get; init; } = [];
    public string? WorkingDir { get; init; }
    public string? User { get; init; }

    /// <summary>Exposed ports in <c>port/proto</c> form.</summary>
    public IReadOnlyList<string> ExposedPorts { get; init; } = [];

    /// <summary>Anonymous volume paths declared by the image.</summary>
    public IReadOnlyList<string> Volumes { get; init; } = [];

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public string? StopSignal { get; init; }
    public HealthcheckConfig? Healthcheck { get; init; }
}

/// <summary>An image's <c>HEALTHCHECK</c>; durations are nanoseconds like Docker.</summary>
public sealed record HealthcheckConfig
{
    public IReadOnlyList<string> Test { get; init; } = [];
    public long Interval { get; init; }
    public long Timeout { get; init; }
    public int Retries { get; init; }
    public long StartPeriod { get; init; }
}

/// <summary>A network as the engine sees it.</summary>
public sealed record RuntimeNetwork
{
    public required string Name { get; init; }
    public string? Id { get; init; }
    public string Mode { get; init; } = "nat";
    public string? Subnet { get; init; }
    public string? Gateway { get; init; }
    public string? SubnetV6 { get; init; }
    public bool Internal { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset? Created { get; init; }
}

/// <summary>A volume as the engine sees it.</summary>
public sealed record RuntimeVolume
{
    public required string Name { get; init; }
    public string Driver { get; init; } = "local";
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset? Created { get; init; }
    public string? Mountpoint { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>A single stats sample for one container.</summary>
public sealed record RuntimeStats
{
    public long MemoryUsageBytes { get; init; }
    public long MemoryLimitBytes { get; init; }
    public long CpuUsageUsec { get; init; }
    public long NetworkRxBytes { get; init; }
    public long NetworkTxBytes { get; init; }
    public long BlockReadBytes { get; init; }
    public long BlockWriteBytes { get; init; }
    public int NumProcesses { get; init; }
    public DateTimeOffset ReadAt { get; init; }
}

/// <summary>One progress line of a pull/push/build; maps 1:1 to Docker's <c>JsonMessage</c>.</summary>
public sealed record ProgressEvent
{
    public string? Status { get; init; }
    public string? Id { get; init; }
    public long? Current { get; init; }
    public long? Total { get; init; }
    public string? Stream { get; init; }
    public string? Error { get; init; }
}

/// <summary>Identity and readiness of the engine behind <see cref="IContainerRuntime"/>.</summary>
public sealed record RuntimeInfo
{
    public string Name { get; init; } = "apple-container";
    public string Version { get; init; } = "";
    public string? KernelVersion { get; init; }
    public bool Ready { get; init; }

    /// <summary>The engine's own data root, when it exposes one.</summary>
    public string? AppRoot { get; init; }
}

/// <summary>Rough disk accounting for <c>/system/df</c>.</summary>
public sealed record RuntimeDiskUsage
{
    public long ImagesBytes { get; init; }
    public long ContainersBytes { get; init; }
    public long VolumesBytes { get; init; }
    public long BuildCacheBytes { get; init; }
    public int ImagesCount { get; init; }
    public int ContainersCount { get; init; }
    public int VolumesCount { get; init; }
}
