using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cider.Core.Configuration;

/// <summary>
/// The daemon's configuration: <c>&lt;DataDir&gt;/config.json</c> overlaid by <c>CIDER_*</c>
/// environment variables and finally by explicit command line overrides.
/// </summary>
public sealed partial class CiderOptions
{
    /// <summary>Unexpanded default data directory.</summary>
    public const string DefaultDataDir = "~/.cider";

    /// <summary>
    /// Transitional (rename to Cider): the default data directory used before the rename. It is
    /// never read or written — it only feeds <see cref="LegacyDataDirHint(string)"/>, so a daemon
    /// started after the rename tells the user how to move their state instead of silently starting
    /// empty. Delete once nobody has a <c>~/.apple-demon</c> left.
    /// </summary>
    public const string LegacyDefaultDataDir = "~/.apple-demon";

    /// <summary>The unix socket path length limit of <c>sockaddr_un.sun_path</c> on macOS.</summary>
    public const int MaxSocketPathLength = 104;

    /// <summary>Name of the config file inside <see cref="DataDir"/>.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary><see cref="PortPublishing"/>: the daemon binds the host port and forwards itself.</summary>
    public const string ProxyPortPublishing = "proxy";

    /// <summary><see cref="PortPublishing"/>: hand <c>-p</c> to Apple <c>container</c> and let it forward.</summary>
    public const string ApplePortPublishing = "apple";

    private string _dataDir = ExpandHome(DefaultDataDir);
    private string _socketPath = "";

    /// <summary>Root of everything the daemon persists (default <c>~/.cider</c>, expanded).</summary>
    public string DataDir
    {
        get => _dataDir;
        set => _dataDir = ExpandHome(value);
    }

    /// <summary>The unix socket the daemon listens on; defaults to <c>&lt;DataDir&gt;/docker.sock</c>.</summary>
    public string SocketPath
    {
        get => string.IsNullOrEmpty(_socketPath) ? Path.Combine(DataDir, "docker.sock") : _socketPath;
        set => _socketPath = ExpandHome(value ?? "");
    }

    /// <summary>Path of the Apple <c>container</c> CLI (or a bare name resolved through <c>PATH</c>).</summary>
    public string ContainerCliPath { get; set; } = "container";

    /// <summary>CPU count handed to containers that do not ask for a specific amount.</summary>
    public double DefaultCpus { get; set; } = 2;

    /// <summary>Memory handed to containers that do not ask for a specific amount.</summary>
    public long DefaultMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Whether the built-in DNS server runs and containers get <c>--dns</c>.</summary>
    public bool DnsEnabled { get; set; } = true;

    /// <summary>
    /// Listen endpoint of the built-in DNS server. Port 53 on the host is taken by macOS/vmnet once
    /// Apple's container network is up, so the daemon listens high and a forwarder container relays.
    /// </summary>
    public string DnsListen { get; set; } = "0.0.0.0:10053";

    /// <summary>Image used for the in-network DNS forwarder that relays to <see cref="DnsListen"/>.</summary>
    public string DnsForwarderImage { get; set; } = "docker.io/coredns/coredns:1.14.7";

    /// <summary>Upstream resolvers for names the daemon does not know.</summary>
    public string[] DnsUpstreams { get; set; } = ["1.1.1.1:53", "8.8.8.8:53"];

    /// <summary>Optional <c>--dns-search</c> domain handed to containers.</summary>
    public string DnsSearchDomain { get; set; } = "";

    /// <summary>
    /// How published ports carry traffic: <see cref="ProxyPortPublishing"/> (the daemon binds the
    /// host port itself and proxies to the container's VM address) or <see cref="ApplePortPublishing"/>
    /// (pass <c>-p</c> to Apple <c>container</c> and let its own forwarder do the work). Anything
    /// other than <c>apple</c> means <c>proxy</c>.
    /// </summary>
    public string PortPublishing { get; set; } = ProxyPortPublishing;

    /// <summary><c>true</c> when the daemon publishes ports itself instead of passing <c>-p</c> through.</summary>
    public bool UseProxyPortPublishing =>
        !string.Equals(PortPublishing, ApplePortPublishing, StringComparison.OrdinalIgnoreCase);

    /// <summary>How often the state poller reconciles with the engine.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Cap for one container's captured log file; the file is truncated when it is exceeded.</summary>
    public long LogMaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Minimum log level of the daemon itself.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>The Docker Engine API version the daemon advertises.</summary>
    public string ApiVersion { get; } = "1.47";

    /// <summary>The lowest Docker Engine API version the daemon accepts.</summary>
    public string MinApiVersion { get; } = "1.24";

    /// <summary>The engine version reported to clients (must parse as a plain numeric version).</summary>
    public string EngineVersion { get; } = "29.0.0";

