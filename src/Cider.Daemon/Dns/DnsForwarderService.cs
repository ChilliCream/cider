using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Cider.Core.Configuration;
using Cider.Core.Ids;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Dns;

namespace Cider.Daemon.Dns;

/// <summary>
/// Hosts the daemon's DNS server and the per-network CoreDNS forwarders that relay to it.
/// Host port 53 belongs to macOS/vmnet once Apple's network is up (docs/apple-container-notes.md §7),
/// so the server listens high (default <c>0.0.0.0:10053</c>, or the first free port in the 20 above
/// it when another daemon already holds it) and every Docker network gets a tiny
/// <c>cider-dns-&lt;network&gt;-&lt;datadir-hash&gt;</c> container forwarding <c>:53</c> to
/// <c>&lt;gateway&gt;:&lt;bound-port&gt;</c>. The name carries a hash of the data dir so that several
/// daemon instances on one machine never fight over the same forwarder container.
/// The forwarders are created through <see cref="IContainerRuntime"/> directly so they never become
/// Docker containers of their own.
/// </summary>
public sealed class DnsForwarderService : IDnsForwarderService, IAsyncDisposable
{
    /// <summary>Label marking a container as daemon infrastructure (hidden from every Docker listing).</summary>
    public const string SystemLabel = "com.chillicream.cider.system";

    /// <summary>Label carrying the Docker network a forwarder serves.</summary>
    public const string NetworkLabel = "com.chillicream.cider.network";

    /// <summary>Label carrying the hash of the data dir of the daemon instance owning a forwarder.</summary>
    public const string DataDirLabel = "com.chillicream.cider.datadir";

    /// <summary>
    /// Transitional (rename to Cider): the pre-rename keys of the three labels above. Forwarders
    /// started by an older daemon carry them, and a daemon that could not recognise its own
    /// forwarders would leave them running and unreclaimable. Read, never written; delete once no
    /// <c>com.apple-demon.*</c> labelled objects remain.
    /// </summary>
    public const string LegacySystemLabel = "com.apple-demon.system";

    /// <summary>Transitional (rename to Cider): pre-rename <see cref="NetworkLabel"/>; read, never written.</summary>
    public const string LegacyNetworkLabel = "com.apple-demon.network";

    /// <summary>Transitional (rename to Cider): pre-rename <see cref="DataDirLabel"/>; read, never written.</summary>
    public const string LegacyDataDirLabel = "com.apple-demon.datadir";

    /// <summary>How many ports above the configured one are probed before DNS is given up on.</summary>
    public const int PortProbeRange = 20;

    private const long ForwarderMemoryBytes = 256L * 1024 * 1024;

    private readonly IContainerRuntime _runtime;
    private readonly NetworkManager _networks;
    private readonly IDnsResolver _resolver;
    private readonly CiderOptions _options;
    private readonly ILogger<DnsForwarderService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IPAddress> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IContainerProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dataDirHash;

    private DnsServer? _server;
    private int _stopped;

    /// <summary>Creates the service; nothing is started until <see cref="StartAsync"/> runs.</summary>
    public DnsForwarderService(
        IContainerRuntime runtime,
        NetworkManager networks,
        IDnsResolver resolver,
        CiderOptions options,
        ILogger<DnsForwarderService> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _networks = networks ?? throw new ArgumentNullException(nameof(networks));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataDirHash = DataDirHash(_options.DataDir);
    }

    /// <summary>The 8 hex characters of SHA-256(data dir) that identify this daemon's forwarders.</summary>
    public string InstanceHash => _dataDirHash;

    /// <summary>The endpoint the DNS server actually bound, or <c>null</c> when it is not running.</summary>
    public IPEndPoint? ListenEndPoint => _server?.LocalEndPoint;

    /// <summary>Starts the host-side DNS server; a failed bind only disables DNS, it never fails startup.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_server is not null)
        {
            return;
        }

        if (!TryParseEndPoint(_options.DnsListen, 10053, out var listen))
        {
            _logger.LogWarning("invalid DnsListen '{Listen}'; the DNS server stays off", _options.DnsListen);
            return;
        }

        var upstreams = new List<IPEndPoint>();
        foreach (var upstream in _options.DnsUpstreams)
        {
            if (TryParseEndPoint(upstream, 53, out var parsed))
            {
                upstreams.Add(parsed);
            }
            else
            {
                _logger.LogWarning("ignoring unparsable DNS upstream '{Upstream}'", upstream);
            }
        }

