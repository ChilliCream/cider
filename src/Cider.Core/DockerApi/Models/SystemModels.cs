using System.Text.Json.Serialization;

namespace Cider.Core.DockerApi.Models;

/// <summary>Values advertised by <c>GET/HEAD /_ping</c> (returned as headers, never serialized).
/// <c>BuilderVersion</c> is "2" when BuildKit is enabled (advertises the h2c /grpc + /session upgrade
/// path so docker/compose/buildx route builds to it) and "1" when disabled (falls back to legacy
/// <c>POST /build</c>). See <see cref="Cider.Core.Services.SystemManager.Ping"/>.</summary>
public sealed class PingInfo
{
    public string ApiVersion { get; set; } = "1.47";
    public string BuilderVersion { get; set; } = "1";
    public bool Experimental { get; set; }
    public string OsType { get; set; } = "linux";
    public string Swarm { get; set; } = "inactive";
}

/// <summary><c>GET /version</c>.</summary>
public sealed class VersionResponse
{
    public PlatformInfo Platform { get; set; } = new();
    public List<ComponentVersion> Components { get; set; } = [];
    public string Version { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string MinAPIVersion { get; set; } = "";
    public string GitCommit { get; set; } = "";
    public string GoVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string KernelVersion { get; set; } = "";
    public bool Experimental { get; set; }
    public string BuildTime { get; set; } = "";
}

/// <summary>One entry of <c>VersionResponse.Components</c>.</summary>
public sealed class ComponentVersion
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = [];
}

/// <summary><c>VersionResponse.Platform</c>.</summary>
public sealed class PlatformInfo
{
    public string Name { get; set; } = "";
}