    /// <summary>Directory holding the JSON record stores.</summary>
    public string StateDir => Path.Combine(DataDir, "state");

    /// <summary>Directory holding per-container captured logs.</summary>
    public string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Directory backing named/anonymous volumes (informational for Docker's <c>Mountpoint</c>).</summary>
    public string VolumesDir => Path.Combine(DataDir, "volumes");

    /// <summary>Scratch directory for build contexts and <c>docker cp</c> staging.</summary>
    public string TmpDir => Path.Combine(DataDir, "tmp");

    /// <summary>Loads the configuration and creates the directory tree it describes.</summary>
    public static CiderOptions Load(string? dataDirOverride, string? socketOverride, string? logLevelOverride)
    {
        var dataDir = FirstNonEmpty(
            dataDirOverride,
            Environment.GetEnvironmentVariable("CIDER_DATA_DIR"),
            DefaultDataDir);

        var options = new CiderOptions { DataDir = dataDir };

        var configPath = Path.Combine(options.DataDir, ConfigFileName);
        if (File.Exists(configPath))
        {
            options.ApplyFile(configPath);
            // The file may relocate the data dir; an explicit override still wins.
            if (!string.IsNullOrEmpty(dataDirOverride))
            {
                options.DataDir = ExpandHome(dataDirOverride);
            }
        }

        var envSocket = Environment.GetEnvironmentVariable("CIDER_SOCKET");
        if (!string.IsNullOrEmpty(envSocket))
        {
            options.SocketPath = envSocket;
        }

        var envLogLevel = Environment.GetEnvironmentVariable("CIDER_LOG_LEVEL");
        if (!string.IsNullOrEmpty(envLogLevel))
        {
            options.LogLevel = envLogLevel;
        }

        var envCli = Environment.GetEnvironmentVariable("CIDER_CONTAINER_CLI");
        if (!string.IsNullOrEmpty(envCli))
        {
            options.ContainerCliPath = envCli;
        }

        var envPortPublishing = Environment.GetEnvironmentVariable("CIDER_PORT_PUBLISHING");
        if (!string.IsNullOrEmpty(envPortPublishing))
        {
            options.PortPublishing = envPortPublishing;
        }

        if (!string.IsNullOrEmpty(socketOverride))
        {
            options.SocketPath = socketOverride;
        }

        if (!string.IsNullOrEmpty(logLevelOverride))
        {
            options.LogLevel = logLevelOverride;
        }

        options.Validate();
        // Computed before the directory tree is created, which would otherwise hide the evidence.
        options.MigrationHint = LegacyDataDirHint(options.DataDir);
        options.EnsureDirectories();
        return options;
    }

    /// <summary>
    /// Transitional (rename to Cider): a loud <c>mv</c> hint when the pre-rename data directory
    /// still holds the user's state and this one does not, otherwise <c>null</c>. Set by
    /// <see cref="Load"/>; the daemon prints it at startup. Nothing is ever moved automatically.
    /// </summary>
    public string? MigrationHint { get; private set; }

    /// <summary>Transitional (rename to Cider): see <see cref="MigrationHint"/>.</summary>
    public static string? LegacyDataDirHint(string dataDir) =>
        LegacyDataDirHint(dataDir, ExpandHome(DefaultDataDir), ExpandHome(LegacyDefaultDataDir));

    /// <summary>Transitional (rename to Cider): see <see cref="MigrationHint"/>; exposed for tests.</summary>
    public static string? LegacyDataDirHint(string dataDir, string defaultDataDir, string legacyDataDir)
    {
        // Only the default location can have been superseded by the rename; an explicit --data-dir
        // or CIDER_DATA_DIR says exactly which directory the user means.
        if (!string.Equals(dataDir, defaultDataDir, StringComparison.Ordinal) || !Directory.Exists(legacyDataDir))
        {
            return null;
        }

        // A directory this run just created (or an earlier run created and never filled) is not
        // state — the user's records would still be in the pre-rename directory.
        if (Directory.Exists(defaultDataDir) && HasState(defaultDataDir))
        {
            return null;
        }

        // The daemon creates its (empty) directory tree on every start, so a bare `mv` would nest the
        // old directory inside the new one; clearing the empty tree first is what actually works.
        return $"cider: {legacyDataDir} is a data directory from before the rename, and {defaultDataDir} holds no state."
            + Environment.NewLine
            + "cider: your containers, networks and volumes are recorded in the old one. To keep them, stop cider and run:"
            + Environment.NewLine
            + $"cider:     rm -rf {defaultDataDir} && mv {legacyDataDir} {defaultDataDir}";
    }

    private static bool HasState(string dataDir)
    {
        var stateDir = Path.Combine(dataDir, "state");
        return Directory.Exists(stateDir) && Directory.EnumerateFileSystemEntries(stateDir).Any();
    }

    /// <summary>Creates <see cref="DataDir"/> and every directory derived from it.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(VolumesDir);
        Directory.CreateDirectory(TmpDir);
        var socketDir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(socketDir))
        {
            Directory.CreateDirectory(socketDir);
        }
    }

    /// <summary>Throws when the configuration cannot work (currently: an over-long socket path).</summary>
    public void Validate()
    {
        if (SocketPath.Length > MaxSocketPathLength)
        {
            throw new InvalidOperationException(
                $"socket path '{SocketPath}' is {SocketPath.Length} characters; the limit is {MaxSocketPathLength}");
        }
    }

    /// <summary>Serializes the settings that <see cref="Load"/> reads back from <c>config.json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(FileModel.From(this), FileJsonContext.Default.FileModel);

    private void ApplyFile(string path)
    {
        FileModel? model;
        try
        {
            model = JsonSerializer.Deserialize(File.ReadAllText(path), FileJsonContext.Default.FileModel);
        }
        catch (JsonException)
        {
            // A broken config file must never stop the daemon from starting.
            return;
        }

        if (model is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(model.DataDir))
        {
            DataDir = model.DataDir;
        }

        if (!string.IsNullOrEmpty(model.SocketPath))
        {
            SocketPath = model.SocketPath;
        }

        if (!string.IsNullOrEmpty(model.ContainerCliPath))
        {
            ContainerCliPath = model.ContainerCliPath;
        }

        if (!string.IsNullOrEmpty(model.DnsForwarderImage))
        {
            DnsForwarderImage = model.DnsForwarderImage;
        }

        if (model.DefaultCpus is > 0)
        {
            DefaultCpus = model.DefaultCpus.Value;
        }

        if (model.DefaultMemoryBytes is > 0)
        {
            DefaultMemoryBytes = model.DefaultMemoryBytes.Value;
        }

        if (model.Dns is not null)
        {
            DnsEnabled = model.Dns.Enabled ?? DnsEnabled;
            if (!string.IsNullOrEmpty(model.Dns.Listen))
            {
                DnsListen = model.Dns.Listen;
            }

            if (model.Dns.Upstream is { Length: > 0 })
            {
                DnsUpstreams = model.Dns.Upstream;
            }

            if (model.Dns.SearchDomain is not null)
            {
                DnsSearchDomain = model.Dns.SearchDomain;
            }
        }

        if (!string.IsNullOrEmpty(model.PortPublishing))
        {
            PortPublishing = model.PortPublishing;
        }

        if (model.PollIntervalSeconds is > 0)
        {
            PollIntervalSeconds = model.PollIntervalSeconds.Value;
        }

        if (model.LogMaxBytes is > 0)
        {
            LogMaxBytes = model.LogMaxBytes.Value;
        }

        if (!string.IsNullOrEmpty(model.LogLevel))
        {
            LogLevel = model.LogLevel;
        }
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return DefaultDataDir;
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length <= 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    // config.json's contract, source-generated so the daemon carries no reflection-based
    // serializer into a Native AOT build. Every setting is what the hand-built
    // JsonSerializerOptions used to carry, in the same order.
    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(FileModel))]
    private sealed partial class FileJsonContext : JsonSerializerContext;

    private sealed class FileModel
    {
        public string? DataDir { get; set; }
        public string? SocketPath { get; set; }
        public string? ContainerCliPath { get; set; }
        public string? DnsForwarderImage { get; set; }
        public double? DefaultCpus { get; set; }
        public long? DefaultMemoryBytes { get; set; }
        public DnsModel? Dns { get; set; }
        public string? PortPublishing { get; set; }
        public int? PollIntervalSeconds { get; set; }
        public long? LogMaxBytes { get; set; }
        public string? LogLevel { get; set; }

        public static FileModel From(CiderOptions options) => new()
        {
            DataDir = options.DataDir,
            SocketPath = options.SocketPath,
            ContainerCliPath = options.ContainerCliPath,
            DnsForwarderImage = options.DnsForwarderImage,
            DefaultCpus = options.DefaultCpus,
            DefaultMemoryBytes = options.DefaultMemoryBytes,
            Dns = new DnsModel
            {
                Enabled = options.DnsEnabled,
                Listen = options.DnsListen,
                Upstream = options.DnsUpstreams,
                SearchDomain = options.DnsSearchDomain,
            },
            PortPublishing = options.PortPublishing,
            PollIntervalSeconds = options.PollIntervalSeconds,
            LogMaxBytes = options.LogMaxBytes,
            LogLevel = options.LogLevel,
        };
    }

    private sealed class DnsModel
    {
        public bool? Enabled { get; set; }
        public string? Listen { get; set; }
        public string[]? Upstream { get; set; }
        public string? SearchDomain { get; set; }
    }
}
