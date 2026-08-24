namespace Cider.Core.DockerApi.Models;

/// <summary>Docker's <c>container.Config</c>: the image-level configuration of a container.</summary>
public class ContainerConfig
{
    public string Hostname { get; set; } = "";
    public string Domainname { get; set; } = "";
    public string User { get; set; } = "";
    public bool AttachStdin { get; set; }
    public bool AttachStdout { get; set; }
    public bool AttachStderr { get; set; }
    // Setter coalesces null: some clients send "ExposedPorts": null (see DockerJson.cs).
    public Dictionary<string, EmptyStruct> ExposedPorts
    {
        get => _exposedPorts;
        set => _exposedPorts = value ?? [];
    }

    public bool Tty { get; set; }
    public bool OpenStdin { get; set; }
    public bool StdinOnce { get; set; }
    public List<string>? Env { get; set; }
    public List<string>? Cmd { get; set; }
    public HealthConfig? Healthcheck { get; set; }
    public bool ArgsEscaped { get; set; }
    public string Image { get; set; } = "";
    public Dictionary<string, EmptyStruct> Volumes
    {
        get => _volumes;
        set => _volumes = value ?? [];
    }

    public string WorkingDir { get; set; } = "";
    public List<string>? Entrypoint { get; set; }
    public bool NetworkDisabled { get; set; }
    public string? MacAddress { get; set; }
    public List<string>? OnBuild { get; set; }
    public Dictionary<string, string> Labels
    {
        get => _labels;
        set => _labels = value ?? [];
    }

    public string? StopSignal { get; set; }
    public int? StopTimeout { get; set; }
    public List<string>? Shell { get; set; }

    private Dictionary<string, EmptyStruct> _exposedPorts = [];
    private Dictionary<string, EmptyStruct> _volumes = [];
    private Dictionary<string, string> _labels = [];
}

/// <summary><c>ContainerConfig.Healthcheck</c>; durations are nanoseconds like Go's <c>time.Duration</c>.</summary>
public sealed class HealthConfig
{
    public List<string> Test { get; set; } = [];
    public long Interval { get; set; }
    public long Timeout { get; set; }
    public int Retries { get; set; }
    public long StartPeriod { get; set; }
    public long StartInterval { get; set; }
}

/// <summary>Body of <c>POST /containers/create</c>: container config fields plus <c>HostConfig</c>/<c>NetworkingConfig</c>.</summary>
public sealed class ContainerCreateRequest : ContainerConfig
{
    public HostConfig? HostConfig { get; set; }
    public NetworkingConfig? NetworkingConfig { get; set; }
}

/// <summary>Docker's <c>container.HostConfig</c>: the host-side knobs of a container.</summary>
public sealed class HostConfig
{
    public List<string>? Binds { get; set; }
    public string ContainerIDFile { get; set; } = "";
    public LogConfig LogConfig { get; set; } = new();
    public string NetworkMode { get; set; } = "";
    // Setter coalesces null: docker compose sends "PortBindings": null when nothing is published.
    public Dictionary<string, List<PortBinding>?> PortBindings
    {
        get => _portBindings;
        set => _portBindings = value ?? [];
    }

    public RestartPolicy RestartPolicy { get; set; } = new();
    public bool AutoRemove { get; set; }
    public string VolumeDriver { get; set; } = "";
    public List<string>? VolumesFrom { get; set; }
    public List<int> ConsoleSize { get; set; } = [0, 0];
    public Dictionary<string, string>? Annotations { get; set; }

    public List<string>? CapAdd { get; set; }
    public List<string>? CapDrop { get; set; }
    public string CgroupnsMode { get; set; } = "";
    public List<string>? Dns { get; set; }
    public List<string>? DnsOptions { get; set; }
    public List<string>? DnsSearch { get; set; }
    public List<string>? ExtraHosts { get; set; }
    public List<string>? GroupAdd { get; set; }
    public string IpcMode { get; set; } = "";
    public string Cgroup { get; set; } = "";
    public List<string>? Links { get; set; }
    public int OomScoreAdj { get; set; }
    public string PidMode { get; set; } = "";
    public bool Privileged { get; set; }
    public bool PublishAllPorts { get; set; }
    public bool ReadonlyRootfs { get; set; }
    public List<string>? SecurityOpt { get; set; }
    public Dictionary<string, string>? StorageOpt { get; set; }
    public Dictionary<string, string>? Tmpfs { get; set; }
    public string UTSMode { get; set; } = "";
    public string UsernsMode { get; set; } = "";
    public long ShmSize { get; set; }
    public Dictionary<string, string>? Sysctls { get; set; }
    public string Runtime { get; set; } = "";
    public string Isolation { get; set; } = "";

    // Resources
    public long CpuShares { get; set; }
    public long Memory { get; set; }
    public long NanoCpus { get; set; }
    public string CgroupParent { get; set; } = "";
    public ushort BlkioWeight { get; set; }
    public List<WeightDevice>? BlkioWeightDevice { get; set; }
    public List<ThrottleDevice>? BlkioDeviceReadBps { get; set; }
    public List<ThrottleDevice>? BlkioDeviceWriteBps { get; set; }
    public List<ThrottleDevice>? BlkioDeviceReadIOps { get; set; }
    public List<ThrottleDevice>? BlkioDeviceWriteIOps { get; set; }
    public long CpuPeriod { get; set; }
    public long CpuQuota { get; set; }
    public long CpuRealtimePeriod { get; set; }
    public long CpuRealtimeRuntime { get; set; }
    public string CpusetCpus { get; set; } = "";
    public string CpusetMems { get; set; } = "";
    public List<DeviceMapping>? Devices { get; set; }
    public List<string>? DeviceCgroupRules { get; set; }
    public List<DeviceRequest>? DeviceRequests { get; set; }
    public long MemoryReservation { get; set; }
    public long MemorySwap { get; set; }
    public long? MemorySwappiness { get; set; }
    public bool? OomKillDisable { get; set; }
    public long? PidsLimit { get; set; }
    public List<Ulimit>? Ulimits { get; set; }
    public long CpuCount { get; set; }
    public long CpuPercent { get; set; }
    public long IOMaximumIOps { get; set; }
    public long IOMaximumBandwidth { get; set; }

