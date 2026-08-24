using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

public sealed partial class FakeContainerRuntime
{
    private readonly List<RuntimeVolume> _volumes = new();

    public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct)
    {
        Record("ListVolumesAsync");
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<RuntimeVolume>>(_volumes.ToList());
        }
    }

    public Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct)
    {
        Record($"InspectVolumeAsync:{name}");
        lock (_sync)
        {
            return Task.FromResult(_volumes.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal)));
        }
    }

    public Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct)
    {
        Record($"CreateVolumeAsync:{spec.Name}");
        lock (_sync)
        {
            if (_volumes.Any(v => string.Equals(v.Name, spec.Name, StringComparison.Ordinal)))
            {
                return Task.CompletedTask;
            }

            _volumes.Add(new RuntimeVolume
            {
                Name = spec.Name,
                Driver = "local",
                Labels = spec.Labels,
                Options = spec.Options,
                Created = DateTimeOffset.UtcNow,
                SizeBytes = spec.SizeBytes,
            });
        }

        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, bool force, CancellationToken ct)
    {
        Record($"RemoveVolumeAsync:{name}:{force}");
        lock (_sync)
        {
            var volume = _volumes.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal))
                ?? throw RuntimeException.NotFound($"no such volume: {name}");

            if (!force && _containers.Any(c => c.Mounts.Any(m => m.Kind == MountKind.Volume && string.Equals(m.Source, name, StringComparison.Ordinal))))
            {
                throw RuntimeException.Conflict($"volume {name} is in use");
            }

            _volumes.Remove(volume);
        }

        return Task.CompletedTask;
    }
}
