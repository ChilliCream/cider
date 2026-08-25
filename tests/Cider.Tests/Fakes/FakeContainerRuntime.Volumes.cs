using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

public sealed partial class FakeContainerRuntime
{
    private readonly List<RuntimeVolume> _volumes = new();

    /// <summary>Test hook: fails the next <see cref="ListVolumesAsync"/> with this error.</summary>
    public RuntimeException? ListVolumesFailure { get; set; }

    public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct)
    {
        Record("ListVolumesAsync");

        if (ListVolumesFailure is { } failure)
        {
            ListVolumesFailure = null;
            throw failure;
        }

        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<RuntimeVolume>>(_volumes.ToList());
        }
    }

    /// <summary>
    /// Test-only helper: drops a volume from <see cref="ListVolumesAsync"/>/
    /// <see cref="InspectVolumeAsync"/> without going through <see cref="RemoveVolumeAsync"/> — the
    /// way <c>container volume delete</c> run by hand against the Apple CLI leaves cider's record
    /// behind.
    /// </summary>
    public void VanishVolume(string name)
    {
        lock (_sync)
        {
            _volumes.RemoveAll(v => string.Equals(v.Name, name, StringComparison.Ordinal));
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
