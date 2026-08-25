using System.Globalization;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.Events;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

/// <summary>
/// Watches the engine for containers that started, stopped or vanished behind the daemon's back
/// (someone ran <c>container stop</c> directly) and brings the records and events back in line.
/// </summary>
public sealed class StatePoller : IAsyncDisposable
{
    /// <summary>Default cadence when nobody set <see cref="CiderOptions.PollIntervalSeconds"/>
    /// explicitly and the runtime is CLI-backed — a ~19 ms spawn each pass makes anything tighter
    /// mostly wasted work (docs/spikes/xpc/04-dotnet-xpc-probe-report.md). Matches
    /// <see cref="CiderOptions"/>'s own constructor default, unchanged from before task cider-ede.19.</summary>
    private const int CliDefaultPollIntervalSeconds = 3;

    /// <summary>Default cadence when nobody set <see cref="CiderOptions.PollIntervalSeconds"/>
    /// explicitly and the runtime is XPC — a pass costs ~0.1 ms there
    /// (docs/spikes/xpc/04-dotnet-xpc-probe-report.md), so the 3 s CLI-era default only added
    /// latency to exit detection, adoption and <c>docker events</c> (task cider-ede.19's problem
    /// statement).</summary>
    private const int XpcDefaultPollIntervalSeconds = 1;

    private readonly ContainerManager _containers;
    private readonly IContainerRuntime _runtime;
    private readonly EventBus _events;
    private readonly CiderOptions _options;
    private readonly ILogger<StatePoller> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Consecutive polls a record's runtime container has been missing from `container ls -a`.
    // Reset the moment it is seen again; a single miss stays today's "mark exited" (an incomplete
    // listing right after the daemon or Apple's services restart must never drop a record), and only
    // the second consecutive miss is treated as "removed outside cider".
    private Dictionary<string, int> _missCounts = new(StringComparer.Ordinal);

