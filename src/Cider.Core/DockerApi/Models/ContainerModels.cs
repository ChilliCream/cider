using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary>One entry of <c>GET /containers/json</c>.</summary>
public sealed class ContainerSummary
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public string ImageID { get; set; } = "";
    public string Command { get; set; } = "";
    public long Created { get; set; }
    public List<Port> Ports { get; set; } = [];
    public long? SizeRw { get; set; }
    public long? SizeRootFs { get; set; }
    public Dictionary<string, string> Labels { get; set; } = [];
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public SummaryHostConfig HostConfig { get; set; } = new();
    public NetworkSettingsSummary NetworkSettings { get; set; } = new();
    public List<MountPoint> Mounts { get; set; } = [];
}

/// <summary>One published port of a container summary.</summary>
public sealed class Port
{
    public string? IP { get; set; }
    public int PrivatePort { get; set; }
    public int? PublicPort { get; set; }
    public string Type { get; set; } = "tcp";
}

/// <summary>The trimmed <c>HostConfig</c> Docker puts on <c>ContainerSummary</c>.</summary>
public sealed class SummaryHostConfig
{
    public string NetworkMode { get; set; } = "";
    public Dictionary<string, string>? Annotations { get; set; }
}

/// <summary>The trimmed <c>NetworkSettings</c> Docker puts on <c>ContainerSummary</c>.</summary>
public sealed class NetworkSettingsSummary
{
    public Dictionary<string, EndpointSettings> Networks { get; set; } = [];
}

/// <summary><c>POST /containers/create</c> response.</summary>
public sealed class ContainerCreateResponse
{
    public string Id { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
}

/// <summary><c>GET /containers/{id}/json</c>.</summary>
public sealed class ContainerInspectResponse
{
    public string Id { get; set; } = "";
    public string Created { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public ContainerInspectState State { get; set; } = new();
    public string Image { get; set; } = "";
    public string ResolvConfPath { get; set; } = "";
    public string HostnamePath { get; set; } = "";
    public string HostsPath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string Name { get; set; } = "";
    public int RestartCount { get; set; }
    public string Driver { get; set; } = "apple-container";
    public string Platform { get; set; } = "linux";
    public string MountLabel { get; set; } = "";
    public string ProcessLabel { get; set; } = "";
    public string AppArmorProfile { get; set; } = "";
    public List<string> ExecIDs { get; set; } = [];
    public HostConfig HostConfig { get; set; } = new();
    public GraphDriverData GraphDriver { get; set; } = new();
    public long? SizeRw { get; set; }
    public long? SizeRootFs { get; set; }
    public List<MountPoint> Mounts { get; set; } = [];
    public ContainerConfig Config { get; set; } = new();
    public NetworkSettings NetworkSettings { get; set; } = new();
}

/// <summary><c>ContainerInspectResponse.State</c> (Docker's <c>container.State</c>).</summary>
public sealed class ContainerInspectState
{
    public string Status { get; set; } = "created";
    public bool Running { get; set; }
    public bool Paused { get; set; }
    public bool Restarting { get; set; }
    public bool OOMKilled { get; set; }
    public bool Dead { get; set; }
    public int Pid { get; set; }
    public int ExitCode { get; set; }
    public string Error { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public Health? Health { get; set; }
}

/// <summary><c>ContainerInspectState.Health</c>.</summary>
public sealed class Health
{
    public string Status { get; set; } = "";
    public int FailingStreak { get; set; }
    public List<HealthcheckResult> Log { get; set; } = [];
}

/// <summary>One probe result in <c>Health.Log</c>.</summary>
public sealed class HealthcheckResult
{
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
}

/// <summary><c>ContainerInspectResponse.GraphDriver</c>.</summary>
public sealed class GraphDriverData
{
    public string Name { get; set; } = "apple-container";
    public Dictionary<string, string> Data { get; set; } = [];
}

/// <summary>A resolved mount as reported on inspect/list.</summary>
public sealed class MountPoint
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool RW { get; set; }
    public string Propagation { get; set; } = "";
}

/// <summary><c>ContainerInspectResponse.NetworkSettings</c>.</summary>
public sealed class NetworkSettings
{
    public string Bridge { get; set; } = "";
    public string SandboxID { get; set; } = "";
    public string SandboxKey { get; set; } = "";
    public Dictionary<string, List<PortBinding>?> Ports { get; set; } = [];
    public bool HairpinMode { get; set; }
    public string LinkLocalIPv6Address { get; set; } = "";
    public int LinkLocalIPv6PrefixLen { get; set; }
    public List<Dictionary<string, object?>>? SecondaryIPAddresses { get; set; }
    public List<Dictionary<string, object?>>? SecondaryIPv6Addresses { get; set; }
    public string EndpointID { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string GlobalIPv6Address { get; set; } = "";
    public int GlobalIPv6PrefixLen { get; set; }
    public string IPAddress { get; set; } = "";
    public int IPPrefixLen { get; set; }
    public string IPv6Gateway { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public Dictionary<string, EndpointSettings> Networks { get; set; } = [];
}

/// <summary><c>POST /containers/{id}/wait</c> response.</summary>
public sealed class ContainerWaitResponse
{
    public long StatusCode { get; set; }
    public ContainerWaitExitError? Error { get; set; }
}

/// <summary><c>ContainerWaitResponse.Error</c>.</summary>
public sealed class ContainerWaitExitError
{
    public string Message { get; set; } = "";
}

/// <summary><c>GET /containers/{id}/top</c>.</summary>
public sealed class ContainerTopResponse
{
    public List<string> Titles { get; set; } = [];
    public List<List<string>> Processes { get; set; } = [];
}

/// <summary><c>POST /containers/{id}/update</c> body.</summary>
public sealed class ContainerUpdateRequest
{
    public long CpuShares { get; set; }
    public long Memory { get; set; }
    public string CgroupParent { get; set; } = "";
    public long CpuPeriod { get; set; }
    public long CpuQuota { get; set; }
    public long CpuRealtimePeriod { get; set; }
    public long CpuRealtimeRuntime { get; set; }
    public string CpusetCpus { get; set; } = "";
    public string CpusetMems { get; set; } = "";
    public long MemoryReservation { get; set; }
    public long MemorySwap { get; set; }
    public long? MemorySwappiness { get; set; }
    public long NanoCpus { get; set; }
    public long? PidsLimit { get; set; }
    public List<Ulimit>? Ulimits { get; set; }
    public RestartPolicy? RestartPolicy { get; set; }
}

/// <summary><c>POST /containers/{id}/update</c> response.</summary>
public sealed class ContainerUpdateResponse
{
    public List<string> Warnings { get; set; } = [];
}

/// <summary><c>POST /containers/prune</c>.</summary>
public sealed class ContainerPruneResponse
{
    public List<string> ContainersDeleted { get; set; } = [];
    public long SpaceReclaimed { get; set; }
}

/// <summary>Body of the <c>X-Docker-Container-Path-Stat</c> header (lowercase keys).</summary>
public sealed class ContainerPathStat
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mode")]
    public uint Mode { get; set; }

    [JsonPropertyName("mtime")]
    public string Mtime { get; set; } = "";

    [JsonPropertyName("linkTarget")]
    public string LinkTarget { get; set; } = "";
}
