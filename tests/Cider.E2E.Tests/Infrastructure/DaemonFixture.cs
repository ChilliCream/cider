using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Cider.Core.Configuration;
using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cider.E2E.Tests.Infrastructure;

/// <summary>
/// One in-process cider on a throwaway socket and data dir, driving the real Apple
/// <c>container</c> runtime, with the built-in DNS server on. Everything the E2E suite talks to goes
/// through the <c>docker</c> CLI pointed at this daemon's socket.
/// </summary>
public class DaemonFixture : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>Whether the suite may run at all (<c>CIDER_E2E=1</c> and a usable environment).</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E"), "1", StringComparison.Ordinal);

    /// <summary>Why the suite is skipped, or <c>null</c> when it runs.</summary>
    public static string? SkipReason => Enabled
        ? null
        : "set CIDER_E2E=1 to run the end-to-end suite against the real Apple container runtime";

    /// <summary>Which port-publishing mode the suite runs against (<c>proxy</c> or <c>apple</c>).</summary>
    public static string PortPublishingMode =>
        Environment.GetEnvironmentVariable("CIDER_PORT_PUBLISHING") is { Length: > 0 } mode
            ? mode
            : CiderOptions.ProxyPortPublishing;

    /// <summary><c>true</c> when the daemon under test hands <c>-p</c> to Apple instead of forwarding itself.</summary>
    public static bool AppleModePorts =>
        string.Equals(PortPublishingMode, CiderOptions.ApplePortPublishing, StringComparison.OrdinalIgnoreCase);

    /// <summary>The daemon's configuration (short socket path, temp data dir).</summary>
    public CiderOptions Options { get; private set; } = new();

    /// <summary>The value tests put into <c>DOCKER_HOST</c>.</summary>
    public string DockerHost => "unix://" + Options.SocketPath;

    /// <summary>A throwaway <c>DOCKER_CONFIG</c> so the developer's own config never leaks in.</summary>
    public string DockerConfigDir { get; private set; } = "";

    /// <summary>A scratch directory tests may create fixture files in; removed on dispose.</summary>
    public string ScratchDir { get; private set; } = "";

    /// <summary>Everything the daemon logged, newest last.</summary>
    public IReadOnlyList<string> DaemonLog => _log.ToArray();

    private readonly ConcurrentQueue<string> _log = new();

    // Snapshotted right after the daemon comes up, before any test runs. The in-process daemon
    // adopts every container Apple's runtime already knows about as a read-only record
    // (ContainerManager.Reconcile.cs), so `docker ps -aq` through it returns developer containers
    // the suite never created (e.g. a hand-started reference container, Apple's `buildkit` VM) right
    // alongside the suite's own. Teardown must never touch anything captured here.
    private readonly HashSet<string> _preExistingContainerIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preExistingNetworkNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preExistingVolumeNames = new(StringComparer.Ordinal);

    /// <summary>Overridable so the daemon-restart test can rebuild a daemon on the same data dir.</summary>
    protected virtual string InstanceSuffix => "";

    /// <summary>
    /// The state poller's interval, in seconds. Overridable so a test that specifically wants to
    /// exercise <c>POST /_cider/sync</c> (rather than the separate automatic poller-drop behaviour —
    /// see <see cref="Cider.Daemon.Routes.CiderRoutes"/>) can push it out far enough that the poller
    /// never races the assertion.
    /// </summary>
    protected virtual int PollIntervalOverride => 2;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var id = Guid.NewGuid().ToString("n")[..8] + InstanceSuffix;
        Options = new CiderOptions
        {
            // sockaddr_un.sun_path is 104 bytes on macOS: /tmp/cider-e2e-xxxxxxxx.sock is far below it.
            DataDir = $"/tmp/cider-e2e-{id}",
            SocketPath = $"/tmp/cider-e2e-{id}.sock",
            LogLevel = Environment.GetEnvironmentVariable("CIDER_E2E_LOGLEVEL") ?? "Information",
            PollIntervalSeconds = PollIntervalOverride,
            DnsEnabled = true,

            // `proxy` by default, like the real daemon; CIDER_PORT_PUBLISHING=apple runs the
            // suite against Apple's own `-p` forwarder instead.
            PortPublishing = PortPublishingMode,
        };

        DockerConfigDir = Path.Combine(Options.DataDir, "docker-config");
        ScratchDir = Path.Combine(Options.DataDir, "scratch");
        Directory.CreateDirectory(DockerConfigDir);
        Directory.CreateDirectory(ScratchDir);
        Options.EnsureDirectories();
        LinkCliPlugins();

        await StartDaemonAsync();
        await SnapshotPreExistingDockerObjectsAsync();
    }

    /// <summary>
    /// Records every container/network/volume the daemon already knows about right after startup
    /// (its own startup reconcile adopts whatever Apple's runtime already has, not just what this
    /// suite goes on to create), so teardown can tell "the suite's own" from "was already there"
    /// apart and never remove the latter.
    /// </summary>
    private async Task SnapshotPreExistingDockerObjectsAsync()
    {
        try
        {
            var containers = await DockerAsync(["ps", "-aq"], timeout: TimeSpan.FromSeconds(60));
            foreach (var id in containers.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _preExistingContainerIds.Add(id);
            }

            var networks = await DockerAsync(["network", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
            foreach (var network in networks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _preExistingNetworkNames.Add(network);
            }

            var volumes = await DockerAsync(["volume", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
            foreach (var volume in volumes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _preExistingVolumeNames.Add(volume);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Best-effort: if this fails, cleanup below still runs but with an empty snapshot, i.e.
            // no worse than before this fix. It is not worth failing fixture startup over.
        }
    }

    /// <summary>Builds and starts the daemon on the current options, then waits for <c>/_ping</c>.</summary>
    protected async Task StartDaemonAsync()
    {
        var app = DaemonHost.Create(Options, new DaemonHostSettings
        {
            DnsEnabled = true,
            ConfigureServices = services =>
                services.AddSingleton<ILoggerProvider>(new CollectingLoggerProvider(_log)),
        });

        await app.StartAsync();
        _app = app;
        await WaitForPingAsync();
    }

    /// <summary>Stops the daemon without touching the data dir (used by the restart test).</summary>
    protected async Task StopDaemonAsync()
    {
        if (_app is null)
        {
            return;
        }

        var app = _app;
        _app = null;
        try
        {
            await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        await app.DisposeAsync();
    }

    /// <summary>
    /// A throwaway <c>DOCKER_CONFIG</c> loses the user's <c>cli-plugins</c> directory, and with it
    /// <c>docker compose</c> and <c>docker buildx</c> ("unknown shorthand flag: 'p' in -p"), so the
    /// real one is symlinked in.
    /// </summary>
    private void LinkCliPlugins()
    {
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".docker",
            "cli-plugins");
        if (!Directory.Exists(source))
        {
            return;
        }

        try
        {
            var link = Path.Combine(DockerConfigDir, "cli-plugins");
            if (!Directory.Exists(link) && !File.Exists(link))
            {
                Directory.CreateSymbolicLink(link, source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs the <c>docker</c> CLI against this daemon.</summary>
    public Task<CommandResult> DockerAsync(params string[] arguments) => DockerAsync(arguments, null, null, null);

    /// <summary>Runs the <c>docker</c> CLI against this daemon, with stdin, a timeout and extra env.</summary>
    public Task<CommandResult> DockerAsync(
        IEnumerable<string> arguments,
        string? stdin = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        string? workingDirectory = null) =>
        Cmd.RunAsync("docker", arguments, BuildEnvironment(extraEnvironment), stdin, timeout, workingDirectory);

    /// <summary>Starts a long-running <c>docker</c> command (e.g. <c>docker events</c>).</summary>
    public BackgroundProcess DockerBackground(params string[] arguments) =>
        Cmd.Start("docker", arguments, BuildEnvironment(null));

    /// <summary>The environment every child gets: our socket, no context, a throwaway docker config.</summary>
    public IReadOnlyDictionary<string, string?> BuildEnvironment(IReadOnlyDictionary<string, string?>? extra)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = DockerHost,
            ["DOCKER_CONTEXT"] = null,
            ["DOCKER_CONFIG"] = DockerConfigDir,
            ["DOCKER_TLS_VERIFY"] = null,
            ["DOCKER_CERT_PATH"] = null,
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                env[key] = value;
            }
        }

        return env;
    }

    /// <summary>Polls <paramref name="probe"/> until it returns true or <paramref name="budget"/> runs out.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> probe, TimeSpan budget, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + budget;
        var step = interval ?? TimeSpan.FromMilliseconds(500);
        while (true)
        {
            if (await probe())
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(step);
        }
    }

    /// <summary>A short unique token for container/network/volume names.</summary>
    public static string NewName(string prefix) => $"e2e-{prefix}-{Guid.NewGuid():n}"[..(prefix.Length + 13)];

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await CleanupDockerObjectsAsync();
        await StopDaemonAsync();
        await CleanupForwarderAsync();
        RemoveDirectory(Options.DataDir);
        RemoveFile(Options.SocketPath);
    }

    /// <summary>
    /// Force-removes everything this suite created, so nothing outlives the run — but never anything
    /// that was already there when the fixture started (see <see cref="_preExistingContainerIds"/>).
    /// </summary>
    protected async Task CleanupDockerObjectsAsync()
    {
        try
        {
            var containers = await DockerAsync(["ps", "-aq"], timeout: TimeSpan.FromSeconds(60));
            var allIds = containers.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = allIds.Where(id => !_preExistingContainerIds.Contains(id)).ToArray();
            LogSkipped("container", allIds.Length - ids.Length);
            if (ids.Length > 0)
            {
                await DockerAsync(["rm", "-f", "-v", .. ids], timeout: TimeSpan.FromSeconds(180));
            }

            var volumes = await DockerAsync(["volume", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
            var allVolumes = volumes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var volumeNames = allVolumes.Where(name => !_preExistingVolumeNames.Contains(name)).ToArray();
            LogSkipped("volume", allVolumes.Length - volumeNames.Length);
            if (volumeNames.Length > 0)
            {
                await DockerAsync(["volume", "rm", "-f", .. volumeNames], timeout: TimeSpan.FromSeconds(60));
            }

            var networks = await DockerAsync(["network", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
            var allNetworks = networks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var skippedNetworks = 0;
            foreach (var network in allNetworks)
            {
                if (network is "bridge" or "host" or "none")
                {
                    continue;
                }

                if (_preExistingNetworkNames.Contains(network))
                {
                    skippedNetworks++;
                    continue;
                }

                await DockerAsync(["network", "rm", network], timeout: TimeSpan.FromSeconds(60));
            }

            LogSkipped("network", skippedNetworks);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    /// <summary>Notes in <see cref="DaemonLog"/> how many pre-existing objects teardown left alone.</summary>
    private void LogSkipped(string kind, int count)
    {
        if (count > 0)
        {
            _log.Enqueue(
                $"{DateTime.Now:HH:mm:ss.fff} Information DaemonFixture: teardown skipped {count} pre-existing " +
                $"{kind}(s) that existed before this fixture started");
        }
    }

    /// <summary>Deletes this instance's CoreDNS forwarder containers straight through the Apple CLI.</summary>
    private async Task CleanupForwarderAsync()
    {
        var hash = Cider.Daemon.Dns.DnsForwarderService.DataDirHash(Options.DataDir);
        try
        {
            var list = await Cmd.RunAsync("container", ["ls", "-a", "--format", "json"], timeout: TimeSpan.FromSeconds(60));
            if (!list.Ok || string.IsNullOrWhiteSpace(list.Stdout))
            {
                return;
            }

            using var document = JsonDocument.Parse(list.Stdout);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("configuration", out var configuration)
                    || !configuration.TryGetProperty("id", out var id)
                    || id.GetString() is not { } name
                    || !name.EndsWith("-" + hash, StringComparison.Ordinal))
                {
                    continue;
                }

                await Cmd.RunAsync("container", ["stop", name], timeout: TimeSpan.FromSeconds(60));
                await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(60));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
        }
    }

    private async Task WaitForPingAsync()
    {
        using var client = DaemonClient.Create(Options.SocketPath, TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/_ping", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"the E2E daemon never answered on {Options.SocketPath}\n" + string.Join('\n', DaemonLog));
    }

    private static void RemoveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, sink);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    line += " | " + exception.GetType().Name + ": " + exception.Message;
                }

                sink.Enqueue(line);
                while (sink.Count > 20000 && sink.TryDequeue(out _))
                {
                }
            }
        }
    }
}

/// <summary>The collection every E2E class joins so one daemon serves the whole run.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DaemonCollection : ICollectionFixture<DaemonFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "cider-e2e";
}