    /// <summary>Creates the poller.</summary>
    public StatePoller(
        ContainerManager containers,
        IContainerRuntime runtime,
        EventBus events,
        CiderOptions options,
        ILogger<StatePoller> logger)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var defaultSeconds = runtime.IsXpcTransport ? XpcDefaultPollIntervalSeconds : CliDefaultPollIntervalSeconds;
        var effectiveSeconds = options.PollIntervalSecondsIsExplicit ? options.PollIntervalSeconds : defaultSeconds;
        Interval = TimeSpan.FromSeconds(Math.Max(effectiveSeconds, 1));
    }

    /// <summary>How often the engine is polled while nobody watches <c>/events</c>.</summary>
    public TimeSpan Interval { get; set; }

    /// <summary>How often the engine is polled while at least one client watches <c>/events</c>.</summary>
    public TimeSpan FastInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Starts the background loop.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "state poller: interval {IntervalSeconds}s ({Source}, transport {Transport}), fast interval {FastIntervalSeconds}s",
            Interval.TotalSeconds,
            _options.PollIntervalSecondsIsExplicit ? "configured" : "default",
            _runtime.IsXpcTransport ? "xpc" : "cli",
            FastInterval.TotalSeconds);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the background loop and waits for it.</summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _loop = null;
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Runs one reconciliation pass; exposed so tests do not have to wait for a tick.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<RuntimeContainer> runtimeContainers;
        try
        {
            runtimeContainers = await _runtime.ListContainersAsync(ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "state poll could not list engine containers");
            return;
        }

        var byRuntimeId = new Dictionary<string, RuntimeContainer>(StringComparer.Ordinal);
        foreach (var container in runtimeContainers)
        {
            // The daemon's own hidden containers (DNS forwarders) are not Docker containers.
            if (ContainerManager.IsSystemContainer(container))
            {
                continue;
            }

            byRuntimeId[container.RuntimeId] = container;
        }

        var missCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var record in EnumerateRecords())
        {
            if (!byRuntimeId.TryGetValue(record.RuntimeId, out var runtimeContainer))
            {
                var misses = _missCounts.TryGetValue(record.Id, out var previous) ? previous + 1 : 1;

                if (misses == 1)
                {
                    if (record.State.Running)
                    {
                        record.State.Status = "exited";
                        record.State.FinishedAt ??= DateTimeOffset.UtcNow;
                        record.State.Error = "exit code unknown (daemon restarted)";
                        Save(record);
                        _events.Publish(DockerEvents.Container("die", record, new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["exitCode"] = record.State.ExitCode.ToString(CultureInfo.InvariantCulture),
                        }));
                    }

                    missCounts[record.Id] = misses;
                    continue;
                }

                // The daemon is holding this container's init process (a `container start -a` it
                // launched), so the runtime not listing it yet is a transient gap, not a removal.
                if (IsHeldByUs(record.Id))
                {
                    missCounts[record.Id] = misses;
                    continue;
                }

                _logger.LogWarning(
                    "container {Name} ({Id}) no longer exists in Apple container (removed outside cider); dropping its record",
                    record.Name, record.Id);

                try
                {
                    await _containers.ForgetVanishedAsync(record, ct);
                }
                catch (Exception ex) when (ex is DockerApiException or RuntimeException)
                {
                    _logger.LogDebug(ex, "dropping vanished container {Container} failed", record.Id);
                    missCounts[record.Id] = misses;
                }

                continue;
            }

            var running = runtimeContainer.State == RuntimeContainerState.Running;
            if (running && !record.State.Running)
            {
                record.State.Status = "running";
                record.State.StartedAt ??= runtimeContainer.StartedAt ?? DateTimeOffset.UtcNow;
                Save(record);
                _events.Publish(DockerEvents.Container("start", record));
            }
            else if (!running && record.State.Running && !IsHeldByUs(record.Id))
            {
                record.State.Status = "exited";
                record.State.FinishedAt = DateTimeOffset.UtcNow;
                Save(record);
                _events.Publish(DockerEvents.Container("die", record, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exitCode"] = record.State.ExitCode.ToString(CultureInfo.InvariantCulture),
                }));
            }

            // Belt and braces for the startup network race (ARCHITECTURE §6/§9): if
            // ContainerManager.StartAsync gave up waiting for the address before Apple reported one,
            // keep retrying here on every tick until it shows up.
            if (running && record.State.Running && HasUnresolvedAddress(record))
            {
                try
                {
                    await _containers.RefreshNetworkInfoAsync(record, ct);
                }
                catch (RuntimeException ex)
                {
                    _logger.LogDebug(ex, "network refresh for container {Container} failed", record.Id);
                }
            }

            // Same idea for the daemon's own port forwarders: a container that is running and has
            // bindings but nothing published yet (address learned late, daemon restarted) gets them
            // here. Cheap and idempotent when everything is already up.
            if (running && record.State.Running)
            {
                try
                {
                    await _containers.EnsurePublishedPortsAsync(record, ct);
                }
                catch (Exception ex) when (ex is RuntimeException or IOException)
                {
                    _logger.LogDebug(ex, "publishing ports of container {Container} failed", record.Id);
                }
            }
            else if (!running)
            {
                _containers.UnpublishPorts(record.Id);
            }
        }

        _missCounts = missCounts;
    }

    private static bool HasUnresolvedAddress(State.ContainerRecord record) =>
        record.Networks.Count > 0 && record.Networks.Values.Any(endpoint => string.IsNullOrEmpty(endpoint.IPAddress));

    private IEnumerable<State.ContainerRecord> EnumerateRecords() => _containers.AllRecords();

    private void Save(State.ContainerRecord record) => _containers.PersistExternal(record);

    private bool IsHeldByUs(string id) => _containers.HasHeldProcess(id);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delay = _events.SubscriberCount > 0 ? FastInterval : Interval;
                await Task.Delay(delay, ct);
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "state poll failed");
            }
        }
    }
}
