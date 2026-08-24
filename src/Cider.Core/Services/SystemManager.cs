using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Core.Time;
using DockerRuntimeInfo = Cider.Core.DockerApi.Models.RuntimeInfo;

namespace Cider.Core.Services;

/// <summary>System-level Docker endpoints: <c>/version</c>, <c>/info</c>, <c>/system/df</c>, <c>/_ping</c>.</summary>
public sealed class SystemManager
{
    // Resolved once: neither depends on anything that changes while the daemon is running.
    private static readonly string GitCommitValue = ResolveGitCommit();
    private static readonly string BuildTimeValue = ResolveBuildTime();

    // Detected, not hard-coded: Apple container requires Apple silicon today, but
    // the wire format must not state an architecture the process does not actually have. Docker
    // spells it GOARCH-style on /version ("arm64") and uname-style on /info ("aarch64").
    // OSArchitecture, not ProcessArchitecture: an x64 daemon build under Rosetta must still report
    // the machine (arm64/aarch64) — Apple container only runs arm64 guests, and a client selecting
    // an image platform from /info would otherwise pull amd64 images that cannot run.
    private static readonly string GoArchValue = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => "arm64",
    };

    private static readonly string UnameArchValue = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        _ => "aarch64",
    };

    private readonly IContainerRuntime _runtime;
    private readonly IContainerCounts _containers;
    private readonly ImageManager _images;
    private readonly VolumeManager _volumes;
    private readonly CiderOptions _options;
    private readonly EngineId _engineId;

    public SystemManager(IContainerRuntime runtime, IContainerCounts containers, ImageManager images, VolumeManager volumes, CiderOptions options, EngineId engineId)
    {
        _runtime = runtime;
        _containers = containers;
        _images = images;
        _volumes = volumes;
        _options = options;
        _engineId = engineId;
    }

    public async Task<VersionResponse> VersionAsync(CancellationToken ct)
    {
        var info = await _runtime.GetInfoAsync(ct).ConfigureAwait(false);
        var kernelVersion = info.KernelVersion ?? "";

        return new VersionResponse
        {
            Version = _options.EngineVersion,
            ApiVersion = _options.ApiVersion,
            MinAPIVersion = _options.MinApiVersion,
            Os = "linux",
            Arch = GoArchValue,
            KernelVersion = kernelVersion,
            Platform = new PlatformInfo { Name = $"cider (Apple container {info.Version})" },
            Components =
            [
                new ComponentVersion
                {
                    Name = "Engine",
                    Version = _options.EngineVersion,
                    Details = new Dictionary<string, string>
                    {
                        ["ApiVersion"] = _options.ApiVersion,
                        ["MinAPIVersion"] = _options.MinApiVersion,
                        ["Os"] = "linux",
                        ["Arch"] = GoArchValue,
                        ["GoVersion"] = "n/a",
                        ["GitCommit"] = GitCommitValue,
                        ["BuildTime"] = BuildTimeValue,
                        ["KernelVersion"] = kernelVersion,
                        ["Experimental"] = "false",
                    },
                },
            ],
            BuildTime = BuildTimeValue,
            GitCommit = GitCommitValue,
            GoVersion = "n/a",
            Experimental = false,
        };
    }

    public async Task<SystemInfo> InfoAsync(CancellationToken ct)
    {
        var info = await _runtime.GetInfoAsync(ct).ConfigureAwait(false);
        var imagesCount = await _images.CountAsync(ct).ConfigureAwait(false);
        var macosVersion = await HostFacts.MacosProductVersion.ConfigureAwait(false);

        return new SystemInfo
        {
            ID = _engineId.Value,
            Containers = _containers.Count(),
            ContainersRunning = _containers.Count("running"),
            ContainersPaused = 0,
            ContainersStopped = _containers.Count("exited"),
            Images = imagesCount,
            Driver = "apple-container",
            OSType = "linux",
            Architecture = UnameArchValue,
            OperatingSystem = $"Apple container {info.Version} (macOS {macosVersion})",
            KernelVersion = info.KernelVersion ?? "",
            NCPU = Environment.ProcessorCount,
            MemTotal = await HostFacts.MemTotalBytes.ConfigureAwait(false),
            Name = Environment.MachineName,
            ServerVersion = _options.EngineVersion,
            Labels = [],
            ExperimentalBuild = false,
            Swarm = new SwarmInfo { LocalNodeState = "inactive", NodeID = "", ControlAvailable = false },
            Plugins = new PluginsInfo { Volume = ["local"], Network = ["bridge"], Authorization = null, Log = ["json-file"] },
            LoggingDriver = "json-file",
            CgroupDriver = "none",
            CgroupVersion = "2",
            DefaultRuntime = "apple-container",
            Runtimes = new Dictionary<string, DockerRuntimeInfo>
            {
                ["apple-container"] = new DockerRuntimeInfo { Path = _options.ContainerCliPath },
            },
            SecurityOptions = [],
            Warnings = [],
            IndexServerAddress = "https://index.docker.io/v1/",
            RegistryConfig = new RegistryServiceConfig { IndexConfigs = [], Mirrors = [], InsecureRegistryCIDRs = ["127.0.0.0/8"] },
            DockerRootDir = _options.DataDir,
            SystemTime = DockerTime.Format(DateTimeOffset.UtcNow),
            IPv4Forwarding = true,
            BridgeNfIptables = false,
            BridgeNfIp6tables = false,
            Debug = false,
            NFd = 0,
            NGoroutines = 0,
            HttpProxy = "",
            HttpsProxy = "",
            NoProxy = "",
        };
    }

    public async Task<DiskUsage> DiskUsageAsync(CancellationToken ct)
    {
        var usage = await _runtime.GetDiskUsageAsync(ct).ConfigureAwait(false);
        var images = await _images.ListAsync(true, Filters.Empty, false, ct).ConfigureAwait(false);
        var volumes = await _volumes.ListAsync(Filters.Empty, ct).ConfigureAwait(false);

        return new DiskUsage
        {
            LayersSize = usage.ImagesBytes,
            Images = images.ToList(),
            Containers = [],
            Volumes = volumes.Volumes,
            BuildCache = [],
            BuilderSize = 0,
        };
    }

    public PingInfo Ping() => new()
    {
        ApiVersion = _options.ApiVersion,
        BuilderVersion = "1",
        Experimental = false,
        OsType = "linux",
        Swarm = "inactive",
    };

    // No explicit SourceLink step is configured, but the .NET SDK stamps the informational version
    // with "+<git sha>" on its own whenever it can run `git rev-parse HEAD` in the source tree, so
    // this reads a real commit on a normal checkout. "unknown" is what real dockerd itself prints
    // when it was built without git metadata (e.g. from a source tarball), so it is a legitimate
    // value here too, not a bug, when that auto-detection has nothing to stamp.
    private static string ResolveGitCommit()
    {
        var informational = typeof(SystemManager).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0 && plus + 1 < informational.Length)
            {
                return informational[(plus + 1)..];
            }
        }

        return "unknown";
    }

    // Deterministic builds strip the PE timestamp, so the assembly file's own last-write time is
    // the closest available approximation of "when this build was produced".
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "IL3000 warns that Assembly.Location is the empty string in a single-file "
            + "or Native AOT app, which is exactly the case this method already handles: an empty "
            + "location returns an empty BuildTime, the same value the catch below produces. "
            + "Docker itself reports BuildTime as a best-effort string, so an empty one is a valid "
            + "answer and there is nothing to fall back to (AppContext.BaseDirectory names the "
            + "directory, not a file with a meaningful timestamp).")]
    private static string ResolveBuildTime()
    {
        try
        {
            var location = typeof(SystemManager).Assembly.Location;
            return string.IsNullOrEmpty(location) ? "" : DockerTime.Format(File.GetLastWriteTimeUtc(location));
        }
        catch (IOException)
        {
            return "";
        }
    }

}

