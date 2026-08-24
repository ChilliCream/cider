using Cider.Core.Configuration;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Logs;
using Cider.Core.Net;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

/// <summary>Wires a <see cref="ContainerManager"/> up against the fake engine in a throwaway data dir.</summary>
public sealed class ContainerTestHarness : IAsyncDisposable
{
    private ContainerTestHarness(CiderOptions options)
    {
        Options = options;
        Runtime = new FakeContainerRuntime();
        Events = new EventBus();
        Ports = new PortAllocator();
        Publisher = options.UseProxyPortPublishing ? new RecordingPortPublisher() : new RecordingPortPublisher(enabled: false);
        NameRegistry = new NameRegistry();
        Dns = new FakeDnsForwarder();
        Logs = new LogStore(options.LogsDir, options.LogMaxBytes);
        Store = new JsonFileStore<ContainerRecord>(Path.Combine(options.StateDir, "containers"));

        Images = new ImageManager(Runtime, Events, options, NullLogger<ImageManager>.Instance);
        Networks = new NetworkManager(Runtime, new InMemoryRecordStore<NetworkRecord>(), Events, NullLogger<NetworkManager>.Instance);
        Volumes = new VolumeManager(Runtime, new InMemoryRecordStore<VolumeRecord>(), Events, options, NullLogger<VolumeManager>.Instance);

        Containers = new ContainerManager(
            Runtime, Store, Logs, Events, Ports, Publisher, NameRegistry, Dns, Images, Networks, Volumes, options,
            NullLogger<ContainerManager>.Instance);
        Execs = new ExecManager(Runtime, Containers, Events, NullLogger<ExecManager>.Instance);
    }

    /// <summary>Creates a harness with a fresh temporary data directory.</summary>
    public static async Task<ContainerTestHarness> CreateAsync(Action<CiderOptions>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cider-tests", Guid.NewGuid().ToString("n")[..12]);
        var options = new CiderOptions { DataDir = root };
        configure?.Invoke(options);
        options.EnsureDirectories();

        var harness = new ContainerTestHarness(options);
        await harness.Networks.EnsureDefaultAsync(CancellationToken.None);
        return harness;
    }

    public CiderOptions Options { get; }

    public FakeContainerRuntime Runtime { get; }

    public EventBus Events { get; }

    public PortAllocator Ports { get; }

    /// <summary>The port publisher the manager was given (recording, never binding a real socket).</summary>
    public RecordingPortPublisher Publisher { get; }

    public NameRegistry NameRegistry { get; }

    public FakeDnsForwarder Dns { get; }

    public LogStore Logs { get; }

    public JsonFileStore<ContainerRecord> Store { get; }

    public ImageManager Images { get; }

    public NetworkManager Networks { get; }

    public VolumeManager Volumes { get; }

    public ContainerManager Containers { get; }

    public ExecManager Execs { get; }

    /// <summary>Creates a container and returns its record.</summary>
    public async Task<ContainerRecord> CreateAsync(
        string image = "alpine",
        string? name = null,
        Action<ContainerCreateRequest>? configure = null,
        CancellationToken ct = default)
    {
        var request = new ContainerCreateRequest { Image = image };
        configure?.Invoke(request);
        var response = await Containers.CreateAsync(request, name, platform: null, ct);
        return await Containers.ResolveAsync(response.Id, ct);
    }

    /// <summary>Creates a container running <c>sh -c &lt;script&gt;</c>.</summary>
    public Task<ContainerRecord> CreateShellAsync(string script, string? name = null, Action<ContainerCreateRequest>? configure = null) =>
        CreateAsync("alpine", name, request =>
        {
            request.Cmd = ["sh", "-c", script];
            configure?.Invoke(request);
        });

    /// <summary>Creates and starts a container running <c>sh -c &lt;script&gt;</c>.</summary>
    public async Task<ContainerRecord> RunShellAsync(string script, string? name = null, Action<ContainerCreateRequest>? configure = null)
    {
        var record = await CreateShellAsync(script, name, configure);
        await Containers.StartAsync(record.Id, CancellationToken.None);
        return record;
    }

    /// <summary>Starts collecting published events; the subscription is live when the task completes.</summary>
    public async Task<EventCollector> CollectEventsAsync()
    {
        var collector = new EventCollector(Events);
        await collector.StartAsync();
        return collector;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, polling on a short interval.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"timed out waiting for: {because}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var record in Store.GetAll())
        {
            try
            {
                await Containers.RemoveAsync(record.Id, force: true, removeVolumes: false, CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort cleanup; the temp directory goes away regardless.
            }
        }

        try
        {
            if (Directory.Exists(Options.DataDir))
            {
                Directory.Delete(Options.DataDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Collects everything the bus publishes while it is alive.</summary>
    public sealed class EventCollector : IAsyncDisposable
    {
        private readonly EventBus _bus;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly System.Collections.Concurrent.ConcurrentQueue<EventMessage> _messages = new();
        private Task? _loop;

        internal EventCollector(EventBus bus) => _bus = bus;

        /// <summary>Everything published since the collector started, in order.</summary>
        public IReadOnlyCollection<EventMessage> Messages => _messages;

        /// <summary>The actions of every collected event, in order.</summary>
        public IReadOnlyList<string> Actions => [.. _messages.Select(message => message.Action)];

        internal async Task StartAsync()
        {
            var before = _bus.SubscriberCount;
            _loop = Task.Run(async () =>
            {
                await foreach (var message in _bus.Subscribe(Core.DockerApi.Filters.Empty, null, null, _cts.Token))
                {
                    _messages.Enqueue(message);
                }
            });

            await WaitUntilAsync(() => _bus.SubscriberCount > before, "the event subscription to register");
        }

        /// <summary>Waits for an event with this action to arrive.</summary>
        public Task WaitForAsync(string action, int timeoutMs = 5000) =>
            WaitUntilAsync(() => _messages.Any(message => string.Equals(message.Action, action, StringComparison.Ordinal)),
                $"event '{action}'", timeoutMs);

        /// <summary>The first event with this action.</summary>
        public EventMessage First(string action) =>
            _messages.First(message => string.Equals(message.Action, action, StringComparison.Ordinal));

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
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
            }

            _cts.Dispose();
        }
    }
}