        // Several cider instances can share one machine (a compat run, an E2E run, the
        // installed daemon), and only the first one gets the configured port. Walk the next
        // PortProbeRange ports rather than switching container name resolution off outright; the
        // port that actually got bound is the one the forwarders' Corefile points at.
        Exception? lastError = null;
        var probes = listen.Port == 0 ? 1 : PortProbeRange + 1;
        for (var offset = 0; offset < probes; offset++)
        {
            var port = listen.Port == 0 ? 0 : listen.Port + offset;
            if (port > IPEndPoint.MaxPort)
            {
                break;
            }

            var server = new DnsServer(new IPEndPoint(listen.Address, port), _resolver, upstreams, _logger);
            try
            {
                await server.StartAsync(ct);
                _server = server;
                if (offset == 0)
                {
                    _logger.LogInformation("DNS server listening on {EndPoint}", server.LocalEndPoint);
                }
                else
                {
                    _logger.LogInformation(
                        "DNS port {Requested} is already in use; the DNS server listens on {EndPoint} instead",
                        listen.Port,
                        server.LocalEndPoint);
                }

                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                lastError = ex;
                await server.DisposeAsync();
                _logger.LogDebug("DNS port {Port} is already in use; trying the next one", port);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                await server.DisposeAsync();
                break;
            }
        }

        if (_server is null)
        {
            _logger.LogWarning(
                lastError,
                "could not bind the DNS server to {Listen} or the {Count} ports above it; container name resolution is off",
                listen,
                PortProbeRange);
            return;
        }

