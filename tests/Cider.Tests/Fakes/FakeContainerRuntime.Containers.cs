using System.Formats.Tar;
using System.Text;
using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

/// <summary>
/// The container/process half of the fake engine (owner: core-containers): an in-memory container
/// table whose init and exec processes are <see cref="FakeProcess"/>es.
/// </summary>
public sealed partial class FakeContainerRuntime
{
    private readonly Dictionary<string, FakeContainer> _containerTable = new(StringComparer.Ordinal);
    private int _nextAddress = 5;

    // The image/network/volume partials (owner: core-resources) enumerate `_containers` as engine
    // containers; this projection keeps that view working over the mutable table.
    private IReadOnlyList<RuntimeContainer> _containers => [.. _containerTable.Values.Select(Project)];

    /// <summary>Subnet the fake hands out container addresses from (matches the fake <c>default</c> network).</summary>
    public const string NetworkPrefix = "192.168.64.";

    /// <summary>Gateway of the fake <c>default</c> network.</summary>
    public const string NetworkGateway = "192.168.64.1";

    /// <summary>Test hook: fails the next <see cref="CreateContainerAsync"/> with this error.</summary>
    public RuntimeException? CreateFailure { get; set; }

    /// <summary>Test hook: fails the next <see cref="StartContainerAsync"/> with this error.</summary>
    public RuntimeException? StartFailure { get; set; }

    /// <summary>
    /// Test hook: runs once <see cref="RemoveContainerAsync"/> has taken a container out of the
    /// table, for tests that need something to happen in the window between a remove and the
    /// re-create that follows it.
    /// </summary>
    public Action? AfterRemove { get; set; }

    /// <summary>
    /// Test hook: how many <see cref="CopyToContainerAsync"/> calls still answer "is not running"
    /// before one succeeds. Apple <c>container cp</c> keeps saying that for a moment after
    /// <c>container start</c> has already handed the init process over.
    /// </summary>
    public int CopyToNotRunningFailures { get; set; }

    /// <summary>Stats handed out by <see cref="GetStatsAsync"/>; <c>null</c> means "unavailable".</summary>
    public RuntimeStats? Stats { get; set; } = new()
    {
        MemoryUsageBytes = 32 * 1024 * 1024,
        MemoryLimitBytes = 2L * 1024 * 1024 * 1024,
        CpuUsageUsec = 1_500_000,
        NetworkRxBytes = 1024,
        NetworkTxBytes = 2048,
        BlockReadBytes = 4096,
        BlockWriteBytes = 8192,
        NumProcesses = 3,
        ReadAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>Test-only helper: injects a container without going through <see cref="CreateContainerAsync"/>.</summary>
    public void SeedContainer(RuntimeContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        lock (_sync)
        {
            _containerTable[container.RuntimeId] = new FakeContainer
            {
                Spec = new ContainerSpec { RuntimeId = container.RuntimeId, Image = container.ImageReference },
                Seeded = container,
                State = container.State,
                CreatedAt = container.CreatedAt ?? DateTimeOffset.UtcNow,
                StartedAt = container.StartedAt,
            };
        }
    }

    /// <summary>
    /// Test-only helper: drops a container from <see cref="ListContainersAsync"/>/
    /// <see cref="InspectContainerAsync"/> without touching any held process — the way Apple's
    /// runtime loses track of a container when its services restart (<c>container ls -a</c> goes
    /// empty while a process cider is already piped to keeps running), as opposed to
    /// <see cref="RemoveContainerAsync"/>, which also kills the process the way
    /// <c>container delete -f</c> would.
    /// </summary>
    public void VanishContainer(string runtimeId)
    {
        lock (_sync)
        {
            _containerTable.Remove(runtimeId);
        }
    }

    /// <summary>The fake's view of one container, for assertions.</summary>
    public FakeContainer? GetContainer(string runtimeId)
    {
        lock (_sync)
        {
            return _containerTable.GetValueOrDefault(runtimeId);
        }
    }

    /// <summary>The spec the daemon created a container with, for assertions.</summary>
    public ContainerSpec? GetSpec(string runtimeId) => GetContainer(runtimeId)?.Spec;

    /// <summary>Every exec process the daemon started, in order.</summary>
    public List<FakeProcess> ExecProcesses { get; } = [];

    /// <summary>
    /// Test-only helper: adds an image to the fake registry (the image table itself lives in the
    /// <c>.Images.cs</c> partial). Used by container tests that need an image with an entrypoint.
    /// </summary>
    public void SeedImage(RuntimeImageDetail image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_sync)
        {
            _images.RemoveAll(existing => existing.References.Intersect(image.References, StringComparer.Ordinal).Any());
            _images.Add(image);
        }
    }

