namespace Cider.Core.Runtime;

/// <summary>Everything the runtime needs to create one container.</summary>
public sealed record ContainerSpec
{
    /// <summary>The engine-side id chosen by the daemon (a valid Apple container id).</summary>
    public required string RuntimeId { get; init; }

    /// <summary>Image reference to run.</summary>
    public required string Image { get; init; }

    public string? Platform { get; init; }
    public string? Entrypoint { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<string> Env { get; init; } = [];
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public bool Tty { get; init; }
    public bool OpenStdin { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];
    public IReadOnlyList<PortSpec> Ports { get; init; } = [];
    public IReadOnlyList<string> Networks { get; init; } = [];
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public IReadOnlyList<string> DnsSearch { get; init; } = [];
    public IReadOnlyList<string> DnsOptions { get; init; } = [];
    public double? Cpus { get; init; }
    public long? MemoryBytes { get; init; }
    public IReadOnlyList<string> CapAdd { get; init; } = [];
    public IReadOnlyList<string> CapDrop { get; init; } = [];
    public bool Privileged { get; init; }
    public bool ReadOnlyRootfs { get; init; }
    public long? ShmSizeBytes { get; init; }
    public bool Init { get; init; }
    public IReadOnlyList<UlimitSpec> Ulimits { get; init; } = [];
    public IReadOnlyList<TmpfsSpec> Tmpfs { get; init; } = [];
    public IReadOnlyList<string> PublishSockets { get; init; } = [];
    public string? Hostname { get; init; }

    /// <summary>Kernel parameters (Docker <c>--sysctl</c> / <c>HostConfig.Sysctls</c>).</summary>
    public IReadOnlyDictionary<string, string> Sysctls { get; init; } = new Dictionary<string, string>();

    /// <summary>The signal <c>docker stop</c> sends before falling back to <c>SIGKILL</c> (Docker
    /// <c>--stop-signal</c> / the image's own <c>StopSignal</c>); <c>null</c> means the engine's default.</summary>
    public string? StopSignal { get; init; }
}

/// <summary>The kinds of mount the runtime understands.</summary>
public enum MountKind
{
    Bind,
    Volume,
    Tmpfs,
}

/// <summary>One mount of a container.</summary>
public sealed record MountSpec
{
    public required MountKind Kind { get; init; }

    /// <summary>Host path for <see cref="MountKind.Bind"/>, volume name for <see cref="MountKind.Volume"/>.</summary>
    public string Source { get; init; } = "";

    public required string Target { get; init; }
    public bool ReadOnly { get; init; }
}

/// <summary>One published port.</summary>
public sealed record PortSpec
{
    public string HostIp { get; init; } = "";
    public int HostPort { get; init; }
    public required int ContainerPort { get; init; }
    public string Proto { get; init; } = "tcp";
}

/// <summary>One <c>--ulimit</c> entry.</summary>
public sealed record UlimitSpec
{
    public required string Name { get; init; }
    public long Soft { get; init; }
    public long Hard { get; init; }
}

/// <summary>One <c>--tmpfs</c> entry.</summary>
public sealed record TmpfsSpec
{
    public required string Target { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>Options for starting a container; stdout/stderr are always attached.</summary>
public sealed record StartOptions
{
    public bool AttachStdin { get; init; }
}

/// <summary>Everything the runtime needs to run one exec process.</summary>
public sealed record ExecSpec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public IReadOnlyList<string> Env { get; init; } = [];
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public bool Tty { get; init; }
    public bool OpenStdin { get; init; }
    public bool Privileged { get; init; }
}

/// <summary>A network to create.</summary>
public sealed record NetworkSpec
{
    public required string Name { get; init; }
    public string? Subnet { get; init; }
    public string? SubnetV6 { get; init; }
    public bool Internal { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}

/// <summary>A volume to create.</summary>
public sealed record VolumeSpec
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public long? SizeBytes { get; init; }
}

/// <summary>Everything the runtime needs to build one image.</summary>
public sealed record BuildSpec
{
    public required string ContextDir { get; init; }

    /// <summary>Dockerfile path relative to <see cref="ContextDir"/>.</summary>
    public string Dockerfile { get; init; } = "Dockerfile";

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> BuildArgs { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public string? Target { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public bool NoCache { get; init; }
    public bool Pull { get; init; }
    public bool Quiet { get; init; }
    public int? Cpus { get; init; }
    public long? MemoryBytes { get; init; }
}

/// <summary>Registry credentials handed to pull/push/login.</summary>
public sealed record RegistryAuth
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? ServerAddress { get; init; }
    public string? IdentityToken { get; init; }
}