/// <summary><c>GET /info</c>.</summary>
public sealed class SystemInfo
{
    public string ID { get; set; } = "";
    public int Containers { get; set; }
    public int ContainersRunning { get; set; }
    public int ContainersPaused { get; set; }
    public int ContainersStopped { get; set; }
    public int Images { get; set; }
    public string Driver { get; set; } = "apple-container";
    // Must stay empty: a [["driver-type","io.containerd.snapshotter.v1"]] entry makes buildx rewrite
    // `--load` to the oci exporter (buildx util/dockerutil/client.go:83-94), which cider does not want.
    public List<List<string>> DriverStatus { get; set; } = [];
    public string DockerRootDir { get; set; } = "";
    public PluginsInfo Plugins { get; set; } = new();
    public bool MemoryLimit { get; set; }
    public bool SwapLimit { get; set; }
    public bool KernelMemoryTCP { get; set; }
    public bool CpuCfsPeriod { get; set; }
    public bool CpuCfsQuota { get; set; }
    public bool CPUShares { get; set; }
    public bool CPUSet { get; set; }
    public bool PidsLimit { get; set; }
    public bool OomKillDisable { get; set; }
    public bool IPv4Forwarding { get; set; } = true;
    public bool BridgeNfIptables { get; set; }
    public bool BridgeNfIp6tables { get; set; }
    public bool Debug { get; set; }
    public int NFd { get; set; }
    public int NGoroutines { get; set; }
    public string SystemTime { get; set; } = "";
    public string LoggingDriver { get; set; } = "json-file";
    public string CgroupDriver { get; set; } = "none";
    public string CgroupVersion { get; set; } = "2";
    public int NEventsListener { get; set; }
    public string KernelVersion { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string OSVersion { get; set; } = "";
    public string OSType { get; set; } = "linux";
    public string Architecture { get; set; } = "aarch64";
    public int NCPU { get; set; }
    public long MemTotal { get; set; }
    public string IndexServerAddress { get; set; } = "https://index.docker.io/v1/";
    public RegistryServiceConfig RegistryConfig { get; set; } = new();
    public List<string> Labels { get; set; } = [];
    public bool ExperimentalBuild { get; set; }
    public string ServerVersion { get; set; } = "";
    public Dictionary<string, RuntimeInfo> Runtimes { get; set; } = [];
    public string DefaultRuntime { get; set; } = "apple-container";
    public SwarmInfo Swarm { get; set; } = new();
    public bool LiveRestoreEnabled { get; set; }
    public string Isolation { get; set; } = "";
    public string InitBinary { get; set; } = "";
    public string HttpProxy { get; set; } = "";
    public string HttpsProxy { get; set; } = "";
    public string NoProxy { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> SecurityOptions { get; set; } = [];
    public string? ProductLicense { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> DefaultAddressPools { get; set; } = [];
    public string? CDISpecDirs { get; set; }
}

/// <summary><c>SystemInfo.Plugins</c> — <c>Authorization</c> is <c>null</c> like Docker.</summary>
public sealed class PluginsInfo
{
    public List<string> Volume { get; set; } = [];
    public List<string> Network { get; set; } = [];
    public List<string>? Authorization { get; set; }
    public List<string> Log { get; set; } = [];
}

/// <summary><c>SystemInfo.RegistryConfig</c>.</summary>
public sealed class RegistryServiceConfig
{
    public List<string> AllowNondistributableArtifactsCIDRs { get; set; } = [];
    public List<string> AllowNondistributableArtifactsHostnames { get; set; } = [];
    public List<string> InsecureRegistryCIDRs { get; set; } = [];
    public Dictionary<string, IndexInfo> IndexConfigs { get; set; } = [];
    public List<string> Mirrors { get; set; } = [];
}

/// <summary>One entry of <c>RegistryServiceConfig.IndexConfigs</c>.</summary>
public sealed class IndexInfo
{
    public string Name { get; set; } = "";
    public List<string> Mirrors { get; set; } = [];
    public bool Secure { get; set; } = true;
    public bool Official { get; set; }
}

/// <summary><c>SystemInfo.Runtimes</c> value — Docker serializes this one in lowercase.</summary>
public sealed class RuntimeInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("runtimeArgs")]
    public List<string>? RuntimeArgs { get; set; }

    [JsonPropertyName("status")]
    public Dictionary<string, string>? Status { get; set; }
}

/// <summary><c>SystemInfo.Swarm</c> — always inactive for cider.</summary>
public sealed class SwarmInfo
{
    public string NodeID { get; set; } = "";
    public string NodeAddr { get; set; } = "";
    public string LocalNodeState { get; set; } = "inactive";
    public bool ControlAvailable { get; set; }
    public string Error { get; set; } = "";
    public List<string>? RemoteManagers { get; set; }
}

/// <summary>Registry credentials — Docker serializes these lowercase (<c>POST /auth</c>, <c>X-Registry-Auth</c>).</summary>
public sealed class AuthConfig
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("auth")]
    public string? Auth { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("serveraddress")]
    public string? ServerAddress { get; set; }

    [JsonPropertyName("identitytoken")]
    public string? IdentityToken { get; set; }

    [JsonPropertyName("registrytoken")]
    public string? RegistryToken { get; set; }
}

/// <summary><c>POST /auth</c> response.</summary>
public sealed class AuthResponse
{
    public string Status { get; set; } = "Login Succeeded";
    public string IdentityToken { get; set; } = "";
}

/// <summary><c>GET /system/df</c>.</summary>
public sealed class DiskUsage
{
    public long LayersSize { get; set; }
    public List<ImageSummary> Images { get; set; } = [];
    public List<ContainerSummary> Containers { get; set; } = [];
    public List<Volume> Volumes { get; set; } = [];
    public List<BuildCache> BuildCache { get; set; } = [];
    public long? BuilderSize { get; set; }
}

/// <summary>One entry of <c>DiskUsage.BuildCache</c>.</summary>
public sealed class BuildCache
{
    public string ID { get; set; } = "";
    public string Parent { get; set; } = "";
    public List<string> Parents { get; set; } = [];
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool InUse { get; set; }
    public bool Shared { get; set; }
    public long Size { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? LastUsedAt { get; set; }
    public int UsageCount { get; set; }
}

/// <summary>Docker's error envelope — the wire key is lowercase <c>message</c>.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
