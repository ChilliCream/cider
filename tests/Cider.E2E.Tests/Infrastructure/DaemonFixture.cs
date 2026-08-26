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

    /// <summary>
    /// Which runtime transport the suite requests (<see cref="CiderOptions.AutoRuntimeTransport"/>,
    /// <see cref="CiderOptions.XpcRuntimeTransport"/>, or <see cref="CiderOptions.CliRuntimeTransport"/>),
    /// read straight from <c>CIDER_RUNTIME_TRANSPORT</c> the same way <see cref="PortPublishingMode"/>
    /// reads <c>CIDER_PORT_PUBLISHING</c> — this is what CI's transport matrix sets to run the suite
    /// once per transport. Left unset (the default), the daemon under test decides for itself
    /// (<c>RuntimeTransportSelector</c>: ping the apiserver and fall back below the version gate).
    /// </summary>
    public static string Transport =>
        Environment.GetEnvironmentVariable("CIDER_RUNTIME_TRANSPORT") is { Length: > 0 } transport
            ? transport
            : CiderOptions.AutoRuntimeTransport;

    /// <summary>
    /// <c>true</c> only when the suite explicitly requested XPC (<c>CIDER_RUNTIME_TRANSPORT=xpc</c>) —
    /// not merely when it happens to resolve to XPC under the <c>auto</c> default, since that resolution
    /// is a runtime decision this static property cannot see. Used to gate transport-specific
    /// characterizations such as <see cref="Cider.E2E.Tests.PerfSmokeTests"/>.
    /// </summary>
    public static bool XpcTransport =>
        string.Equals(Transport, CiderOptions.XpcRuntimeTransport, StringComparison.OrdinalIgnoreCase);

    /// <summary>The daemon's configuration (short socket path, temp data dir).</summary>
    public CiderOptions Options { get; protected set; } = new();

    /// <summary>
    /// The fixture-instance-unique id <see cref="BuildOptions"/> bakes into <see cref="Options"/>'s
    /// paths, captured once in <see cref="InitializeAsync"/> so every later rebuild (see
    /// <see cref="RecreateOptions"/>) produces a value-identical <see cref="CiderOptions"/> — same
    /// <c>DataDir</c>, same <c>SocketPath</c> — just a different object instance.
    /// </summary>
    private string _id = "";

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
    //
    // Residual: this only protects objects that existed at fixture startup. Anything created outside
    // the suite after that point (e.g. a developer starting a container mid-run) is not covered —
    // that would need label-based filtering of the suite's own objects instead of a startup snapshot.
    private readonly HashSet<string> _preExistingContainerIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preExistingNetworkNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preExistingVolumeNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preExistingImageIds = new(StringComparer.Ordinal);

    // Set true only once all three pre-existing lists below have been captured successfully.
    // Teardown refuses to remove anything unless this is true, so a failed snapshot can never
    // fall back to the blanket delete it exists to prevent.
    private bool _snapshotOk;

    /// <summary>Overridable so the daemon-restart test can rebuild a daemon on the same data dir.</summary>
    protected virtual string InstanceSuffix => "";

    /// <summary>
    /// The state poller's interval, in seconds, or <c>null</c> to leave it unset so <c>CiderOptions</c>
    /// picks its own transport-aware default (<c>StatePoller</c>: 1s on xpc, 3s on cli — see
    /// <see cref="CiderOptions.PollIntervalSecondsIsExplicit"/>). Overridable so a test that
    /// specifically wants to exercise <c>POST /_cider/sync</c> (rather than the separate automatic
    /// poller-drop behaviour — see <see cref="Cider.Daemon.Routes.CiderRoutes"/>) can push it out far
    /// enough that the poller never races the assertion. Left at its <c>null</c> default, every other
    /// fixture (including the shared <see cref="DaemonCollection"/> one) now exercises the real
    /// transport-aware default end-to-end instead of pinning it to a fixture-chosen value.
    /// </summary>
    protected virtual int? PollIntervalOverride => null;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _id = Guid.NewGuid().ToString("n")[..8] + InstanceSuffix;
        Options = BuildOptions(_id);

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
    /// Builds this fixture instance's <see cref="CiderOptions"/> deterministically from <paramref
    /// name="id"/>: same <c>DataDir</c>, <c>SocketPath</c>, <c>PollIntervalSeconds</c>,
    /// <c>PortPublishing</c>, <c>LogLevel</c> and <c>DnsEnabled</c> every time it is called for this
    /// instance, but a fresh object each call.
    /// </summary>
    private CiderOptions BuildOptions(string id)
    {
        var options = new CiderOptions
        {
            // sockaddr_un.sun_path is 104 bytes on macOS: /tmp/cider-e2e-xxxxxxxx.sock is far below it.
            DataDir = $"/tmp/cider-e2e-{id}",
            SocketPath = $"/tmp/cider-e2e-{id}.sock",
            LogLevel = Environment.GetEnvironmentVariable("CIDER_E2E_LOGLEVEL") ?? "Information",
            DnsEnabled = true,

            // `proxy` by default, like the real daemon; CIDER_PORT_PUBLISHING=apple runs the
            // suite against Apple's own `-p` forwarder instead.
            PortPublishing = PortPublishingMode,

            // `auto` by default, like the real daemon; CI's transport matrix sets
            // CIDER_RUNTIME_TRANSPORT=xpc or =cli to pin this fixture's daemon to one transport for
            // the whole run instead of letting each fixture instance decide for itself.
            RuntimeTransport = Transport,
        };

        // Only assign when a fixture actually wants a pinned interval: the setter latches
        // PollIntervalSecondsIsExplicit, and assigning it unconditionally (even to the constructor's
        // own default) would pin it right back, defeating the transport-aware default this exists to
        // let through.
        if (PollIntervalOverride is { } pollIntervalSeconds)
        {
            options.PollIntervalSeconds = pollIntervalSeconds;
        }

        return options;
    }

    /// <summary>
    /// Replaces <see cref="Options"/> with a freshly built, value-identical copy of itself. A new
    /// <see cref="CiderOptions"/> instance is a new <c>ConditionalWeakTable</c> key for
    /// <c>RuntimeTransportSelector</c>'s per-instance transport-selection cache
    /// (<c>src/Cider.AppleContainer/Xpc/RuntimeTransportSelector.cs</c>), so restarting a daemon on a
    /// recreated <see cref="Options"/> forces a fresh <c>SelectAsync</c> and a live
    /// <c>XpcClient</c>, instead of the disposed one the cache would otherwise hand back keyed on the
    /// original, still-alive <see cref="Options"/> reference.
    /// </summary>
    protected void RecreateOptions() => Options = BuildOptions(_id);

    /// <summary>
    /// Records every container/network/volume the daemon already knows about right after startup
    /// (its own startup reconcile adopts whatever Apple's runtime already has, not just what this
    /// suite goes on to create), so teardown can tell "the suite's own" from "was already there"
    /// apart and never remove the latter.
    /// </summary>
    private async Task SnapshotPreExistingDockerObjectsAsync()
    {
        var containers = await DockerAsync(["ps", "-aq"], timeout: TimeSpan.FromSeconds(60));
        if (!containers.Ok)
        {
            _log.Enqueue(containers.ToString());
            throw new InvalidOperationException(
                "failed to snapshot pre-existing containers; refusing to start the fixture, since " +
                "teardown must never guess at what it may safely remove:\n" + containers);
        }

        foreach (var id in containers.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _preExistingContainerIds.Add(id);
        }

        var networks = await DockerAsync(["network", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
        if (!networks.Ok)
        {
            _log.Enqueue(networks.ToString());
            throw new InvalidOperationException(
                "failed to snapshot pre-existing networks; refusing to start the fixture, since " +
                "teardown must never guess at what it may safely remove:\n" + networks);
        }

        foreach (var network in networks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _preExistingNetworkNames.Add(network);
        }

        var volumes = await DockerAsync(["volume", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
        if (!volumes.Ok)
        {
            _log.Enqueue(volumes.ToString());
            throw new InvalidOperationException(
                "failed to snapshot pre-existing volumes; refusing to start the fixture, since " +
                "teardown must never guess at what it may safely remove:\n" + volumes);
        }

        foreach (var volume in volumes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _preExistingVolumeNames.Add(volume);
        }

        // By id (not tag): a multi-tag image must be recognised as pre-existing under every one of
        // its tags, and removed as one unit under all of them if the run itself created it -- an id
        // is the only handle stable across that. cider-24v/cider-0o3: the shared Apple store means an
        // image already present before this fixture started (the developer's own images, and every
        // base layer another concurrent run has already pulled) must never be swept up here.
        var images = await DockerAsync(["images", "-aq", "--no-trunc"], timeout: TimeSpan.FromSeconds(60));
        if (!images.Ok)
        {
            _log.Enqueue(images.ToString());
            throw new InvalidOperationException(
                "failed to snapshot pre-existing images; refusing to start the fixture, since " +
                "teardown must never guess at what it may safely remove:\n" + images);
        }

        foreach (var id in images.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _preExistingImageIds.Add(id);
        }

        // Only reached once all four lists above were captured successfully; teardown checks this
        // before removing anything.
        _snapshotOk = true;
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

            // BuildKit tests exercise the *default* builder (buildx's `docker` driver, talking to
            // cider's own /grpc + /session — no `docker buildx create`). A BUILDX_BUILDER left over
            // in the developer's shell would silently point builds at some other (possibly
            // docker-container) builder instead, and BUILDX_NO_DEFAULT_LOAD would suppress the
            // load-into-the-local-store behaviour the untagged/dangling-image tests depend on; both
            // are stripped unconditionally so every child starts from the same clean slate.
            ["BUILDX_BUILDER"] = null,
            ["BUILDX_NO_DEFAULT_LOAD"] = null,
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

        try
        {
            // Deliberately not swallowed: a vanished builder VM (see CleanupDockerObjectsAsync) is a
            // real regression the run must fail loudly on. `finally` still tears down the daemon
            // process and scratch state below so a failure here never leaks either.
            await CleanupDockerObjectsAsync();
        }
        finally
        {
            await StopDaemonAsync();
            await CleanupForwarderAsync();
            RemoveDirectory(Options.DataDir);
            RemoveFile(Options.SocketPath);
        }
    }

    /// <summary>
    /// Force-removes everything this suite created, so nothing outlives the run — but never anything
    /// that was already there when the fixture started (see <see cref="_preExistingContainerIds"/>).
    /// </summary>
    protected async Task CleanupDockerObjectsAsync()
    {
        if (!_snapshotOk)
        {
            _log.Enqueue(
                $"{DateTime.Now:HH:mm:ss.fff} Warning DaemonFixture: teardown skipped: pre-existing " +
                "snapshot unavailable, not removing any containers/volumes/networks");
            return;
        }

        // Snapshotted *before* the blanket `docker rm -f -v` sweep below, straight through the Apple
        // CLI rather than through cider: per cider-ger.3/T4b the builder VM is a system container
        // cider hides from `docker ps` entirely, so it can never show up in `ids` and get swept up by
        // that command directly — but a regression that stopped hiding it (or a teardown that stopped
        // going through cider and reached the runtime directly) would silently kill the developer's
        // builder VM. Compared again once the sweep is done, below — deliberately outside the
        // catch-and-ignore block that follows, so a real regression here fails loudly instead of being
        // swallowed along with ordinary teardown flakiness.
        var builderStateBefore = await AppleBuilderStateAsync();

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

            // Images this run tagged (e2e/*, cider-e2e*, cider-compat*), by id, so a multi-tag image
            // is removed in one call under every tag at once, same as `docker rmi -f <id>`.
            //
            // An untagged build also leaves a `cider-build-*` synthetic tag behind
            // (cider-ede.10/ImageManager) as an internal bookkeeping marker, but that marker is never
            // visible through cider's own listing API -- ImageManager.VisibleReferences strips
            // synthetic build tags and ToSummary derives RepoTags from that filtered set, so such an
            // image always lists here as `<none>:<none>` and is disqualified by the
            // `EndsWith(":<none>")` branch in FilterOwnedImageIdsAsync, whatever OwnedImageTagPrefixes
            // says. That residual untagged/synthetic-tagged debris is not reclaimed by this filter; it
            // is a known, accepted residual under cider-24v's rule that teardown never removes what
            // this run did not unambiguously create (an untagged id could just as easily be another
            // run's in-flight build). What this filter actually reclaims is the suite's own tagged
            // images.
            //
            // Since cider-ede.31, a plain `rmi` no longer sweeps the store's blob content on the XPC
            // transport (only `docker image prune` does, and only once per prune call) -- so this may
            // untag/remove the image records without reclaiming the disk space their blobs used the
            // way it implicitly did before that fix. That tradeoff is deliberate: an explicit
            // store-wide prune from every fixture's teardown would be a shared-infrastructure sweep
            // racing every other concurrent run's in-flight (not-yet-tagged) builds on the one Apple
            // store this machine has, which cider-0o3 is explicit teardown must never risk.
            //
            // "New since our snapshot" alone is not enough: the id space is global content shared
            // with every other concurrent run and the operator's own images, so anything another run
            // pulled or built between our snapshot and now would look identical to "this run created
            // it". A second, ownership test narrows the removal to ids this run can actually claim:
            // only an id all of whose repo:tag entries carry one of this harness's own prefixes (see
            // OwnedImageTagPrefixes) is removed; an untagged id or one carrying any other tag is left
            // alone -- it may be another run's in-flight build, or a base image (alpine, nginx,
            // ryuk, ...) newly pulled into the shared cache, which stays in the store by design. The
            // leak this task measures (this fixture's own e2e/* tags) is still cleaned.
            var currentImages = await DockerAsync(["images", "-aq", "--no-trunc"], timeout: TimeSpan.FromSeconds(60));
            var allImageIds = currentImages.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var newImageIds = allImageIds.Where(id => !_preExistingImageIds.Contains(id)).ToArray();
            LogSkipped("image", allImageIds.Length - newImageIds.Length);

            var imageIds = await FilterOwnedImageIdsAsync(newImageIds);
            var unownedCount = newImageIds.Length - imageIds.Length;
            if (unownedCount > 0)
            {
                _log.Enqueue(
                    $"{DateTime.Now:HH:mm:ss.fff} Information DaemonFixture: teardown skipped {unownedCount} " +
                    "new image(s) untagged or tagged outside this fixture's own prefixes -- may belong to " +
                    "another concurrent run or be a shared base image");
            }

            if (imageIds.Length > 0)
            {
                await DockerAsync(["rmi", "-f", .. imageIds], timeout: TimeSpan.FromSeconds(180));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }

        if (string.Equals(builderStateBefore, "running", StringComparison.Ordinal))
        {
            var builderStateAfter = await AppleBuilderStateAsync();
            if (!string.Equals(builderStateAfter, "running", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Apple's builder VM ('buildkit') was running before this fixture's teardown swept " +
                    $"containers and is not anymore (state now: {builderStateAfter ?? "<gone>"}). This is " +
                    "a regression of cider-ger.3/T4b (the builder must stay hidden from `docker ps -aq` " +
                    "so a suite-wide `docker rm -f -v` never reaches it) — not something teardown may " +
                    "silently paper over.");
            }
        }
    }

    /// <summary>
    /// The single prefix every E2E-minted image tag actually carries -- <see
    /// cref="BuildKitTests.UniqueTag"/> and <see cref="BuildTests.Tag"/> (the only two places the E2E
    /// suite tags an image) build their tags from this constant so the two can never drift apart from
    /// what <see cref="OwnedImageTagPrefixes"/> below recognises.
    /// </summary>
    internal const string OwnedTagPrefix = "e2e/";

    /// <summary>
    /// The repo-tag prefixes this fixture (and the compat harness, <c>tests/compat/lib/daemon.sh</c>)
    /// actually tag things with -- the synthetic tags cider-0o3's verification asserts on. <see
    /// cref="OwnedTagPrefix"/> ("e2e/...") covers every tag <see cref="BuildKitTests.UniqueTag"/> and
    /// <see cref="BuildTests.Tag"/> mint; "e2e-" covers a bare <see cref="NewName"/> used directly as a
    /// tag; "cider-e2e"/"cider-compat" are the compat harness's own prefixes
    /// (<c>tests/compat/lib/daemon.sh</c>). Used by <see cref="FilterOwnedImageIdsAsync"/> to narrow
    /// "new since our snapshot" down to "this run can actually claim it".
    ///
    /// This deliberately does NOT include a `cider-build-*` prefix: that synthetic tag
    /// (<see cref="Cider.Core.Services.ImageManager"/>'s marker for an untagged build) is never
    /// visible through cider's own API in the first place -- <c>ImageManager.VisibleReferences</c>
    /// strips it and <c>ToSummary</c> derives <c>RepoTags</c> from that filtered set, so such an image
    /// always lists as <c>&lt;none&gt;:&lt;none&gt;</c> here and is disqualified by the
    /// <c>EndsWith(":&lt;none&gt;")</c> branch below regardless of what prefixes this array names.
    /// Listing it as a prefix would misrepresent what this filter reclaims.
    /// </summary>
    private static readonly string[] OwnedImageTagPrefixes = [OwnedTagPrefix, "e2e-", "cider-e2e", "cider-compat"];

    /// <summary>
    /// Narrows <paramref name="candidateIds"/> (already known to be new since <see
    /// cref="SnapshotPreExistingDockerObjectsAsync"/>) down to the ids teardown may actually remove:
    /// an id whose every repo:tag entry carries one of <see cref="OwnedImageTagPrefixes"/>. An id that
    /// is untagged (<c>&lt;none&gt;</c>) or carries any other tag is left alone -- "new" is not the
    /// same as "ours" on a store shared with every other concurrent run and the operator's own images
    /// (cider-0o3); an untagged new layer may just as well be another run's in-flight build, and a
    /// freshly pulled base image (alpine, nginx, ryuk, ...) is shared content other runs depend on, so
    /// it stays in the cache by design -- re-pulling it is the cost this filter buys. Fails safe: an
    /// id the listing does not mention at all (e.g. it vanished between calls) is never returned.
    /// </summary>
    private async Task<string[]> FilterOwnedImageIdsAsync(IReadOnlyCollection<string> candidateIds)
    {
        if (candidateIds.Count == 0)
        {
            return [];
        }

        var listing = await DockerAsync(
            ["images", "-a", "--no-trunc", "--format", "{{.ID}}\t{{.Repository}}:{{.Tag}}"],
            timeout: TimeSpan.FromSeconds(60));
        if (!listing.Ok)
        {
            // Cannot tell what any of these ids are tagged with; refuse to remove any of them rather
            // than guess.
            return [];
        }

        return ParseOwnedImageIds(listing.Stdout, candidateIds);
    }

    /// <summary>
    /// The line-parsing/set logic <see cref="FilterOwnedImageIdsAsync"/> runs over a real <c>docker
    /// images -a --no-trunc --format "{{.ID}}\t{{.Repository}}:{{.Tag}}"</c> listing, pulled out as a
    /// pure function (cider-0o3 finding #2) so it is drivable directly in
    /// <c>DaemonFixtureImageOwnershipTests</c> against captured real CLI output, without a live daemon
    /// or <c>CIDER_E2E=1</c> — the whole point being that this is the one genuinely destructive step
    /// in teardown and it previously had zero non-E2E coverage. Fails closed line by line: a line that
    /// does not split into exactly two tab-separated fields, or whose id is not in
    /// <paramref name="candidateIds"/>, is ignored rather than guessed at (this is also the one place
    /// the real CLI's separator matters — the format string above is tab-separated, unlike the plain-
    /// space-separated shell equivalent in <c>tests/compat/lib/daemon.sh</c>).
    /// </summary>
    internal static string[] ParseOwnedImageIds(string listingText, IReadOnlyCollection<string> candidateIds)
    {
        var wanted = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var disqualified = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in listingText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length != 2 || !wanted.Contains(parts[0]))
            {
                continue;
            }

            var id = parts[0];
            var repoTag = parts[1];
            seen.Add(id);

            if (repoTag.EndsWith(":<none>", StringComparison.Ordinal)
                || !OwnedImageTagPrefixes.Any(prefix => repoTag.StartsWith(prefix, StringComparison.Ordinal)))
            {
                disqualified.Add(id);
            }
            else
            {
                owned.Add(id);
            }
        }

        return [.. seen.Where(id => owned.Contains(id) && !disqualified.Contains(id))];
    }

    /// <summary>
    /// <c>container builder status</c>'s state column for the <c>buildkit</c> row, straight through
    /// the Apple CLI — not through cider, which (per cider-ger.3/T4b) treats the builder VM as a
    /// system container and hides it from <c>docker ps</c> entirely, so there is no way to observe it
    /// through the daemon under test. <c>null</c> when there is no such row (builder never created) or
    /// the query itself failed.
    /// </summary>
    public static async Task<string?> AppleBuilderStateAsync()
    {
        try
        {
            var result = await Cmd.RunAsync("container", ["builder", "status"], timeout: TimeSpan.FromSeconds(30));
            if (!result.Ok)
            {
                return null;
            }

            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length >= 3 && string.Equals(columns[0], "buildkit", StringComparison.Ordinal))
                {
                    return columns[2];
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
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
