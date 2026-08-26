using System.Collections.Concurrent;
using System.Net;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Logs;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>
/// Docker's container endpoints, emulated on top of <see cref="IContainerRuntime"/>: it owns the
/// records, the held init process of every running container, the log pump, the attach fan-out and
/// every event a container produces.
/// </summary>
public sealed partial class ContainerManager : IContainerCounts
{
    private readonly IContainerRuntime _runtime;
    private readonly IRecordStore<ContainerRecord> _store;
    private readonly LogStore _logs;
    private readonly EventBus _events;
    private readonly PortAllocator _ports;
    private readonly IPortPublisher _publisher;
    private readonly NameRegistry _names;
    private readonly IDnsForwarderService _dnsForwarder;
    private readonly ImageManager _images;
    private readonly NetworkManager _networks;
    private readonly VolumeManager _volumes;
    private readonly CiderOptions _options;
    private readonly ILogger<ContainerManager> _logger;

    private readonly ConcurrentDictionary<string, ContainerHandle> _handles = new(StringComparer.Ordinal);
    private readonly Lock _nameGate = new();
    private readonly HashSet<string> _pendingNames = new(StringComparer.Ordinal);
    private int _dnsWarningIssued;

    /// <summary>Creates the manager and registers its endpoint provider with the network manager.</summary>
    public ContainerManager(
        IContainerRuntime runtime,
        IRecordStore<ContainerRecord> store,
        LogStore logs,
        EventBus events,
        PortAllocator ports,
        IPortPublisher publisher,
        NameRegistry names,
        IDnsForwarderService dnsForwarder,
        ImageManager images,
        NetworkManager networks,
        VolumeManager volumes,
        CiderOptions options,
        ILogger<ContainerManager> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _dnsForwarder = dnsForwarder ?? throw new ArgumentNullException(nameof(dnsForwarder));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _networks = networks ?? throw new ArgumentNullException(nameof(networks));
        _volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _networks.SetContainerEndpoints(EndpointsForNetwork);
        _networks.SetContainerAttachments(this);
    }

    /// <summary>Raised after every lifecycle transition: <c>create</c>, <c>start</c>, <c>die</c>, <c>destroy</c>, <c>health_status: …</c>.</summary>
    public event Action<ContainerRecord, string>? StateChanged;

    /// <summary><c>true</c> when the container was created with a TTY.</summary>
    public bool IsTty(string id)
    {
        var record = _store.Get(id);
        return record?.Request.Tty ?? false;
    }

    /// <summary>Number of known containers, optionally restricted to one <c>State.Status</c>.</summary>
    public int Count(string? status = null)
    {
        var all = _store.GetAll();
        if (string.IsNullOrEmpty(status))
        {
            return all.Count;
        }

        return all.Count(record => string.Equals(record.State.Status, status, StringComparison.Ordinal));
    }

