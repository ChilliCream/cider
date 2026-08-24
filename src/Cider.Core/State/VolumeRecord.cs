using Cider.Core.DockerApi.Models;

namespace Cider.Core.State;

/// <summary>Persisted state for one Docker volume (owner: core-resources).</summary>
public sealed class VolumeRecord
{
    public required string Name { get; set; }

    public VolumeCreateRequest Request { get; set; } = new();

    public DateTimeOffset Created { get; set; }
}
