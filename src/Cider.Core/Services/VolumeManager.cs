using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Core.Time;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>Docker volume operations: list/inspect/create/remove/prune.</summary>
public sealed class VolumeManager
{
    private readonly IContainerRuntime _runtime;
    private readonly IRecordStore<VolumeRecord> _store;
    private readonly EventBus _events;
    private readonly CiderOptions _options;
    private readonly ILogger<VolumeManager> _logger;

    public VolumeManager(IContainerRuntime runtime, IRecordStore<VolumeRecord> store, EventBus events, CiderOptions options, ILogger<VolumeManager> logger)
    {
        _runtime = runtime;
        _store = store;
        _events = events;
        _options = options;
        _logger = logger;
    }

    public Task<VolumeListResponse> ListAsync(Filters filters, CancellationToken ct)
    {
        var volumes = _store.GetAll()
            .Where(r => filters.MatchName(r.Name) && filters.MatchesLabels(r.Request.Labels))
            .Select(ToVolume)
            .ToList();
        return Task.FromResult(new VolumeListResponse { Volumes = volumes, Warnings = [] });
    }

    public Task<Volume> InspectAsync(string name, CancellationToken ct)
    {
        var record = _store.Get(name) ?? throw DockerErrors.NoSuchVolume(name);
        return Task.FromResult(ToVolume(record));
    }

    public async Task<Volume> CreateAsync(VolumeCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = string.IsNullOrEmpty(request.Name) ? DockerId.New() : request.Name;
        if (!Names.IsValidDockerName(name))
        {
            throw DockerErrors.BadParameter($"invalid volume name: \"{name}\"");
        }

        // Volumes here are host directories under the data dir: `local` is the only driver there is,
        // and it is the one every response reports (see ToVolume). Accepting some other name used to
        // return 201 and then quietly answer `"Driver":"local"`, so a client asking for `nfs` was
        // told it had an nfs volume. dockerd fails the plugin lookup instead, with
        // a 404 rather than a 400.
        if (!string.IsNullOrEmpty(request.Driver) && !string.Equals(request.Driver, "local", StringComparison.Ordinal))
        {
            throw DockerErrors.NoSuchVolumeDriver(name, request.Driver);
        }

        var existing = _store.Get(name);
        if (existing is not null)
        {
            return ToVolume(existing);
        }

        long? sizeBytes = null;
        if (request.DriverOpts.TryGetValue("size", out var sizeText) && TryParseSize(sizeText, out var parsedSize))
        {
            sizeBytes = parsedSize;
        }

        var spec = new VolumeSpec { Name = name, Labels = request.Labels, Options = request.DriverOpts, SizeBytes = sizeBytes };
        try
        {
            await _runtime.CreateVolumeAsync(spec, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        request.Name = name;
        var record = new VolumeRecord { Name = name, Request = request, Created = DateTimeOffset.UtcNow };
        _store.Upsert(name, record);
        _events.Publish(DockerEvents.Volume("create", name));

        return ToVolume(record);
    }

    public async Task RemoveAsync(string name, bool force, CancellationToken ct)
    {
        var record = _store.Get(name) ?? throw DockerErrors.NoSuchVolume(name);

        var containers = await _runtime.ListContainersAsync(ct).ConfigureAwait(false);
        var usedBy = containers.Where(c => c.Mounts.Any(m => m.Kind == MountKind.Volume && string.Equals(m.Source, name, StringComparison.Ordinal))).ToList();
        if (usedBy.Count > 0 && !force)
        {
            var ids = string.Join(" ", usedBy.Select(DisplayId));
            throw DockerErrors.Conflict($"remove {name}: volume is in use - [{ids}]");
        }

        try
        {
            await _runtime.RemoveVolumeAsync(name, force, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.Conflict)
        {
            throw DockerErrors.Conflict($"remove {name}: volume is in use - []");
        }
        catch (RuntimeException ex)
        {
            throw ex.ToDockerError();
        }

        _store.Delete(name);
        _events.Publish(DockerEvents.Volume("destroy", name));
    }

    public async Task<VolumePruneResponse> PruneAsync(Filters filters, CancellationToken ct)
    {
        // dockerd's acceptedPruneFilters (moby/volume/service/service.go) - note no `until` here.
        filters = (filters ?? Filters.Empty).Validate("label", "label!", "all");
        var containers = await _runtime.ListContainersAsync(ct).ConfigureAwait(false);
        var usedNames = containers
            .SelectMany(c => c.Mounts.Where(m => m.Kind == MountKind.Volume).Select(m => m.Source))
            .ToHashSet(StringComparer.Ordinal);

        var deleted = new List<string>();
        long space = 0;
        foreach (var record in _store.GetAll().ToList())
        {
            if (usedNames.Contains(record.Name))
            {
                continue;
            }

            if (!filters.MatchesLabels(record.Request.Labels))
            {
                continue;
            }

            RuntimeVolume? runtimeVolume = null;
            try
            {
                runtimeVolume = await _runtime.InspectVolumeAsync(record.Name, ct).ConfigureAwait(false);
            }
            catch (RuntimeException)
            {
            }

            try
            {
                await _runtime.RemoveVolumeAsync(record.Name, false, ct).ConfigureAwait(false);
            }
            catch (RuntimeException)
            {
                continue;
            }

            _store.Delete(record.Name);
            _events.Publish(DockerEvents.Volume("destroy", record.Name));
            deleted.Add(record.Name);
            space += runtimeVolume?.SizeBytes ?? 0;
        }

        return new VolumePruneResponse { VolumesDeleted = deleted, SpaceReclaimed = space };
    }

    public async Task<Volume> EnsureAsync(string name, IReadOnlyDictionary<string, string>? labels, CancellationToken ct)
    {
        var existing = _store.Get(name);
        if (existing is not null)
        {
            return ToVolume(existing);
        }

        var request = new VolumeCreateRequest
        {
            Name = name,
            Labels = labels is null ? [] : new Dictionary<string, string>(labels),
        };
        return await CreateAsync(request, ct).ConfigureAwait(false);
    }

    // ---- helpers ------------------------------------------------------

    private Volume ToVolume(VolumeRecord record) => new()
    {
        Name = record.Name,
        Driver = "local",
        Mountpoint = Path.Combine(_options.VolumesDir, record.Name, "_data"),
        CreatedAt = DockerTime.Format(record.Created),
        Labels = new Dictionary<string, string>(record.Request.Labels),
        Scope = "local",
        Options = new Dictionary<string, string>(record.Request.DriverOpts),
    };

    private static string DisplayId(RuntimeContainer container) =>
        ContainerIdentity.ReadDockerId(container.Labels) is { } dockerId ? DockerId.Short(dockerId) : container.RuntimeId;

    private static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return long.TryParse(text, out bytes);
    }
}
