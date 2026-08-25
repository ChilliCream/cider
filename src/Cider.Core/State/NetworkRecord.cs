using Cider.Core.DockerApi.Models;

namespace Cider.Core.State;

/// <summary>Persisted state for one Docker network (owner: core-resources).</summary>
public sealed class NetworkRecord
{
    /// <summary>Docker's 64-hex network id.</summary>
    public required string Id { get; set; }

    /// <summary>Docker network name (e.g. "bridge", or a user-chosen name).</summary>
    public required string Name { get; set; }

    /// <summary>The create request as received, after defaults were applied.</summary>
    public required NetworkCreateRequest Request { get; set; }

    public DateTimeOffset Created { get; set; }

    /// <summary>The name the underlying Apple <c>container</c> network is known by (e.g. "default" for "bridge").</summary>
    public string RuntimeName { get; set; } = "";

    /// <summary><c>false</c> for networks discovered on the engine that cider did not create.</summary>
    public bool Managed { get; set; } = true;
}