    public bool? Init { get; set; }
    public List<Mount>? Mounts { get; set; }
    public List<string>? MaskedPaths { get; set; }
    public List<string>? ReadonlyPaths { get; set; }

    /// <summary>
    /// Field-by-field copy that shares the collections. Used by read-only paths (inspect) that fill
    /// in daemon-side defaults for the response and must not write them into the stored request.
    /// </summary>
    public HostConfig ShallowCopy() => (HostConfig)MemberwiseClone();

    private Dictionary<string, List<PortBinding>?> _portBindings = [];
}

/// <summary><c>HostConfig.LogConfig</c>.</summary>
public sealed class LogConfig
{
    private Dictionary<string, string> _config = [];

    public string Type { get; set; } = "json-file";

    // Clients send "Config": null here routinely (docker compose does).
    public Dictionary<string, string> Config
    {
        get => _config;
        set => _config = value ?? [];
    }
}

/// <summary>One host-side binding of a published port.</summary>
public sealed class PortBinding
{
    public string HostIp { get; set; } = "";
    public string HostPort { get; set; } = "";
}

/// <summary><c>HostConfig.RestartPolicy</c>.</summary>
public sealed class RestartPolicy
{
    public string Name { get; set; } = "";
    public int MaximumRetryCount { get; set; }
}

/// <summary><c>HostConfig.Ulimits</c> entry.</summary>
public sealed class Ulimit
{
    public string Name { get; set; } = "";
    public long Soft { get; set; }
    public long Hard { get; set; }
}

/// <summary><c>HostConfig.Devices</c> entry.</summary>
public sealed class DeviceMapping
{
    public string PathOnHost { get; set; } = "";
    public string PathInContainer { get; set; } = "";
    public string CgroupPermissions { get; set; } = "";
}

/// <summary><c>HostConfig.DeviceRequests</c> entry (GPU-style requests).</summary>
public sealed class DeviceRequest
{
    public string Driver { get; set; } = "";
    public int Count { get; set; }
    public List<string> DeviceIDs { get; set; } = [];
    public List<List<string>> Capabilities { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
}

/// <summary>Blkio weight for one device.</summary>
public sealed class WeightDevice
{
    public string Path { get; set; } = "";
    public ushort Weight { get; set; }
}

/// <summary>Blkio throttle for one device.</summary>
public sealed class ThrottleDevice
{
    public string Path { get; set; } = "";
    public ulong Rate { get; set; }
}

/// <summary><c>HostConfig.Mounts</c> entry (Docker's <c>mount.Mount</c>).</summary>
public sealed class Mount
{
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string? Consistency { get; set; }
    public BindOptions? BindOptions { get; set; }
    public VolumeOptions? VolumeOptions { get; set; }
    public TmpfsOptions? TmpfsOptions { get; set; }
}

/// <summary><c>Mount.BindOptions</c>.</summary>
public sealed class BindOptions
{
    public string Propagation { get; set; } = "";
    public bool NonRecursive { get; set; }
    public bool CreateMountpoint { get; set; }
    public bool ReadOnlyNonRecursive { get; set; }
    public bool ReadOnlyForceRecursive { get; set; }
}

/// <summary><c>Mount.VolumeOptions</c>.</summary>
public sealed class VolumeOptions
{
    public bool NoCopy { get; set; }
    public Dictionary<string, string> Labels { get; set; } = [];
    public string? Subpath { get; set; }
    public VolumeDriverConfig? DriverConfig { get; set; }
}

/// <summary><c>VolumeOptions.DriverConfig</c>.</summary>
public sealed class VolumeDriverConfig
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = [];
}

/// <summary><c>Mount.TmpfsOptions</c>.</summary>
public sealed class TmpfsOptions
{
    public long SizeBytes { get; set; }
    public int Mode { get; set; }
    public List<List<string>>? Options { get; set; }
}

/// <summary><c>ContainerCreateRequest.NetworkingConfig</c>.</summary>
public sealed class NetworkingConfig
{
    public Dictionary<string, EndpointSettings> EndpointsConfig { get; set; } = [];
}

/// <summary>A container's attachment to one network.</summary>
public sealed class EndpointSettings
{
    public EndpointIPAMConfig? IPAMConfig { get; set; }
    public List<string>? Links { get; set; }
    public List<string>? Aliases { get; set; }
    public string? MacAddress { get; set; }
    public Dictionary<string, string>? DriverOpts { get; set; }
    public int GwPriority { get; set; }
    public string NetworkID { get; set; } = "";
    public string EndpointID { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public int IPPrefixLen { get; set; }
    public string IPv6Gateway { get; set; } = "";
    public string GlobalIPv6Address { get; set; } = "";
    public int GlobalIPv6PrefixLen { get; set; }
    public List<string>? DNSNames { get; set; }
}

/// <summary><c>EndpointSettings.IPAMConfig</c>.</summary>
public sealed class EndpointIPAMConfig
{
    public string? IPv4Address { get; set; }
    public string? IPv6Address { get; set; }
    public List<string>? LinkLocalIPs { get; set; }
}
