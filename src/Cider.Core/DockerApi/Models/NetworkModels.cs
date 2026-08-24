namespace Cider.Core.DockerApi.Models;

/// <summary>One entry of <c>GET /networks</c> and the body of <c>GET /networks/{id}</c>.</summary>
public sealed class NetworkResource
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Created { get; set; } = "";
    public string Scope { get; set; } = "local";
    public string Driver { get; set; } = "bridge";
    public bool EnableIPv6 { get; set; }
    public Ipam IPAM { get; set; } = new();
    public bool Internal { get; set; }
    public bool Attachable { get; set; }
    public bool Ingress { get; set; }
    public ConfigReference? ConfigFrom { get; set; }
    public bool ConfigOnly { get; set; }
    public Dictionary<string, EndpointResource> Containers { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
    public List<PeerInfo>? Peers { get; set; }
}

/// <summary>IP address management block of a network.</summary>
public sealed class Ipam
{
    public string Driver { get; set; } = "default";
    public Dictionary<string, string> Options { get; set; } = [];
    public List<IpamConfig> Config { get; set; } = [];
}

/// <summary>One subnet entry of <see cref="Ipam"/>.</summary>
public sealed class IpamConfig
{
    public string? Subnet { get; set; }
    public string? IPRange { get; set; }
    public string? Gateway { get; set; }
    public Dictionary<string, string>? AuxiliaryAddresses { get; set; }
}

/// <summary><c>NetworkResource.ConfigFrom</c>.</summary>
public sealed class ConfigReference
{
    public string Network { get; set; } = "";
}

/// <summary>One entry of <c>NetworkResource.Containers</c>.</summary>
public sealed class EndpointResource
{
    public string Name { get; set; } = "";
    public string EndpointID { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string IPv4Address { get; set; } = "";
    public string IPv6Address { get; set; } = "";
}

/// <summary>One entry of <c>NetworkResource.Peers</c> (swarm only; unused here).</summary>
public sealed class PeerInfo
{
    public string Name { get; set; } = "";
    public string IP { get; set; } = "";
}

/// <summary>Body of <c>POST /networks/create</c>.</summary>
public sealed class NetworkCreateRequest
{
    public string Name { get; set; } = "";
    public bool? CheckDuplicate { get; set; }
    public string Driver { get; set; } = "bridge";
    public string Scope { get; set; } = "local";
    public bool Internal { get; set; }
    public bool Attachable { get; set; }
    public bool Ingress { get; set; }
    public bool ConfigOnly { get; set; }
    public ConfigReference? ConfigFrom { get; set; }
    public Ipam? IPAM { get; set; }
    public bool EnableIPv6 { get; set; }

    // `docker compose` always sends "Options": null (and often "Labels": null) for the implicit
    // per-project default network, so both setters coalesce — see the null policy note at the top
    // of DockerApi/Json/DockerJson.cs.
    public Dictionary<string, string> Options
    {
        get => _options;
        set => _options = value ?? [];
    }

    public Dictionary<string, string> Labels
    {
        get => _labels;
        set => _labels = value ?? [];
    }

    private Dictionary<string, string> _options = [];
    private Dictionary<string, string> _labels = [];
}

/// <summary><c>POST /networks/create</c> response.</summary>
public sealed class NetworkCreateResponse
{
    public string Id { get; set; } = "";
    public string Warning { get; set; } = "";
}

/// <summary>Body of <c>POST /networks/{id}/connect</c>.</summary>
public sealed class NetworkConnectRequest
{
    public string Container { get; set; } = "";
    public EndpointSettings? EndpointConfig { get; set; }
}

/// <summary>Body of <c>POST /networks/{id}/disconnect</c>.</summary>
public sealed class NetworkDisconnectRequest
{
    public string Container { get; set; } = "";
    public bool Force { get; set; }
}

/// <summary><c>POST /networks/prune</c>.</summary>
public sealed class NetworkPruneResponse
{
    public List<string> NetworksDeleted { get; set; } = [];
}
