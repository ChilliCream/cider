namespace Cider.Core.DockerApi.Models;

/// <summary>One entry of <c>GET /volumes</c> and the body of <c>GET /volumes/{name}</c>.</summary>
public sealed class Volume
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "local";
    public string Mountpoint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public Dictionary<string, object?> Status { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
    public string Scope { get; set; } = "local";
    public Dictionary<string, string> Options { get; set; } = [];
    public VolumeUsageData? UsageData { get; set; }
}

/// <summary><c>Volume.UsageData</c>.</summary>
public sealed class VolumeUsageData
{
    public long Size { get; set; } = -1;
    public long RefCount { get; set; } = -1;
}

/// <summary><c>GET /volumes</c>.</summary>
public sealed class VolumeListResponse
{
    public List<Volume> Volumes { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Body of <c>POST /volumes/create</c>.</summary>
public sealed class VolumeCreateRequest
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "local";

    // Same story as NetworkCreateRequest: real clients send explicit nulls here.
    public Dictionary<string, string> DriverOpts
    {
        get => _driverOpts;
        set => _driverOpts = value ?? [];
    }

    public Dictionary<string, string> Labels
    {
        get => _labels;
        set => _labels = value ?? [];
    }

    private Dictionary<string, string> _driverOpts = [];
    private Dictionary<string, string> _labels = [];
}

/// <summary><c>POST /volumes/prune</c>.</summary>
public sealed class VolumePruneResponse
{
    public List<string> VolumesDeleted { get; set; } = [];
    public long SpaceReclaimed { get; set; }
}