/// <summary>
/// Host facts served on <c>/info</c> that cannot change while the daemon runs, each probed by a
/// child process <em>once per daemon lifetime</em> and cached. The old shape ran
/// both probes on every <c>GET /info</c> — which Testcontainers and Aspire hit at startup — and its
/// <c>ReadToEnd()</c> blocked a Kestrel request thread unboundedly, because the 2-second
/// <c>WaitForExit</c> only ran <em>after</em> the read had already waited for the child to close
/// stdout. The probe here bounds the read and the exit together and kills the child on timeout.
/// </summary>
/// <remarks>
/// The cached tasks deliberately take no <see cref="CancellationToken"/>: the first caller must not
/// be able to poison the cache for everyone else by disconnecting, and the probe's own timeout
/// already bounds how long any caller can wait.
/// </remarks>
public static class HostFacts
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly ProbedFact<string> MacosProductVersionFact = new(
        () => ReadCommandOutputAsync("/usr/bin/sw_vers", "-productVersion", ProbeTimeout),
        static value => value.Length > 0);

    private static readonly ProbedFact<long> MemTotalFact = new(
        static async () =>
        {
            var output = await ReadCommandOutputAsync("/usr/sbin/sysctl", "-n hw.memsize", ProbeTimeout).ConfigureAwait(false);
            return long.TryParse(output, out var value) ? value : 0;
        },
        static value => value > 0);

    /// <summary>The macOS product version, e.g. <c>26.6.2</c>; <c>""</c> when the probe failed.</summary>
    public static Task<string> MacosProductVersion => MacosProductVersionFact.GetAsync();

    /// <summary>Physical memory in bytes; <c>0</c> when the probe failed.</summary>
    public static Task<long> MemTotalBytes => MemTotalFact.GetAsync();

    /// <summary>
    /// Memoizes a probe's result <em>only once it succeeded</em>. A transient first failure (a
    /// startup storm making fork/exec exceed the probe timeout) must not pin ""/0 — or worse, a
    /// faulted task — for the daemon's whole lifetime; the old shape re-probed every call and so
    /// self-healed, and this keeps that property while still reaching zero steady-state spawns
    /// (review finding).
    /// </summary>
    public sealed class ProbedFact<T>
    {
        private readonly Func<Task<T>> _probe;
        private readonly Func<T, bool> _isSuccess;
        private volatile Task<T>? _cached;

        public ProbedFact(Func<Task<T>> probe, Func<T, bool> isSuccess)
        {
            _probe = probe;
            _isSuccess = isSuccess;
        }

        public async Task<T> GetAsync()
        {
            var cached = _cached;
            if (cached is not null)
            {
                return await cached.ConfigureAwait(false);
            }

            // Concurrent first callers may each probe; the probes are idempotent and the last
            // successful one wins, which is fine for values that cannot change.
            var value = await _probe().ConfigureAwait(false);
            if (_isSuccess(value))
            {
                _cached = Task.FromResult(value);
            }

            return value;
        }
    }

    /// <summary>
    /// Runs <paramref name="fileName"/> and returns its trimmed stdout, or <c>""</c> on any failure.
    /// <paramref name="timeout"/> genuinely bounds the whole thing — the read <em>and</em> the exit —
    /// and a child that never closes stdout is killed (with its tree) rather than parked on a thread.
    /// </summary>
    public static async Task<string> ReadCommandOutputAsync(string fileName, string arguments, TimeSpan timeout)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            process = Process.Start(psi);
            if (process is null)
            {
                return "";
            }

            using var cts = new CancellationTokenSource(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return output.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            return "";
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
                {
                    // Already gone, or unkillable — nothing left to bound. AggregateException is
                    // Kill(entireProcessTree)'s partial-failure shape; thrown from this finally it
                    // would replace the catch's return "" and poison the caller (review finding).
                }

                process.Dispose();
            }
        }
    }
}