    /// <inheritdoc />
    public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct)
    {
        Record("GetInfoAsync");
        return Task.FromResult(new RuntimeInfo
        {
            Name = "apple-container",
            Version = "1.2.2",
            KernelVersion = "6.18.15",
            Ready = true,
            AppRoot = "/tmp/fake-apple-container",
        });
    }

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken ct)
    {
        Record("EnsureReadyAsync");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Like the real runtime, which runs `container create` as a child process: a cancelled
        // token means no container is created.
        ct.ThrowIfCancellationRequested();
        Record($"CreateContainerAsync:{spec.RuntimeId}");

        if (CreateFailure is { } failure)
        {
            CreateFailure = null;
            throw failure;
        }

        lock (_sync)
        {
            if (_containerTable.ContainsKey(spec.RuntimeId))
            {
                throw RuntimeException.Conflict($"container {spec.RuntimeId} already exists");
            }

            _containerTable[spec.RuntimeId] = new FakeContainer
            {
                Spec = spec,
                State = RuntimeContainerState.Created,
                CreatedAt = DateTimeOffset.UtcNow,
                Address = NetworkPrefix + _nextAddress++,
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct)
    {
        Record($"StartContainerAsync:{runtimeId}");

        if (StartFailure is { } failure)
        {
            StartFailure = null;
            throw failure;
        }

        FakeContainer container;
        lock (_sync)
        {
            container = Require(runtimeId);
            if (container.State == RuntimeContainerState.Running &&
                container.Process is { } previous && !previous.Exited.IsCompleted)
            {
                throw RuntimeException.Conflict($"container {runtimeId} is already running");
            }
        }

        var argv = new List<string>();
        if (!string.IsNullOrEmpty(container.Spec.Entrypoint))
        {
            argv.Add(container.Spec.Entrypoint);
        }

        argv.AddRange(container.Spec.Args);

        var process = new FakeProcess(argv, container.Spec.Env, container.Spec.Tty, options?.AttachStdin ?? false);

        lock (_sync)
        {
            container.Process = process;
            container.State = RuntimeContainerState.Running;
            container.StartedAt = DateTimeOffset.UtcNow;

            // A bind mount of a single host file shows up inside the container at its target, which
            // is how the daemon puts files copied into a not-yet-running container in place (Apple
            // `container -v` takes a file and creates the directories above it).
            foreach (var mount in container.Spec.Mounts)
            {
                if (mount.Kind == MountKind.Bind && File.Exists(mount.Source))
                {
                    container.Files[mount.Target] = File.ReadAllBytes(mount.Source);
                }
            }
        }

        _ = process.Exited.ContinueWith(
            _ =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(container.Process, process))
                    {
                        container.State = RuntimeContainerState.Stopped;
                    }
                }
            },
            TaskScheduler.Default);

        return Task.FromResult<IContainerProcess>(process);
    }

    /// <inheritdoc />
    public async Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct)
    {
        Record($"StopContainerAsync:{runtimeId}:{signal ?? "SIGTERM"}");

        FakeProcess? process;
        lock (_sync)
        {
            process = Require(runtimeId).Process;
        }

        if (process is null)
        {
            return;
        }

        await process.KillAsync(string.IsNullOrEmpty(signal) ? "SIGTERM" : signal, ct);
    }

    /// <inheritdoc />
    public async Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct)
    {
        Record($"KillContainerAsync:{runtimeId}:{signal}");

        FakeProcess? process;
        lock (_sync)
        {
            process = Require(runtimeId).Process;
        }

        if (process is null)
        {
            throw NotRunning(runtimeId);
        }

        await process.KillAsync(signal, ct);
    }

    /// <inheritdoc />
    public async Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct)
    {
        Record($"RemoveContainerAsync:{runtimeId}:{force}");

        FakeContainer container;
        lock (_sync)
        {
            container = Require(runtimeId);
            if (container.State == RuntimeContainerState.Running && !force)
            {
                throw RuntimeException.Conflict($"container {runtimeId} is running");
            }
        }

        if (container.Process is { } process)
        {
            await process.KillAsync("SIGKILL", ct);
            await process.DisposeAsync();
        }

        lock (_sync)
        {
            _containerTable.Remove(runtimeId);
        }

        AfterRemove?.Invoke();
    }

    /// <summary>Test hook: fails the next <see cref="ListContainersAsync"/> with this error.</summary>
    public RuntimeException? ListContainersFailure { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct)
    {
        Record("ListContainersAsync");

        if (ListContainersFailure is { } failure)
        {
            ListContainersFailure = null;
            throw failure;
        }

        lock (_sync)
        {
            IReadOnlyList<RuntimeContainer> list = [.. _containerTable.Values.Select(Project)];
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct)
    {
        Record($"InspectContainerAsync:{runtimeId}");
        lock (_sync)
        {
            var container = _containerTable.GetValueOrDefault(runtimeId);
            if (container is null)
            {
                return Task.FromResult<RuntimeContainer?>(null);
            }

            var projected = Project(container);
            if (ShouldDelayNetworkAttachment(runtimeId))
            {
                projected = projected with { Networks = [] };
            }

            return Task.FromResult<RuntimeContainer?>(projected);
        }
    }

    /// <summary>
    /// Unlike the CLI transport (which cannot wait at all), the fake genuinely waits — it completes
    /// when the container's held process exits, the way the XPC apiserver's <c>containerWait</c>
    /// blocks (docs/spikes/xpc/02-apiserver-xpc-protocol.md §8.6). <c>null</c> only when the
    /// container has no held process at all (never started).
    /// </summary>
    public async Task<(int ExitCode, DateTimeOffset ExitedAt)?> WaitContainerAsync(string runtimeId, CancellationToken ct)
    {
        Record($"WaitContainerAsync:{runtimeId}");

        FakeProcess? process;
        lock (_sync)
        {
            process = Require(runtimeId).Process;
        }

        if (process is null)
        {
            return null;
        }

        var exitCode = await process.Exited.WaitAsync(ct);
        return (exitCode, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Record($"ExecAsync:{runtimeId}:{string.Join(' ', spec.Argv)}");

        lock (_sync)
        {
            var container = Require(runtimeId);
            if (container.State != RuntimeContainerState.Running)
            {
                throw NotRunning(runtimeId);
            }

            if (ShouldFailExecAsNotRunning(runtimeId))
            {
                throw NotRunning(runtimeId);
            }
        }

        if (ExecFactory is { } factory)
        {
            return Task.FromResult(factory(spec));
        }

        var process = new FakeProcess(spec.Argv, spec.Env, spec.Tty, spec.OpenStdin);
        lock (_sync)
        {
            ExecProcesses.Add(process);
        }

        return Task.FromResult<IContainerProcess>(process);
    }

    /// <inheritdoc />
    public Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct)
    {
        Record($"OpenLogsAsync:{runtimeId}");
        lock (_sync)
        {
            var container = Require(runtimeId);
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(container.RuntimeLog)));
        }
    }

    /// <inheritdoc />
    public Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct)
    {
        Record($"GetStatsAsync:{runtimeId}");
        lock (_sync)
        {
            Require(runtimeId);
        }

        return Task.FromResult(Stats);
    }

    /// <inheritdoc />
    public Task CopyFromContainerAsync(string runtimeId, string containerPath, string localDestinationDir, CancellationToken ct)
    {
        Record($"CopyFromContainerAsync:{runtimeId}:{containerPath}");

        // Apple `container cp <name>:/ …` refuses the container root outright, before it even looks
        // at the container, and refuses one that is not running (1.2.2, probed for).
        if (containerPath.Trim().Trim('/').Length == 0)
        {
            throw RuntimeException.InvalidArgument($"source path has no last component: {containerPath}");
        }

        byte[]? content;
        lock (_sync)
        {
            var container = Require(runtimeId);
            RequireRunning(runtimeId, container);
            content = container.Files.GetValueOrDefault(containerPath);
        }

        if (content is null)
        {
            throw RuntimeException.NotFound($"{containerPath}: no such file or directory");
        }

        Directory.CreateDirectory(localDestinationDir);
        var name = Path.GetFileName(containerPath.TrimEnd('/'));
        File.WriteAllBytes(Path.Combine(localDestinationDir, name.Length > 0 ? name : "root"), content);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CopyToContainerAsync(string runtimeId, string localSourcePath, string containerPath, CancellationToken ct)
    {
        Record($"CopyToContainerAsync:{runtimeId}:{containerPath}");

        var name = Path.GetFileName(localSourcePath.TrimEnd('/'));
        var target = containerPath.TrimEnd('/') + "/" + name;

        lock (_sync)
        {
            var container = Require(runtimeId);

            if (CopyToNotRunningFailures > 0)
            {
                CopyToNotRunningFailures--;
                throw NotRunning(runtimeId);
            }

            // THE WALL documented by the empirical gap report: Apple `container cp` into a container that
            // is not running fails with `invalidState: "container … is not running"`, which is
            // exactly what Aspire/DCP does between create and start.
            RequireRunning(runtimeId, container);

            if (Directory.Exists(localSourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(localSourcePath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(localSourcePath, file).Replace(Path.DirectorySeparatorChar, '/');
                    container.Files[target + "/" + relative] = File.ReadAllBytes(file);
                }
            }
            else
            {
                container.Files[target] = File.Exists(localSourcePath) ? File.ReadAllBytes(localSourcePath) : [];
            }
        }

        return Task.CompletedTask;
    }

    private static void RequireRunning(string runtimeId, FakeContainer container)
    {
        if (container.State != RuntimeContainerState.Running)
        {
            throw NotRunning(runtimeId);
        }
    }

    /// <summary>
    /// The fake refuses a container that is not running the way a runtime should: by raising the
    /// typed <see cref="RuntimeErrorReason.ContainerNotRunning"/>. The wording deliberately avoids
    /// Apple's <c>is not running</c> phrasing, so any code above the seam that went back to matching
    /// on message text would fail these tests instead of passing them by accident.
    /// </summary>
    private static RuntimeException NotRunning(string runtimeId) => RuntimeException.ContainerNotRunning(
        $"fake runtime refused the operation: container {runtimeId} is stopped");

    /// <summary>
    /// Exports the container's root filesystem, the way Apple <c>container export</c> does: a tar of
    /// everything in it, with relative entry names and no leading <c>./</c> — and, importantly, for a
    /// container that is NOT running as readily as for one that is (probed on 1.2.2).
    /// Anything <see cref="CopyToContainerAsync"/> or a mount put in the container is in it.
    /// </summary>
    public async Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct)
    {
        Record($"ExportContainerAsync:{runtimeId}");
        Dictionary<string, byte[]> files;
        lock (_sync)
        {
            files = new Dictionary<string, byte[]>(Require(runtimeId).Files, StringComparer.Ordinal);
        }

        await using (var writer = new TarWriter(tarOutput, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                await writer.WriteEntryAsync(
                    new PaxTarEntry(TarEntryType.RegularFile, path.TrimStart('/'))
                    {
                        DataStream = new MemoryStream(content),
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    },
                    ct);
            }
        }

        await tarOutput.WriteAsync(new byte[1024], ct);
    }

    private FakeContainer Require(string runtimeId) =>
        _containerTable.TryGetValue(runtimeId, out var container)
            ? container
            : throw RuntimeException.NotFound($"container {runtimeId} not found");

    private static RuntimeContainer Project(FakeContainer container)
    {
        if (container.Seeded is { } seeded)
        {
            return seeded with { State = container.State };
        }

        var spec = container.Spec;
        var argv = new List<string>();
        if (!string.IsNullOrEmpty(spec.Entrypoint))
        {
            argv.Add(spec.Entrypoint);
        }

        argv.AddRange(spec.Args);

        return new RuntimeContainer
        {
            RuntimeId = spec.RuntimeId,
            State = container.State,
            ImageReference = spec.Image,
            ImageDigest = "sha256:" + new string('a', 64),
            Labels = spec.Labels,
            Networks =
            [
                .. spec.Networks.Select(network => new RuntimeNetworkAttachment
                {
                    Network = network,
                    Hostname = spec.Hostname,
                    IPv4Address = container.Address,
                    IPv4Gateway = NetworkGateway,
                    MacAddress = "02:42:c0:a8:40:05",
                }),
            ],
            PublishedPorts = spec.Ports,
            Mounts = spec.Mounts,
            Platform = spec.Platform,
            Argv = argv,
            Env = spec.Env,
            WorkingDir = spec.WorkingDir,
            Tty = spec.Tty,
            Cpus = spec.Cpus,
            MemoryBytes = spec.MemoryBytes,
            CreatedAt = container.CreatedAt,
            StartedAt = container.StartedAt,
        };
    }

    /// <summary>One container in the fake engine.</summary>
    public sealed class FakeContainer
    {
        /// <summary>The spec the daemon created it with.</summary>
        public required ContainerSpec Spec { get; init; }

        /// <summary>Set when the container was injected with <c>SeedContainer</c>.</summary>
        public RuntimeContainer? Seeded { get; init; }

        /// <summary>Current lifecycle state.</summary>
        public RuntimeContainerState State { get; set; }

        /// <summary>The held init process, when running.</summary>
        public FakeProcess? Process { get; set; }

        /// <summary>Creation time.</summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>Last start time.</summary>
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>The address the fake network handed out.</summary>
        public string Address { get; set; } = NetworkPrefix + "5";

        /// <summary>What <c>OpenLogsAsync</c> returns (the engine's own log, used only as a fallback).</summary>
        public string RuntimeLog { get; set; } = "";

        /// <summary>Files placed by <c>docker cp</c>, keyed by container path.</summary>
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    }
}