        await CleanupStaleForwardersAsync(ct);
        await ReapOrphanedForwardersAsync(ct);
    }

    /// <summary>
    /// Removes forwarders tagged with this daemon's data dir whose Docker network is gone — a
    /// leftover from a network removed while the daemon was down, which nothing would ever reclaim.
    /// </summary>
    private async Task CleanupStaleForwardersAsync(CancellationToken ct)
    {
        try
        {
            foreach (var container in await _runtime.ListContainersAsync(ct))
            {
                if (!IsOurForwarder(container))
                {
                    continue;
                }

                ContainerIdentity.TryReadLabel(container.Labels, NetworkLabel, LegacyNetworkLabel, out var network);

                // Transitional (rename to Cider): a forwarder this data dir owns but that a
                // pre-rename daemon named apple-demon-dns-* is never found by ForwarderName, so it
                // would be left running forever beside its replacement. Remove it and let the
                // normal path create the cider-dns-* one. Delete with the legacy label constants.
                var preRename = container.RuntimeId.StartsWith(LegacyForwarderPrefix, StringComparison.Ordinal);
                if (!preRename && !string.IsNullOrEmpty(network) && _networks.TryGetCachedRecord(network) is not null)
                {
                    continue;
                }

                _logger.LogInformation(
                    preRename
                        ? "removing the pre-rename DNS forwarder {Container} for network '{Network}'; a cider-dns-* one replaces it"
                        : "removing the stale DNS forwarder {Container}; its network '{Network}' no longer exists",
                    container.RuntimeId,
                    network);

                try
                {
                    await _runtime.RemoveContainerAsync(container.RuntimeId, force: true, ct);
                }
                catch (RuntimeException ex)
                {
                    _logger.LogDebug(ex, "could not remove the stale DNS forwarder {Container}", container.RuntimeId);
                }
            }
        }
        catch (Exception ex) when (ex is RuntimeException or IOException)
        {
            _logger.LogDebug(ex, "could not scan for stale DNS forwarders");
        }
    }

    private bool IsOurForwarder(RuntimeContainer container) =>
        ContainerIdentity.TryReadLabel(container.Labels, SystemLabel, LegacySystemLabel, out var system)
        && string.Equals(system, "dns", StringComparison.Ordinal)
        && ContainerIdentity.TryReadLabel(container.Labels, DataDirLabel, LegacyDataDirLabel, out var hash)
        && string.Equals(hash, _dataDirHash, StringComparison.Ordinal);

    /// <summary>
    /// Removes DNS forwarders belonging to <em>other</em> daemon instances (any data-dir hash, not just
    /// ours — <see cref="CleanupStaleForwardersAsync"/> already covers our own, by network liveness)
    /// whose owning daemon is gone for good, per cider-0o3: a hard-killed daemon never runs its own
    /// shutdown release (<see cref="ReleaseAsync"/>) or this cleanup, so its forwarder VMs (256 MB
    /// each) would otherwise sit there forever, and a throwaway run's data dir (e.g. the E2E fixture's
    /// <c>/tmp/cider-e2e-&lt;id&gt;</c>, freshly generated every run) is never reused, so a future
    /// daemon's own <see cref="_dataDirHash"/> would never again equal the dead one's either.
    /// <para/>
    /// <see cref="DataDirLabel"/> carries only a one-way SHA-256 hash of the data dir
    /// (<see cref="DataDirHash"/>), not the path itself, so "is the owning daemon still around" cannot
    /// be answered by reversing it. Instead this hashes every data dir this scan can still find on
    /// disk (see <see cref="ComputeLiveDataDirHashes"/> — this instance's own, the real default, and
    /// every <c>/tmp/cider-*</c> directory) into the set of "live" hashes, and removes any forwarder
    /// whose hash is in none of them. A second daemon that is actually still running is always
    /// protected by this: its data dir necessarily still exists on disk for as long as it is up, so
    /// its hash is always in the live set.
    /// <para/>
    /// Residual (confirmed live against this machine's own accumulated state while building this):
    /// a data dir this scan does not know to look under (outside <c>/tmp/cider-*</c> and the real
    /// default) reads as "no live data dir" even for a daemon that is genuinely still running there —
    /// this covers both a custom <c>--data-dir</c> outside those conventions, and a
    /// <c>/tmp/cider-*</c> one whose directory a still-running daemon's own process had removed out
    /// from under it (its listening socket keeps working by inode once opened, so the process can
    /// stay up with no directory left at its own configured path — observed live on this machine
    /// during verification, though with no forwarder of its own at the time). Conversely, a data dir
    /// left on disk by a hard-killed daemon this scan does know to look under reads as "live" until
    /// something removes it. This is a best-effort machine-wide sweep, not a guarantee, exactly the
    /// tradeoff cider-0o3 accepts in place of an isolated per-run store.
    /// </summary>
    private async Task ReapOrphanedForwardersAsync(CancellationToken ct)
    {
        try
        {
            var liveHashes = ComputeLiveDataDirHashes();
            foreach (var container in await _runtime.ListContainersAsync(ct))
            {
                if (!ContainerIdentity.TryReadLabel(container.Labels, SystemLabel, LegacySystemLabel, out var system)
                    || !string.Equals(system, "dns", StringComparison.Ordinal)
                    || !ContainerIdentity.TryReadLabel(container.Labels, DataDirLabel, LegacyDataDirLabel, out var hash)
                    || string.Equals(hash, _dataDirHash, StringComparison.Ordinal)
                    || liveHashes.Contains(hash))
                {
                    continue;
                }

                ContainerIdentity.TryReadLabel(container.Labels, NetworkLabel, LegacyNetworkLabel, out var network);
                _logger.LogInformation(
                    "removing orphaned DNS forwarder {Container} for network '{Network}': its data-dir hash " +
                    "{Hash} matches no data dir this scan can still find, so its owning daemon is gone",
                    container.RuntimeId,
                    network,
                    hash);

                try
                {
                    await _runtime.RemoveContainerAsync(container.RuntimeId, force: true, ct);
                }
                catch (RuntimeException ex)
                {
                    _logger.LogDebug(ex, "could not remove the orphaned DNS forwarder {Container}", container.RuntimeId);
                }
            }
        }
        catch (Exception ex) when (ex is RuntimeException or IOException)
        {
            _logger.LogDebug(ex, "could not scan for orphaned DNS forwarders");
        }
    }

    /// <summary>
    /// Hashes every data dir <see cref="ReapOrphanedForwardersAsync"/> can still find on disk: this
    /// instance's own, the real default (<c>~/.cider</c>), and every <c>/tmp/cider-*</c> directory —
    /// the E2E fixture's own convention is <c>/tmp/cider-e2e-&lt;id&gt;</c>
    /// (<c>DaemonFixture.BuildOptions</c>) and the compat harness's is <c>/tmp/cider-*-data</c>
    /// (<c>tests/compat/lib/daemon.sh</c>), but ad hoc debugging sessions on this machine are observed
    /// to use other <c>/tmp/cider-*</c> names too (e.g. <c>cider-repro</c>), so the broadest prefix a
    /// throwaway data dir under <c>/tmp</c> is ever *not* going to start with is used rather than
    /// either narrower convention alone — a name that happens to collide without being a real data dir
    /// (a build work dir, a log file's directory) only over-protects a hash that could never legitimately
    /// belong to a forwarder anyway, so it costs nothing. A directory that no longer exists contributes
    /// nothing, by design: it is exactly what marks a forwarder as orphaned.
    /// </summary>
    private HashSet<string> ComputeLiveDataDirHashes()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal) { _dataDirHash };

        void AddIfExists(string dir)
        {
            if (Directory.Exists(dir))
            {
                hashes.Add(DataDirHash(dir));
            }
        }

        AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cider"));

        // Literal "/tmp", not Path.GetTempPath(): on macOS that resolves to a per-user
        // /var/folders/.../T/ path from $TMPDIR, but both DaemonFixture.BuildOptions and
        // tests/compat/lib/daemon.sh hardcode "/tmp/..." directly, so that is what must be scanned.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/tmp", "cider-*"))
            {
                AddIfExists(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return hashes;
    }

    /// <summary>Stops the DNS server and releases the held forwarder processes.</summary>
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);

        if (_server is { } server)
        {
            _server = null;
            await server.StopAsync();
            await server.DisposeAsync();
        }

        foreach (var (network, process) in _processes.ToArray())
        {
            _processes.TryRemove(network, out _);
            try
            {
                await process.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<IPAddress?> EnsureAsync(string dockerNetworkName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dockerNetworkName);

        if (_stopped == 1 || _server is null || !_options.DnsEnabled)
        {
            return null;
        }

        if (_addresses.TryGetValue(dockerNetworkName, out var cached))
        {
            return cached;
        }

        var gate = _gates.GetOrAdd(dockerNetworkName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_addresses.TryGetValue(dockerNetworkName, out cached))
            {
                return cached;
            }

            var address = await EnsureCoreAsync(dockerNetworkName, ct);
            if (address is not null)
            {
                _addresses[dockerNetworkName] = address;
            }

            return address;
        }
        catch (Exception ex) when (ex is RuntimeException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "could not provide a DNS forwarder for network {Network}", dockerNetworkName);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string dockerNetworkName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dockerNetworkName);

        var gate = _gates.GetOrAdd(dockerNetworkName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var containerId = ForwarderName(dockerNetworkName, _dataDirHash);
            await StopForwarderAsync(dockerNetworkName, containerId, ct);

            try
            {
                await _runtime.RemoveContainerAsync(containerId, force: true, ct);
                _logger.LogDebug("removed the DNS forwarder {Container} with its network", containerId);
            }
            catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
            {
            }
        }
        catch (Exception ex) when (ex is RuntimeException or IOException)
        {
            _logger.LogWarning(ex, "could not release the DNS forwarder for network {Network}", dockerNetworkName);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IPAddress?> EnsureCoreAsync(string dockerNetworkName, CancellationToken ct)
    {
        var runtimeNetwork = await _networks.GetRuntimeNetworkAsync(dockerNetworkName, ct);
        if (runtimeNetwork?.Gateway is not { Length: > 0 } gateway)
        {
            _logger.LogWarning("network {Network} has no gateway; containers get no --dns", dockerNetworkName);
            return null;
        }

        var listenPort = ListenEndPoint?.Port ?? 10053;
        var configDir = Path.Combine(_options.DataDir, "dns", Sanitize(dockerNetworkName));
        Directory.CreateDirectory(configDir);

        // A forwarder surviving from an earlier run of this same daemon still points at whatever
        // gateway/port were current back then, so the Corefile it is running with is compared
        // against the one we would write now, and a mismatch restarts it.
        var corefilePath = Path.Combine(configDir, "Corefile");
        var corefile = BuildCorefile(gateway, listenPort);
        var stale = !File.Exists(corefilePath)
            || !string.Equals(await File.ReadAllTextAsync(corefilePath, ct), corefile, StringComparison.Ordinal);
        if (stale)
        {
            await File.WriteAllTextAsync(corefilePath, corefile, ct);
        }

        var containerId = ForwarderName(dockerNetworkName, _dataDirHash);
        var existing = await _runtime.InspectContainerAsync(containerId, ct);

        if (existing is null)
        {
            await EnsureImageAsync(ct);

            var spec = new ContainerSpec
            {
                RuntimeId = containerId,
                Image = _options.DnsForwarderImage,
                // Self-contained: the coredns image's own entrypoint, so this create is a fully
                // merged spec the XPC transport's fast path can take directly instead of relying on
                // the entrypoint-less-spec CLI fallback forever (task fix direction §3).
                Entrypoint = await ResolveForwarderEntrypointAsync(ct),
                Args = ["-conf", "/etc/coredns/Corefile"],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SystemLabel] = "dns",
                    [NetworkLabel] = dockerNetworkName,
                    [DataDirLabel] = _dataDirHash,
                },
                Networks = [_networks.RuntimeNameFor(dockerNetworkName)],
                Mounts =
                [
                    new MountSpec
                    {
                        Kind = MountKind.Bind,
                        Source = configDir,
                        Target = "/etc/coredns",
                        ReadOnly = true,
                    },
                ],
                Cpus = 1,
                MemoryBytes = ForwarderMemoryBytes,
            };

            await _runtime.CreateContainerAsync(spec, ct);
            existing = await _runtime.InspectContainerAsync(containerId, ct);
        }
        else if (stale && existing.State == RuntimeContainerState.Running)
        {
            _logger.LogInformation(
                "the DNS forwarder config for network {Network} changed (forward . {Gateway}:{Port}); restarting {Container}",
                dockerNetworkName,
                gateway,
                listenPort,
                containerId);

            await StopForwarderAsync(dockerNetworkName, containerId, ct);
            existing = await _runtime.InspectContainerAsync(containerId, ct);
        }
        else if (existing.State == RuntimeContainerState.Running)
        {
            _logger.LogDebug("reusing the running DNS forwarder {Container}", containerId);
        }

        if (existing?.State != RuntimeContainerState.Running || !_processes.ContainsKey(dockerNetworkName))
        {
            await StartForwarderAsync(dockerNetworkName, containerId, ct);
        }

        var address = await ReadAddressAsync(containerId, ct);
        if (address is null)
        {
            _logger.LogWarning("DNS forwarder {Container} has no address yet; containers get no --dns", containerId);
        }
        else
        {
            _logger.LogInformation("DNS forwarder for network {Network} is {Address}", dockerNetworkName, address);
        }

        return address;
    }

    private async Task StartForwarderAsync(string dockerNetworkName, string containerId, CancellationToken ct)
    {
        IContainerProcess process;
        try
        {
            process = await _runtime.StartContainerAsync(containerId, new StartOptions(), ct);
        }
        catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.Conflict)
        {
            // Already running from an earlier daemon run: fine, we just do not hold its stdio.
            _logger.LogDebug(ex, "DNS forwarder {Container} was already running", containerId);
            return;
        }

        _processes[dockerNetworkName] = process;
        _ = WatchAsync(dockerNetworkName, containerId, process);
    }

    /// <summary>Stops a forwarder on purpose (config change), without the "it died" warning.</summary>
    private async Task StopForwarderAsync(string dockerNetworkName, string containerId, CancellationToken ct)
    {
        if (_processes.TryRemove(dockerNetworkName, out var process))
        {
            try
            {
                await process.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        _addresses.TryRemove(dockerNetworkName, out _);

        try
        {
            await _runtime.StopContainerAsync(containerId, 5, null, ct);
        }
        catch (RuntimeException ex)
        {
            _logger.LogDebug(ex, "could not stop the DNS forwarder {Container}", containerId);
        }
    }

    private async Task WatchAsync(string dockerNetworkName, string containerId, IContainerProcess process)
    {
        _ = DrainAsync(process.Stdout, containerId);
        if (process.Stderr is { } stderr)
        {
            _ = DrainAsync(stderr, containerId);
        }

        int exitCode;
        try
        {
            exitCode = await process.Exited;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            exitCode = -1;
        }

        // False when the process was already taken out deliberately (StopForwarderAsync) or replaced.
        var wasCurrent = _processes.TryRemove(new KeyValuePair<string, IContainerProcess>(dockerNetworkName, process));
        if (wasCurrent)
        {
            _addresses.TryRemove(dockerNetworkName, out _);
        }

        if (_stopped == 0 && wasCurrent)
        {
            _logger.LogWarning("DNS forwarder {Container} exited with code {Code}; it will be re-created on demand", containerId, exitCode);
        }
    }

    private async Task DrainAsync(Stream stream, string containerId)
    {
        try
        {
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
            {
                _logger.LogDebug("[{Container}] {Line}", containerId, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        var image = _options.DnsForwarderImage;
        if (await _runtime.InspectImageAsync(image, ct) is not null)
        {
            return;
        }

        _logger.LogInformation("pulling the DNS forwarder image {Image}", image);
        await _runtime.PullImageAsync(image, null, null, new Progress<ProgressEvent>(), ct);
    }

    /// <summary>The coredns image's own entrypoint, read from the image config already ensured by
    /// <see cref="EnsureImageAsync"/> — falls back to the documented <c>/coredns</c> entrypoint of
    /// the official coredns image if the config somehow does not report one, so the forwarder's own
    /// create never depends on the image-config-less CLI fallback (task fix direction §3).</summary>
    private async Task<string> ResolveForwarderEntrypointAsync(CancellationToken ct)
    {
        var detail = await _runtime.InspectImageAsync(_options.DnsForwarderImage, ct);
        return detail?.Config.Entrypoint is { Count: > 0 } entrypoint ? entrypoint[0] : "/coredns";
    }

    private async Task<IPAddress?> ReadAddressAsync(string containerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var container = await _runtime.InspectContainerAsync(containerId, ct);
            var raw = container?.Networks.FirstOrDefault()?.IPv4Address;
            if (!string.IsNullOrEmpty(raw))
            {
                var slash = raw.IndexOf('/', StringComparison.Ordinal);
                var text = slash < 0 ? raw : raw[..slash];
                if (IPAddress.TryParse(text, out var address))
                {
                    return address;
                }
            }

            await Task.Delay(200, ct);
        }

        return null;
    }

    private const string ForwarderPrefix = "cider-dns-";

    /// <summary>
    /// Transitional (rename to Cider): the forwarder name prefix used before the rename. Only used
    /// to recognise and retire such a forwarder; nothing is ever created with it.
    /// </summary>
    private const string LegacyForwarderPrefix = "apple-demon-dns-";

    /// <summary>
    /// The longest container id Apple <c>container</c> 1.2.2 accepts; one character more and
    /// <c>container create</c> refuses it with "container ID … is not a valid container ID" (probed
    /// on 1.2.2, where Aspire's 42-character session network name pushed the
    /// forwarder id to 65 and left that network without DNS).
    /// </summary>
    private const int MaxRuntimeContainerIdLength = 63;

    /// <summary>
    /// The engine-side id of the forwarder serving one Docker network for one daemon instance:
    /// <c>cider-dns-&lt;network&gt;-&lt;8 hex of SHA-256(DataDir)&gt;</c>. The suffix keeps two
    /// daemons with different data dirs from adopting (and restarting) each other's forwarders, and
    /// the network part is cut short enough for the whole id to stay inside Apple's limit.
    /// </summary>
    public static string ForwarderName(string dockerNetworkName, string dataDirHash) =>
        ForwarderPrefix
        + Sanitize(dockerNetworkName, MaxRuntimeContainerIdLength - ForwarderPrefix.Length - 1 - dataDirHash.Length)
        + "-"
        + dataDirHash;

    /// <summary>The 8 hex characters of SHA-256(<paramref name="dataDir"/>) used in forwarder names.</summary>
    public static string DataDirHash(string dataDir)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(dataDir ?? ""));
        return Convert.ToHexStringLower(bytes)[..8];
    }

    /// <summary>The CoreDNS configuration a forwarder for one gateway/port pair must run with.</summary>
    private static string BuildCorefile(string gateway, int listenPort) =>
        $".:53 {{\n    forward . {gateway}:{listenPort.ToString(CultureInfo.InvariantCulture)}\n    cache 10\n    errors\n}}\n";

    private static string Sanitize(string value, int maxLength = 40)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray();
        var text = new string(chars);
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>Parses <c>host:port</c> / <c>host</c> into an endpoint, defaulting the port.</summary>
    public static bool TryParseEndPoint(string? value, int defaultPort, out IPEndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.Any, defaultPort);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (IPEndPoint.TryParse(text, out var parsed))
        {
            endPoint = parsed.Port == 0 ? new IPEndPoint(parsed.Address, defaultPort) : parsed;
            return true;
        }

        if (IPAddress.TryParse(text, out var address))
        {
            endPoint = new IPEndPoint(address, defaultPort);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();
}