    /// <summary>Resolves by exact id, exact name, or unique id prefix.</summary>
    public Task<ContainerRecord> ResolveAsync(string idOrName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(idOrName));
    }

    private ContainerRecord Resolve(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            throw DockerErrors.NoSuchContainer(idOrName ?? "");
        }

        var key = idOrName.TrimStart('/');
        var all = _store.GetAll();

        foreach (var record in all)
        {
            if (string.Equals(record.Id, key, StringComparison.Ordinal))
            {
                return record;
            }
        }

        foreach (var record in all)
        {
            if (string.Equals(record.Name, key, StringComparison.Ordinal))
            {
                return record;
            }
        }

        if (DockerId.IsHexPrefix(key))
        {
            ContainerRecord? match = null;
            foreach (var record in all)
            {
                if (!record.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match is not null)
                {
                    throw DockerErrors.BadParameter($"Multiple IDs found with provided prefix: {key}");
                }

                match = record;
            }

            if (match is not null)
            {
                return match;
            }
        }

        throw DockerErrors.NoSuchContainer(idOrName);
    }

    private ContainerRecord? FindByName(string name)
    {
        foreach (var record in _store.GetAll())
        {
            if (string.Equals(record.Name, name, StringComparison.Ordinal))
            {
                return record;
            }
        }

        return null;
    }

    private ContainerHandle GetHandle(string id) => _handles.GetOrAdd(id, static key => new ContainerHandle(key));

    /// <summary>Label marking containers the daemon runs for itself (DNS forwarders); never surfaced to clients.</summary>
    public const string SystemLabel = "com.chillicream.cider.system";

    /// <summary>Transitional (rename to Cider): pre-rename <see cref="SystemLabel"/>; read, never written.</summary>
    public const string LegacySystemLabel = "com.apple-demon.system";

    /// <summary>Label Apple's own tooling stamps on the builder VM's plugin role.</summary>
    private const string AppleBuilderRoleLabel = "com.apple.container.resource.role";

    /// <summary>Label Apple's own tooling stamps on the builder VM's plugin identity.</summary>
    private const string AppleBuilderPluginLabel = "com.apple.container.plugin";

    /// <summary>Runtime id Apple's <c>container</c> CLI gives its builder VM.</summary>
    private const string AppleBuilderRuntimeId = "buildkit";

    /// <summary>
    /// <c>true</c> for a hidden container the daemon started for its own plumbing, or for Apple's
    /// builder VM (<c>buildkit</c>): it is the Apple CLI's own build cache, not a Docker container,
    /// and must never be adopted, listed or removable through the Docker API.
    /// </summary>
    public static bool IsSystemContainer(RuntimeContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (ContainerIdentity.TryReadLabel(container.Labels, SystemLabel, LegacySystemLabel, out _))
        {
            return true;
        }

        if (container.Labels.TryGetValue(AppleBuilderRoleLabel, out var role) &&
            string.Equals(role, "builder", StringComparison.Ordinal))
        {
            return true;
        }

        if (container.Labels.TryGetValue(AppleBuilderPluginLabel, out var plugin) &&
            string.Equals(plugin, "builder", StringComparison.Ordinal))
        {
            return true;
        }

        // Belt and braces: the builder's well-known runtime id, so long as it does not also carry
        // Cider's own labels (a user-created container could in principle be named "buildkit").
        if (string.Equals(container.RuntimeId, AppleBuilderRuntimeId, StringComparison.Ordinal) &&
            ContainerIdentity.ReadDockerId(container.Labels) is null)
        {
            return true;
        }

        return false;
    }

    /// <summary>Every known record (used by the state poller and the health monitor).</summary>
    internal IReadOnlyCollection<ContainerRecord> AllRecords() => _store.GetAll();

    /// <summary>Persists a record another component mutated.</summary>
    internal void PersistExternal(ContainerRecord record) => Persist(record);

    /// <summary><c>true</c> when this daemon holds the container's init process (so it sees the real exit).</summary>
    internal bool HasHeldProcess(string id) => _handles.TryGetValue(id, out var handle) && handle.Process is not null;

    /// <summary>
    /// Completes container <paramref name="id"/>'s pending `docker wait` (the <c>next-exit</c> and
    /// default <c>not-running</c> conditions) with <paramref name="exitCode"/>, for an adopted
    /// container -- one <see cref="StatePoller"/> observed transition to exited without the daemon
    /// ever holding its process. Defers to <c>HandleExitAsync</c> whenever a held process exists
    /// for the id: that process is the source of truth for the real exit code, so this is a no-op
    /// in that case, and also when no handle exists at all (nothing has ever waited on it). See
    /// <see cref="CompleteExitWait(ContainerHandle,int)"/> for the shared mechanism this and
    /// <c>HandleExitAsync</c> both go through (cider-ede.33).
    /// </summary>
    internal void CompleteExitWait(string id, int exitCode)
    {
        if (_handles.TryGetValue(id, out var handle) && handle.Process is null)
        {
            CompleteExitWait(handle, exitCode);
        }
    }

    /// <summary>Publishes a container event on behalf of another component.</summary>
    internal void PublishExternal(ContainerRecord record, string action, IReadOnlyDictionary<string, string>? attributes = null) =>
        Publish(record, action, attributes);

    /// <summary>Raises <see cref="StateChanged"/> on behalf of another component.</summary>
    internal void RaiseStateChangedExternal(ContainerRecord record, string action) => RaiseStateChanged(record, action);

    private void Persist(ContainerRecord record) => _store.Upsert(record.Id, record);

    private void Publish(ContainerRecord record, string action, IReadOnlyDictionary<string, string>? attributes = null) =>
        _events.Publish(DockerEvents.Container(action, record, attributes));

    private void RaiseStateChanged(ContainerRecord record, string action)
    {
        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(record, action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "state change handler for container {Container} ({Action}) failed", record.Id, action);
        }
    }

    /// <summary>Maps a runtime failure onto the Docker status code the client expects.</summary>
    internal static DockerApiException Translate(RuntimeException ex) => ex.ToDockerError();

    private IReadOnlyList<(string ContainerId, string ContainerName, EndpointSettings Endpoint)> EndpointsForNetwork(string dockerNetworkName)
    {
        var result = new List<(string, string, EndpointSettings)>();
        foreach (var record in _store.GetAll())
        {
            if (!record.State.Running)
            {
                continue;
            }

            if (record.Networks.TryGetValue(dockerNetworkName, out var endpoint))
            {
                result.Add((record.Id, record.Name, endpoint));
            }
        }

        return result;
    }

    /// <summary>Per-container runtime state that never outlives the process.</summary>
    private sealed class ContainerHandle(string id)
    {
        public string Id { get; } = id;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IContainerProcess? Process { get; set; }

        public ILogWriter? LogWriter { get; set; }

        public bool Tty { get; set; }

        public Task? ExitHandling { get; set; }

        public List<Task> Pumps { get; } = [];

        public List<ContainerAttachment> Attachments { get; } = [];

        public Lock AttachGate { get; } = new();

        private TaskCompletionSource<int> _nextExit = NewExit();

        public TaskCompletionSource<int> NextExit => Volatile.Read(ref _nextExit);

        /// <summary>Atomically swaps in a fresh TCS for the next run and returns the one being retired.</summary>
        public TaskCompletionSource<int> ExchangeNextExit() => Interlocked.Exchange(ref _nextExit, NewExit());

        public TaskCompletionSource<int> Removed { get; } = NewExit();

        public ContainerStats? PreviousSample { get; set; }

        public static TaskCompletionSource<int> NewExit() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
