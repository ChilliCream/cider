<!-- Generated 2026-08-25 by a research agent during planning of the XPC runtime transport; source of truth is the apple/container 1.3.0 tree and the cider tree at that date. -->

# cider runtime layer — map for a CLI→XPC transport swap

Everything below is read-only analysis. All paths absolute; line numbers from the current working tree (note: `src/Cider.Daemon/BuildKit/BuilderLink.cs`, `IBuilderConnection.cs`, `BuilderUnavailableException.cs` are **untracked WIP** and reference a `BuilderConnection` class that does not exist yet).

---

## 1. The seam: `IContainerRuntime`, `IContainerProcess`, models, specs

### 1.1 `IContainerRuntime` — full member list
`/Users/michael/local/cider/src/Cider.Core/Runtime/IContainerRuntime.cs`

| # | Signature | Line |
|---|---|---|
| 1 | `Task<RuntimeInfo> GetInfoAsync(CancellationToken ct)` | :10 |
| 2 | `Task EnsureReadyAsync(CancellationToken ct)` | :13 |
| 3 | `Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct)` | :18 |
| 4 | `Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct)` | :21 |
| 5 | `Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct)` | :23 |
| 6 | `Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct)` | :25 |
| 7 | `Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct)` | :27 |
| 8 | `Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct)` | :30 |
| 9 | `Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct)` | :33 |
| 10 | `Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct)` | :35 |
| 11 | `Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct)` | :38 |
| 12 | `Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct)` | :40 |
| 13 | `Task CopyFromContainerAsync(string runtimeId, string containerPath, string localDestinationDir, CancellationToken ct)` | :42 |
| 14 | `Task CopyToContainerAsync(string runtimeId, string localSourcePath, string containerPath, CancellationToken ct)` | :44 |
| 15 | `Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct)` | :46 |
| 16 | `Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct)` | :50 |
| 17 | `Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct)` | :53 |
| 18 | `Task PullImageAsync(string reference, string? platform, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct)` | :55 |
| 19 | `Task PushImageAsync(string reference, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct)` | :57 |
| 20 | `Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct)` | :59 |
| 21 | `Task RemoveImageAsync(string reference, bool force, CancellationToken ct)` | :61 |
| 22 | `Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct)` | :63 |
| 23 | `Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct)` | :66 |
| 24 | `Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct)` | :69 |
| 25 | `Task LoginAsync(RegistryAuth auth, CancellationToken ct)` | :71 |
| 26 | `Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct)` | :75 |
| 27 | `Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct)` | :77 |
| 28 | `Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)` | :79 |
| 29 | `Task RemoveNetworkAsync(string name, CancellationToken ct)` | :81 |
| 30 | `Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct)` | :85 |
| 31 | `Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct)` | :87 |
| 32 | `Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct)` | :89 |
| 33 | `Task RemoveVolumeAsync(string name, bool force, CancellationToken ct)` | :91 |
| 34 | `Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct)` | :93 |
| 35 | `Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct)` | :99 |
| 36 | `Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct)` | :106 |
| 37 | `Task<IContainerProcess> DialBuilderAsync(CancellationToken ct)` | :114 |

37 members. Exactly two implementations: `AppleContainerRuntime` (`/Users/michael/local/cider/src/Cider.AppleContainer/AppleContainerRuntime.cs:18`) and `FakeContainerRuntime` (`/Users/michael/local/cider/tests/Cider.Tests/Fakes/FakeContainerRuntime.cs:12`).

### 1.2 `IContainerProcess`
`/Users/michael/local/cider/src/Cider.Core/Runtime/IContainerProcess.cs` — `IAsyncDisposable` (:4)

- `int? Pid { get; }` (:8) — host pid of the launched runtime process
- `bool HasTty { get; }` (:11) — stdout carries everything, stderr is `null`
- `Stream? Stdin { get; }` (:14)
- `Stream Stdout { get; }` (:17)
- `Stream? Stderr { get; }` (:20) — `null` in TTY mode
- `Task<int> Exited { get; }` (:23) — never throws; `-1` when unknown
- `Task CloseStdinAsync()` (:26) — Docker `CloseWrite`
- `Task ResizeAsync(int cols, int rows, CancellationToken ct)` (:29)
- `Task KillAsync(string signal, CancellationToken ct)` (:32)

Implementations: `CliProcess` (`/Users/michael/local/cider/src/Cider.AppleContainer/Process/CliProcess.cs:13`), the replay-only `CompletedExecProcess` (`AppleContainerRuntime.cs:388`), and `FakeProcess` (`/Users/michael/local/cider/tests/Cider.Tests/Fakes/FakeProcess.cs`).

### 1.3 `Specs.cs` — `/Users/michael/local/cider/src/Cider.Core/Runtime/Specs.cs`

**`ContainerSpec`** (:4-39): `RuntimeId`(:7, required), `Image`(:11, required), `Platform`(:12), `Entrypoint`(:13, single string), `Args`(:14), `Env`(:15), `WorkingDir`(:16), `User`(:17), `Tty`(:18), `OpenStdin`(:19), `Labels`(:20), `Mounts`(:21), `Ports`(:22), `Networks`(:23), `DnsServers`(:24), `DnsSearch`(:25), `DnsOptions`(:26), `Cpus`(:27, double?), `MemoryBytes`(:28), `CapAdd`(:29), `CapDrop`(:30), `Privileged`(:31), `ReadOnlyRootfs`(:32), `ShmSizeBytes`(:33), `Init`(:34), `Ulimits`(:35), `Tmpfs`(:36), `PublishSockets`(:37), `Hostname`(:38).

Other spec types: `MountKind{Bind,Volume,Tmpfs}`(:42-47); `MountSpec{Kind,Source,Target,ReadOnly}`(:50-59); `PortSpec{HostIp,HostPort,ContainerPort,Proto}`(:62-68); `UlimitSpec{Name,Soft,Hard}`(:71-76); `TmpfsSpec{Target,SizeBytes}`(:79-83); `StartOptions{AttachStdin}`(:86-89); `ExecSpec{Argv,Env,WorkingDir,User,Tty,OpenStdin,Privileged}`(:92-101); `NetworkSpec{Name,Subnet,SubnetV6,Internal,Labels,Options}`(:104-112); `VolumeSpec{Name,Labels,Options,SizeBytes}`(:115-121); `BuildSpec{ContextDir,Dockerfile,Tags,BuildArgs,Labels,Target,Platforms,NoCache,Pull,Quiet,Cpus,MemoryBytes}`(:124-141); `RegistryAuth{Username,Password,ServerAddress,IdentityToken}`(:144-150).

### 1.4 `RuntimeModels.cs` — `/Users/michael/local/cider/src/Cider.Core/Runtime/RuntimeModels.cs`

- `RuntimeContainerState { Created, Running, Stopping, Stopped, Unknown }` (:5-11)
- `RuntimeContainer` (:14-33): `RuntimeId`(required), `State`, `ImageReference`, `ImageDigest`, `Labels`, `Networks`, `PublishedPorts`, `Mounts`, `Platform`, `Argv`, `Env`, `WorkingDir`, `Tty`, `Cpus`, `MemoryBytes`, `CreatedAt`, `StartedAt`
- `RuntimeNetworkAttachment` (:36-43): `Network`, `Hostname`, `IPv4Address`, `IPv4Gateway`, `MacAddress`
- `RuntimeImage` (:46-58): `Id`(`sha256:…`), `References`, `Size`, `Created`, `Platforms`, `Labels`
- `RuntimeImageDetail : RuntimeImage` (:61-79): `+ Config`, `Architecture`, `Os`, `Variant`, `Layers`, `Author`, `RepoDigests`, `History`
- `RuntimeImageHistory` (:82-93); `ImageConfig` (:96-113: `Env, Cmd, Entrypoint, WorkingDir, User, ExposedPorts, Volumes, Labels, StopSignal, Healthcheck`); `HealthcheckConfig` (:116-123, ns durations)
- `RuntimeNetwork` (:126-137): `Name`, `Id`, `Mode`(default `"nat"`), `Subnet`, `Gateway`, `SubnetV6`, `Internal`, `Labels`, `Created`
- `RuntimeVolume` (:140-149): `Name`, `Driver`, `Labels`, `Options`, `Created`, `Mountpoint`, `SizeBytes`
- `RuntimeStats` (:152-163); `ProgressEvent{Status,Id,Current,Total,Stream,Error}` (:166-174); `RuntimeInfo{Name,Version,KernelVersion,Ready,AppRoot}` (:177-186); `RuntimeDiskUsage` (:189-198)
- `BuilderStatus{Name,Image,Running,Address,Cpus,MemoryBytes}` — `/Users/michael/local/cider/src/Cider.Core/Runtime/BuilderStatus.cs:7-23`
- `RuntimeErrorKind{NotFound,Conflict,InvalidArgument,NotSupported,Unavailable,Internal,Timeout}` and `RuntimeErrorReason{None,ContainerNotRunning}` — `/Users/michael/local/cider/src/Cider.Core/Runtime/RuntimeException.cs:4-42`. **Contract note at :26-27: "nothing above the `IContainerRuntime` seam may read an exception's message text."**

---

## 2. How `AppleContainerRuntime` implements every member

Runner kinds referenced below:
- **one-shot** = `ContainerCli.RunAsync` (`/Users/michael/local/cider/src/Cider.AppleContainer/Cli/ContainerCli.cs:34-107`): `Process`, redirected pipes, UTF-8, timeout via linked CTS, `CliResult(ExitCode, Stdout, Stderr)`
- **json** = `ContainerCli.RunJsonAsync` (:198-206) = one-shot + `ThrowIfFailed` + `ParseJson`
- **streaming** = `ContainerCli.RunStreamingAsync` (:113-195): per-line callback for stdout+stderr, stderr tail capped at 100 lines
- **held pipe** = `ProcessLauncher.StartPipe` (`/Users/michael/local/cider/src/Cider.AppleContainer/Process/ProcessLauncher.cs:37-84`) → `CliProcess`
- **held pty** = `ProcessLauncher.StartPty` (:102-198) → `CliProcess` with two ptys
- **stream-until-dispose** = `ProcessLauncher.StartStreaming` (:266-284) → `ProcessOutputStream`

Every public member is wrapped in `GuardAsync` (`AppleContainerRuntime.cs:663`, `:683`) which converts non-`RuntimeException`/non-cancel exceptions to `RuntimeErrorKind.Internal`.

| Member | CLI command (exact argv) | Runner kind | Parsed output | Notes |
|---|---|---|---|---|
| `GetInfoAsync` | `system status --format json` (`:127`), `--version` (`:76`), `system property list` (`:138`) | one-shot ×3 (60 s / 30 s / 30 s) | `AppleSystemStatus` (`Cli/Models/SystemModels.cs:7-27`); version via `VersionRegex` `(\d+\.\d+(\.\d+)?)` (`:63`); kernel via `KernelRegex` `vmlinux-(\d+\.\d+\.\d+)` (`:66`) | `Ready = status.IsRunning`(`:93`), `AppRoot = status.AppRoot`(`:94`); falls back to `status.ApiServerVersion` if `--version` fails (`:82-86`) |
| `EnsureReadyAsync` | `system start --enable-kernel-install` (`:118`, 300 s) | one-shot, preceded by `OrphanReaper` sweep (`:104`) | none — `ThrowIfFailed` | Logs `"Apple container services are not running; starting them"` (`:116`); no-op when `status.IsRunning` (`:111`) |
| `CreateContainerAsync` | `ArgBuilder.Create(spec)` → `create --name <id> …` (`:161`) | one-shot (`CommandTimeout` 5 min) | none | Caches `_ttyByContainer[RuntimeId]=spec.Tty` (`:164`); `spec.Hostname` is **dropped with a debug log** (`:154-159`) — no `--hostname` on the CLI |
| `StartContainerAsync` | `ArgBuilder.Start(id, attachStdin)` → `start -a [-i] <id>` (`:175`) | **held pty** when tty, else **held pipe** (`:180-182`) | none | `attachStdin = options.AttachStdin \|\| tty` (`:174`); tty from `HasTtyAsync` cache/inspect (`:627-640`); records `_startedAt[id]` for the exec race window (`:187`); signal delegate routes `KillAsync` back through `KillContainerAsync` (`:177`) |
| `StopContainerAsync` | `stop [-t N] [-s SIG] <id>` (`:196-209`) | one-shot; budget = `timeout+30 s` when `timeout>0` else `CommandTimeout` (`:212-216`) | none | Signal normalized by `ArgBuilder.NormalizeSignal` (`:206`) |
| `KillContainerAsync` | `kill -s <SIG> <id>` (`:227`) | one-shot | none | |
| `RemoveContainerAsync` | `delete [-f] <id>` (`:237-243`) | one-shot | none | Evicts `_ttyByContainer` / `_startedAt` (`:247-248`) |
| `ListContainersAsync` | `ls -a --format json` (`:254`) | json | `List<AppleContainerJson>` → `RuntimeMapper.ToContainer` (`:265`) | Empty array on `null` |
| `InspectContainerAsync` | `inspect <id>` (`:612`) | one-shot + `ParseJson<List<AppleContainerJson>>` (`:623`) | first element → `RuntimeMapper.ToContainer` | `null` when `CliErrorMapper.Classify(stderr)==NotFound` (`:615`) |
| `ExecAsync` | `ArgBuilder.Exec(id, spec)` → `exec [-i] [-t] [-e …] [-w …] [-u …] <id> <argv…>` (`:291`) | **held pty** when `spec.Tty`, else **held pipe** (`:293-295`) | on race path only: stdout/stderr drained (`:351-383`) and text tested by `CliErrorMapper.IsContainerNotRunning` | `spec.Privileged` ignored + debug log (`:286-289`). Inside a 5 s window after start (`ExecRaceWindow` `:23`) it probes for 300 ms (`:27`), retries up to 10× with 300 ms backoff (`:29-30`), then throws `RuntimeException.ContainerNotRunning` (`:340`). A genuine failure is replayed as `CompletedExecProcess` (`:333`) |
| `OpenLogsAsync` | `logs [-f] [-n N] <id>` (`:419-431`) | **stream-until-dispose** (`:434`) | raw bytes; no stream separation | `logs -f` never terminates — disposal kills the child (`ProcessOutputStream.cs:75-92`) |
| `GetStatsAsync` | `stats --format json --no-stream <id>` (`:442`, 60 s) | one-shot | `List<AppleStats>` → `RuntimeMapper.ToStats` (`:463`) | Returns `null` for classified `NotFound`/`Conflict` (`:449-451`) |
| `CopyFromContainerAsync` | `cp <id>:<path> <destDir>/` (`:490`) | one-shot with `CopyTimeout` (30 min) **plus** a destination-arrival watchdog (`WatchForCopyArrivalAsync` `:541-572`, `CopyIdleGrace` 10 s) | none | Missing guest path hangs forever on 1.2.2; the watchdog converts it into `RuntimeException.Timeout` with a README pointer (`:500-504`) |
| `CopyToContainerAsync` | `cp <localPath> <id>:<path>` (`:529`) | one-shot, `CopyTimeout` | none | No idle check — source is always daemon-staged (`:510-518`) |
| `ExportContainerAsync` | `export -o <tmp>.tar <id>` (`:596`, `PullTimeout`) | one-shot → temp file → `CopyToAsync` (`:599-600`) | none | Temp file in `AppleContainerOptions.TmpDir` (`NewTempFile` `:642`); no streaming export |
| `ListImagesAsync` | `image ls --format json` (`Images.cs:16`) | json | `List<AppleImageJson>` → `RuntimeMapper.ToImages` (grouped by id, `RuntimeMapper.cs:205-231`) | Apple emits one row per *reference*; merged to one image per digest |
| `InspectImageAsync` | `image inspect <ref>` (`Images.cs:30`) | one-shot + `ParseJson<List<AppleImageJson>>` (`:41`) | → `RecoverExposedPortsAsync` (`:47`) → `RuntimeMapper.ToImageDetail` (`:49`) → `WithSiblingReferencesAsync` (`:50`) | `null` on classified `NotFound` (`:33`). See §4 for the blob-store recovery |
| `PullImageAsync` | `[registry login …] image pull --progress plain [--platform P] <ref>` (`Images.cs:258-265`) | **streaming**, `PullTimeout` (30 min) | `ProgressParser.ParsePullLine` per line (`:277`) | Progress **buffered** until `IsPullUnderWay` (`:330-342`) so a 404 stays HTTP 404 (`:267-271`); never reports a terminal `Status:` line (`:320-322`); no `progress.Report` on error (`:308-313`) |
| `PushImageAsync` | `image push --progress plain <ref>` (`Images.cs:361`) | **streaming**, `PullTimeout` | `ProgressParser.ParsePullLine` | Emits its own `"The push refers to repository [ref]"` (`:358`) and reports the error event before throwing (`:376`) |
| `TagImageAsync` | `image tag <src> <dst>` (`Images.cs:387`) | one-shot | none | |
| `RemoveImageAsync` | `image delete [-f] <ref>` (`Images.cs:395-402`) | one-shot | none | Apple `-f` means "ignore not-found", **not** Docker's "remove anyway" (`:398-399`) |
| `SaveImagesAsync` | `image save -o <tmp>.tar <refs…>` (`Images.cs:422-425`) | one-shot, `PullTimeout` → file → stream | none | |
| `LoadImagesAsync` | `image load -i <tmp>.tar` (`Images.cs:451`) | one-shot, `PullTimeout` | **diffs `ListImagesAsync` before/after** (`:449`, `:454-462`); falls back to scraping stdout lines (`:468-475`) | The CLI does not report what it loaded |
| `BuildImageAsync` | `ArgBuilder.Build(spec, tags)` → `build --progress plain [-f DF] -t … [--build-arg k=v] [-l k=v] [--target] [--platform] [--no-cache] [--pull] [-q] [-c N] [-m NM] <ctx>` (`Images.cs:497`) | **streaming**, `PullTimeout` | each line → `ProgressEvent{Stream=line+"\n"}`; id from `ProgressParser.ParseBuiltImageId` (`:506`) | Mints `SyntheticBuildTag.New()` when no tag (`:493-495`); falls back to `InspectImageAsync(tags[0])` for the id (`:523`); deliberately does **not** emit `Successfully built/tagged` (`:532-534`) |
| `LoginAsync` | `registry login <host> -u <user> --password-stdin` + password on stdin (`Images.cs:551-554`) | one-shot with `stdin` | none | `NormalizeRegistry` strips scheme/path and folds `index.docker.io`/`registry-1.docker.io` → `docker.io` (`:561-591`) |
| `ListNetworksAsync` | `network ls --format json` (`Resources.cs:14`) | json | `List<AppleNetworkJson>` → `RuntimeMapper.ToNetwork` | |
| `InspectNetworkAsync` | `network inspect <name>` (`Resources.cs:33`) | one-shot + `ParseOneOrMany` (`:44`, `:148-158`) | → `RuntimeMapper.ToNetwork` | `null` on classified `NotFound` |
| `CreateNetworkAsync` | `ArgBuilder.CreateNetwork` → `network create [--internal] [--label k=v] [--option k=v] [--subnet] [--subnet-v6] <name>` (`Resources.cs:59`) | one-shot, **`ResourceTimeout` 30 s** (`:48-54`) | none | |
| `RemoveNetworkAsync` | `network delete <name>` (`Resources.cs:67`) | one-shot, `ResourceTimeout` | none | Missing vs. in-use both produce "failed to delete one or more…"; disambiguated in `CliErrorMapper` (`:69-73`, `CliErrorMapper.cs:128-132`) |
| `ListVolumesAsync` | `volume ls --format json` (`Resources.cs:80`) | json | `List<AppleVolumeJson>` → `RuntimeMapper.ToVolume` | |
| `InspectVolumeAsync` | `volume inspect <name>` (`Resources.cs:99`) | one-shot + `ParseOneOrMany` | → `RuntimeMapper.ToVolume` | |
| `CreateVolumeAsync` | `ArgBuilder.CreateVolume` → `volume create [--label k=v] [--opt k=v] [-s bytes] <name>` (`Resources.cs:118`) | one-shot, `ResourceTimeout` | none | |
| `RemoveVolumeAsync` | `volume delete <name>` (`Resources.cs:132`) | one-shot, `ResourceTimeout` | none | `force` ignored + debug log — no such flag on 1.2.2 (`:126-130`) |
| `GetDiskUsageAsync` | `system df --format json` (`Resources.cs:143`) | json | `AppleDiskUsage` → `RuntimeMapper.ToDiskUsage` (`RuntimeMapper.cs:526-535`) | `BuildCacheBytes` hardcoded 0 (`:531`) |
| `GetBuilderStatusAsync` | `builder status` (`Builder.cs:23`, 30 s) | one-shot | **plain-text table**, `ParseBuilderStatus` (`:71-132`), columns split on `\s{2,}` (`:134-135`), memory via `(\d+)\s*MB` (`:137-138`) | `null` when the failure classifies as `NotFound`/`Conflict` (`:29-33`) |
| `StartBuilderAsync` | `ArgBuilder.BuilderStart` → `builder start [-c N] [-m NM]` (`Builder.cs:43`) | one-shot, `PullTimeout` (shim image may need pulling, `:45-47`) | none | Tolerates stderr containing `"already running"` (`:49`, `:60-61`) |
| `DialBuilderAsync` | delegates to `ExecAsync("buildkit", {Argv=["buildctl","dial-stdio"], OpenStdin=true, Tty=false})` (`Builder.cs:55-58`) → `exec -i buildkit buildctl dial-stdio` | held pipe | raw duplex bytes | Caller must drain `Stderr` itself and not `CloseStdinAsync` early (`IContainerRuntime.cs:108-113`) |

### 2.1 Error mapping — `/Users/michael/local/cider/src/Cider.AppleContainer/Cli/CliErrorMapper.cs`

The CLI **always exits 1**, so classification is stderr-text-only (`:11-16`). `Classify` order (`:97-145`): `Unavailable` markers (`:31-40`, incl. `"apiserver is not running"`, `"xpc"`, `"connection refused"`) → usage banner ⇒ `InvalidArgument` (`:109-112`, banner marker `"--help' for more information"` `:85`) → `Conflict` markers (`:42-52`) → `NotFound` markers (`:54-60`, includes `"401 unauthorized"`) → the bare `"failed to delete one or more"` ⇒ `NotFound` (`:128-132`) → `InvalidArgument` markers (`:62-70`) → `NotSupported` (`:89-94`) → `Internal`.

`IsContainerNotRunning` (`:152-160`) is the **only** place the phrase `"is not running"` is turned into `RuntimeErrorReason.ContainerNotRunning` (`:163-166`). `ExtractMessage` (`:174-191`) takes the last meaningful stderr line minus the usage banner and the `Error: ` prefix. `ToException` (`:194-201`) builds `RuntimeException(kind, "$context: $message", reason)`.

Timeouts get their own message shape in `ContainerCli.TimedOut` (`ContainerCli.cs:245-249`) using `DescribeOperation` (the first ≤2 non-flag argv tokens, `:252-270`) → `RuntimeErrorKind.Timeout`.

---

## 3. Container lifecycle as cider drives it

### 3.1 Create — `ArgBuilder.Create` (`/Users/michael/local/cider/src/Cider.AppleContainer/Cli/ArgBuilder.cs:26-201`)

Emitted in this exact order (`create --name <RuntimeId>` first, `:28`):

| ContainerSpec field | Flag emitted | Line |
|---|---|---|
| `RuntimeId` | `--name <id>` | :28 |
| `Env[]` | `-e <entry>` per entry | :30-34 |
| `WorkingDir` | `-w <dir>` | :36-40 |
| `User` | `-u <user>` | :42-46 |
| `Tty` | `-t` | :48-51 |
| `OpenStdin` | `-i` | :53-56 |
| `Labels` | `-l k=v` per pair | :58-62 |
| `Mounts` Bind/Volume | `-v src:dst[:ro]` | :68-74 |
| `Mounts` Tmpfs | `--mount type=tmpfs,target=T[,readonly]` | :75-80 |
| `Tmpfs[]` | `--tmpfs <target>` — **`SizeBytes` dropped** | :86-91 |
| `Ports[]` | `-p` + `FormatPort` (`[ip:]host:container[/proto]`, ip omitted for `""`/`0.0.0.0`, `/proto` omitted for tcp) | :93-97, :414-422 |
| `Networks[]` | `--network <n>` per entry | :99-103 |
| `DnsServers[]` | `--dns <s>` | :105-109 |
| `DnsSearch[]` | `--dns-search <s>` | :111-115 |
| `DnsOptions[]` | `--dns-option <o>` | :117-121 |
| `Cpus` | `-c <int>` — `Math.Max(1, round(cpus))`, **fractional CPUs are lost** | :123-128 |
| `MemoryBytes` | `-m <N>M` via `FormatMebibytes` (round up, min 1 MiB) | :130-134, :425-434 |
| `CapAdd[]` | `--cap-add <c>` | :136-140 |
| `CapDrop[]` | `--cap-drop <c>` | :142-146 |
| `Privileged` | **emulated**: `--cap-add ALL --masked-path NONE --read-only-path NONE` | :148-156 |
| `Platform` | `--platform <p>` | :158-162 |
| `ReadOnlyRootfs` | `--read-only` | :164-167 |
| `ShmSizeBytes` | `--shm-size <N>M` | :169-173 |
| `Init` | `--init` | :175-178 |
| `Ulimits[]` | `--ulimit name=soft:hard` | :180-184 |
| `PublishSockets[]` | `--publish-socket <s>` — **never populated by any caller today** (only `ArgBuilder` reads it) | :186-190 |
| `Entrypoint` | `--entrypoint <e>` | :192-196 |
| `Image` then `Args…` | positional | :198-199 |
| `Hostname` | **not emitted at all** — dropped in `AppleContainerRuntime.cs:154-159` | — |

Never `--rm`, never `-d` (`:25`). Spec construction upstream: `ContainerManager.Create.cs:163-197` (fresh create) and `ContainerManager.Spec.cs:151-181` (`BuildSpecFromRecord`, used by network connect/disconnect re-create and by the staged-archive mount re-create).

### 3.2 Start — held `container start -a`

1. `StartContainerAsync` (`AppleContainerRuntime.cs:167-189`) resolves tty (cache first, else `inspect` → `configuration.initProcess.terminal`, `:627-640`), builds `start -a [-i] <id>` (`ArgBuilder.cs:204-214`).
2. **Non-tty** → `ProcessLauncher.StartPipe` (`ProcessLauncher.cs:37-84`): three redirected pipes; stdin disposed immediately when `openStdin` is false (`:58-68`).
3. **TTY** → `ProcessLauncher.StartPty` (`:102-198`): **two** ptys (input + output; a single shared pty re-enables `OPOST`/`ONLCR` and yields `\r\r\n`, `:90-100`), both fds `O_CLOEXEC` from `open` (`:113-119`), child launched as `/bin/sh -c 'exec "$0" "$@" 0<>{inPath} 1>{outPath}'` (`:145`) so nothing is inherited; **stderr stays an ordinary pipe on purpose** so Apple's terminal-only boot spinner is never emitted (`:96-99`) and is drained + logged by `DrainCliErrors` (`:243-263`). The parent keeps its own slave fds open for the child's lifetime (`:171-175`).
4. **Stdio capture**: `ContainerManager.StartAsync` (`ContainerManager.Lifecycle.cs:32-125`) stores the process on the handle (`:80`), opens `_logs.OpenWriter(record.Id)` (`:82`), and starts one `PumpAsync` per stream (`:84-88`). `PumpAsync` (`:517-549`) writes each chunk to the `ILogWriter` and `Broadcast`s to attachments.
5. **Exit code**: `CliProcess.Exited` = `Task.Run(WaitForExitAsync → _process.ExitCode)` (`CliProcess.cs:75-92`); the CLI child's exit code *is* the guest init's exit code (`:8-12`). `-1` on failure. `HandleExitAsync` (`ContainerManager.Lifecycle.cs:551-646`) awaits it, drains pumps with `PumpDrainTimeout` (5 s, `:18`), writes `State.ExitCode/FinishedAt`, publishes `die`, then auto-removes if `AutoRemove`.
6. **PTY teardown**: `CliProcess.ReleasePtyAsync` (`:326-359`) waits (up to `PtyDrainBudget` 5 s, `:16`) for `PendingBytes(master)==0` before closing the last slave — closing early makes Darwin discard queued bytes; `PtyStream` turns `EIO`/`ObjectDisposed` into a clean EOF (`Process/PtyStream.cs:5-11, 69-79, 154-163`).
7. **`CIDER_HELD` marker + `OrphanReaper`**: `OrphanReaper.HeldChildMarker = "CIDER_HELD"` (`Process/OrphanReaper.cs:39`), legacy `"APPLE_DEMON_HELD"` (`:46`). Stamped into the child env at `ProcessLauncher.cs:46` (pipe), `:142` (pty — survives `exec`), `:272` (streaming). `EnsureReadyAsync` sweeps at startup (`AppleContainerRuntime.cs:104-108`); the sweep reads `/bin/ps -axE -o pid=,ppid=,command=` (`OrphanReaper.cs:154`) and kills only rows with `ppid==1` **and** the marker **and** an argv starting `container start -a` / `start -i -a` / `start --attach` / `exec -i buildkit buildctl dial-stdio` (`:70-106`, `:127-150`). `ArgvOnly` (`:112-116`) strips the appended environment out of log lines. Killing the CLI child does **not** stop the container (`:22-27`) — reconcile adopts it.

### 3.3 Attach / exec — PTY vs pipe, multiplexed frames

- **Container attach**: `ContainerManager.AttachAsync` (`ContainerManager.Attach.cs:13-96`) creates a `ContainerAttachment` bound to `handle.Process`; `attachment.Tty = handle.Process?.HasTty ?? record.Request.Tty` (`:66`, `:132-150`). So **the runtime decides tty-ness** (`IContainerProcess.HasTty`), not the record.
- **Exec**: `ExecManager.StartAsync` (`Services/ExecManager.cs:82-126`) computes `useTty = tty || record.Request.Tty` (`:97`) and passes it into `ExecSpec.Tty` (`StartProcessAsync` `:220-270`); `AppleContainerRuntime.ExecAsync` picks pty vs pipe from that (`AppleContainerRuntime.cs:293-295`). One pump per available stream (`:118-122`); `process.Stderr is null` in tty mode is what collapses to a single stream.
- **Framing**: `DockerResults.WriteChunkAsync(clientOutput, chunk.Stream, chunk.Data, tty, ct)` in `/Users/michael/local/cider/src/Cider.Daemon/Hosting/StdioPump.cs:42` — raw when `tty`, 8-byte multiplexed header otherwise (`/Users/michael/local/cider/src/Cider.Core/DockerApi/Streams/MultiplexedFrame.cs`).
- **Exec start/exec race**: two layers — `AppleContainerRuntime.ExecAsync`'s own retry (`:306-347`) and `ExecManager`'s (`ExecManager.cs:28-31`, `:246-254`) keyed on `ex.IsContainerNotRunning`, i.e. `RuntimeErrorReason`, never on message text.
- **Resize**: `CliProcess.ResizeAsync` (`CliProcess.cs:169-211`) does `TIOCSWINSZ` on **both** masters under the gate and then `SIGWINCH`s the child (`:209`); no-op for pipes and after teardown.
- **Half-close**: `CliProcess.CloseStdinAsync` (`:132-160`) really disposes the pipe stdin, but on a pty it only records the flag and keeps `Stdin` alive (`:112-118`, `:126-131`).

### 3.4 Logs

- Capture path: the pumps above write into `LogStore` (`/Users/michael/local/cider/src/Cider.Core/Logs/LogStore.cs`, `OpenWriter` :108, json-file lines with `log`/`stream`/`time` fields :340-346, size cap `LogMaxBytes`).
- Read path: `ContainerManager.LogsAsync` (`ContainerManager.Logs.cs:17-74`) prefers `_logs.HasCapture(record.Id)` (`:26`); **only if there is no capture** does it fall back to `_runtime.OpenLogsAsync` (`:44`), and then *everything is labelled stdout* (`:71`) because Apple's merged log has no stream separation (`:13-15`).
- `TopAsync` (`:77-117`) is an `ExecAsync` of `ps -ef` whose stdout is parsed by `ParseTop` (`:119-154`).

### 3.5 Stop / kill / delete / stats

- `StopAsync` (`ContainerManager.Lifecycle.cs:128-162`) → `_runtime.StopContainerAsync(..., timeout ?? record.StopTimeout, signal ?? record.StopSignal, ...)`; then `WaitForExitHandlingAsync` (`:648-665`) and `MarkStoppedWithoutHandle` (`:170-182`) which writes `"exit code unknown (daemon restarted)"` when the daemon holds no process.
- `KillAsync` (`:185-220`), `RemoveAsync` (`:241-317` — SIGKILL first when running `:258`, tolerates engine-side `NotFound` `:278`), `ForgetVanishedAsync` (`:331-385`, poller-driven).
- Stats: `ContainerManager.StatsAsync` (`ContainerManager.Stats.cs:15-36`) → `BuildStats` (`:38-107`), which synthesizes `system_cpu_usage` from a monotonic clock (`:57-59`, `:109-110`) because Apple reports none, and always reports the single interface `"eth0"` (`:91-98`).

### 3.6 cp in/out, export, commit

- `PutArchiveAsync` (`ContainerManager.Archive.cs:101`) extracts the tar locally and either copies it in per entry (`CopyTreeIntoContainerAsync` `:458-464` → one `CopyToContainerAsync` per top-level entry) or **stages it on disk** when the container is not running (`StageForReplay` `:436-456`), replayed by `FlushStagedArchivesAsync` (`:184`) or converted into bind mounts by `TryMountStagedArchivesAsync` (`:258`, capped at `MaxStagedMounts=64` `:30`) which **deletes and re-creates the engine container** (`:161`, `:178`).
- `GetArchiveAsync`/`StatPathAsync` (`:75`, `:39`) go through `CopyOutAsync` (`:466-464`); `"/"` is answered synthetically because `container cp <id>:/` is refused (`:44-58`). A `ContainerNotRunning` failure falls back to `CopyOutOfStoppedContainerAsync` (`:515`) which does a **whole-rootfs `container export`** and picks one path out of the tar (`:266-287`).
- `ExportAsync` (`:158-172`) → `ExportContainerAsync`.
- **Commit**: `ImageManager.CommitAsync` (`/Users/michael/local/cider/src/Cider.Core/Services/ImageManager.cs:557-606`) — Apple has no commit primitive, so it exports the rootfs (`:595`), wraps it with `OciImageWriter` (`:1314`) and `image load`s it back (`:1321`). Result is a flattened one-layer image (`:548-555`).

---

## 4. Images: listing, inspect, pull progress, and the content-store reads

- **List**: `image ls --format json` (`Images.cs:16`); Apple prints one row per *reference* so `RuntimeMapper.ToImages` (`RuntimeMapper.cs:205-231`) groups by `id` and unions `References` with `MergeReferences` (`:234-250`). `ToImage` (`:252-279`) sums variant sizes, collects platforms, and takes `Created`/`Labels` from the preferred variant.
- **Variant selection**: `RealVariants` filters attestation manifests (`platform.architecture=="unknown"`, `ImageModels.cs:47-49`; `RuntimeMapper.cs:359-376`); `PickVariant` (`:379-409`) prefers requested platform → host arch (`HostArchitecture` `:412-418`) → first.
- **Inspect**: `image inspect <ref>` returns only the row for that reference, so `WithSiblingReferencesAsync` (`Images.cs:210-241`) merges in the other tags of the same id via `ListImagesAsync`.
- **The content-store read**: `RecoverExposedPortsAsync` (`Images.cs:66-125`) — on 1.2.2 `image inspect` **drops empty-object-valued dict fields `ExposedPorts` and `Volumes`** (`:53-64`). Recovery path: `ResolveAppRootAsync` (`:180-203`, cached behind `_appRootGate`, sourced from `system status --format json` → `appRoot`) → `TryReadLocalConfigAsync` (`:129-140`) reads the variant's manifest blob, follows `config.digest`, reads the config blob → `TryReadLocalBlobAsync` (`:145-173`) reads `{appRoot}/content/blobs/{alg}/{hex}` and parses with `AppleJson.Deserialize` (`:166`). Best-effort; never throws. Shapes: `AppleOciManifest` (`Cli/Models/ImageModels.cs:121-124`), `AppleOciImageDocument` (`:53-75`), `AppleOciConfig` (`:93-114`).
- **Pull progress**: `--progress plain` lines → `ProgressParser.ParsePullLine` (`Cli/ProgressParser.cs:31-69`): `^\[(\d+)/(\d+)\]\s*(rest)$` (`:14`), blob counts from `\((\d+) of (\d+) blobs` (`:17`), `[Ns]` suffix stripped (`:20`), non-matching lines become bare `Status` events (`:42`). Build ids from `exporting manifest list sha256:<64hex>` / `exporting manifest sha256:…` (`:24-28`, `:71-82`).
- **Buffering rule**: nothing reaches the caller until `IsPullUnderWay` (`Images.cs:330-342`) — an event carrying `Current`/`Total`, or a step number > 1. This is what preserves an HTTP 404 for a missing tag (`:267-271`).
- **Load**: no CLI report of what was loaded → before/after `ListImagesAsync` diff (`Images.cs:449-475`).
- **Save/export**: temp `.tar` under `TmpDir`, then streamed (`Images.cs:419-434`, `AppleContainerRuntime.cs:593-605`).
- **Login**: `registry login <host> -u <u> --password-stdin`; credentials then live in the CLI's own store — `PullImageAsync`/`PushImageAsync` just call `LoginAsync` first when a username is present (`Images.cs:253-256`, `:353-356`).

---

## 5. Networks and volumes — `AppleContainerRuntime.Resources.cs`

Covered in the table in §2. Structural points a socket client must reproduce:

- `ParseOneOrMany<T>` (`Resources.cs:148-158`): inspect prints an array *usually*, a bare object *sometimes*.
- `RuntimeMapper.ToNetwork` (`RuntimeMapper.cs:475-492`): `Subnet` prefers `status.ipv4Subnet` over `configuration.subnet`; `Gateway` only exists on `status`; `Mode` defaults `"nat"`.
- `RuntimeMapper.ToVolume` (`:494-511`): `Mountpoint` = `configuration.source` (the host path of `volume.img`), `Driver` defaults `"local"`.
- Timeout policy split is deliberate: create/delete on `ResourceTimeout` 30 s, list/inspect still on the 5-min `CommandTimeout` (`Resources.cs:48-54`).
- Docker `bridge` ⇄ Apple `default` is a **daemon-side** mapping in `NetworkManager.RuntimeNameFor` (`/Users/michael/local/cider/src/Cider.Core/Services/NetworkManager.cs:359-361`, `EnsureDefaultAsync` `:548-582`) — not a runtime concern.

---

## 6. System: info, "services are not running", version detection

- `GetInfoAsync` (`AppleContainerRuntime.cs:71-96`): three separate CLI invocations (`system status --format json`, `--version`, `system property list`).
- `Ready` ⇐ `AppleSystemStatus.IsRunning` (`Cli/Models/SystemModels.cs:26` — `Status == "running"`); `AppRoot` ⇐ `status.appRoot` (`:12`), which is also what the image content-store recovery keys off.
- Version: regex over `container --version` stdout, falling back to `status.apiServerVersion` (`AppleContainerRuntime.cs:76-86`).
- Kernel: regex `vmlinux-(\d+\.\d+\.\d+)` over the free-form `system property list` output (`:66-67`, `:136-146`).
- `EnsureReadyAsync` (`:98-123`): orphan sweep → status check → `container system start --enable-kernel-install` with a 300 s budget, logging `"Apple container services are not running; starting them"` (`:116`). Called once at startup from `/Users/michael/local/cider/src/Cider.Daemon/Hosting/DaemonLifecycle.cs:44` under a 3-minute `EngineStartTimeout` (`:29`), and a failure is only a warning (`:46-49`).
- Consumers: `SystemManager.VersionAsync` (`/Users/michael/local/cider/src/Cider.Core/Services/SystemManager.cs:56-95`), `InfoAsync` (`:97-148`, which also reports `Runtimes["apple-container"].Path = _options.ContainerCliPath` at `:130` — **a CLI path leaked into the Docker API surface**), `DiskUsageAsync` (`:150+`).

---

## 7. Daemon-side consumers, and where they depend on CLI-specific behaviour

`IContainerRuntime` is injected into: `ContainerManager` (`ContainerManager.cs:23,44`), `ExecManager` (`ExecManager.cs:33,41`), `ImageManager` (`ImageManager.cs:18,23`), `NetworkManager` (`NetworkManager.cs:28,36`), `VolumeManager` (`VolumeManager.cs:16,22`), `SystemManager` (`SystemManager.cs:39,46`), `StatePoller` (`StatePoller.cs:17,34`), `StateSynchronizer` (`StateSynchronizer.cs:21,30`), `DnsForwarderService` (`/Users/michael/local/cider/src/Cider.Daemon/Dns/DnsForwarderService.cs:57,74`), `DaemonLifecycle` (`DaemonLifecycle.cs:17`).

CLI-shaped dependencies, by consumer:

| Consumer | Dependency | Cite |
|---|---|---|
| `ContainerManager.StartAsync` | The whole "held process" model: `IContainerProcess` returned by start owns stdio **and** the exit code; `handle.Tty = process.HasTty` decides framing | `ContainerManager.Lifecycle.cs:54-88` |
| `ContainerManager.HandleExitAsync` | Exit code comes only from the held child; `-1` when it cannot be observed | `:551-561` |
| `ContainerManager.MarkStoppedWithoutHandle` / `StatePoller` | `"exit code unknown (daemon restarted)"` — the fallback for a container whose CLI child this daemon does not own | `Lifecycle.cs:180`, `StatePoller.cs:135` |
| `ContainerManager` (attach) | PTY boot-noise is handled *below* the seam (`PtyBootFilterStream`), so nothing here filters — a transport that surfaces raw guest bytes must not reintroduce a banner | `ProcessLauncher.cs:178-181`, `Process/PtyBootFilterStream.cs:23-45` |
| `ContainerManager.AwaitStartupAndRegisterNetworkNamesAsync` | Polls `InspectContainerAsync` every 250 ms up to 10 s because Apple reports `status.networks: []` for ~1–2 s after start | `ContainerManager.Reconcile.cs:258-324`, budgets at `Lifecycle.cs:20-29` |
| `ContainerManager` (network connect/disconnect) | Apple cannot change a running container's networks → delete + re-create from `BuildSpecFromRecord` | `ContainerManager.Networks.cs:116-155`, `Spec.cs:70-86` |
| `ContainerManager.Archive` | `container cp` refuses a stopped container ⇒ stage-and-replay and export-based read-out; `cp <id>:/` refused ⇒ synthetic root stat | `Archive.cs:44-58`, `:245-252`, `:266-287` |
| `ContainerManager.LogsAsync` | Runtime logs have no stream separation → all fallback output is labelled stdout | `Logs.cs:13-15`, `:71` |
| `ExecManager` | `ContainerNotRunning` retry (5 s window, 10 attempts) for the Apple start/exec race; `process.Stderr is null` ⇒ tty | `ExecManager.cs:22-31`, `:246-254`, `:119-122` |
| `ImageManager.PullAsync` | Header `Pulling from …` withheld until the runtime reports progress — this only works because the adapter buffers pull lines; a runtime that reports too early costs the client its 404 | `ImageManager.cs:125-159`, `:166-175` + `Images.cs:267-271` |
| `ImageManager.BuildAsync` | Drops runtime-produced `Successfully built/tagged` lines (`IsBuildTerminalLine`, `ImageManager.cs:1104-1109`) and hides the synthetic tag | `ImageManager.cs:776-797`, `:811-824` |
| `ImageManager` | Multi-reference-per-digest semantics: `rmi <tag>` untags rather than deleting when other refs exist; synthetic build tags filtered from client-visible refs | `ImageManager.cs:257-352`, `:1094-1101` |
| `ImageManager.CommitAsync`/`ImportAsync` | Export→OCI→`image load` because there is no commit primitive | `:548-606`, `:1290-1321` |
| `NetworkManager` | `bridge`↔`default` name folding; `host`/`none`/`container:` modes rejected up front | `NetworkManager.cs:344-361`, `ContainerManager.Spec.cs:36-42` |
| `VolumeManager` | Volume `Mountpoint` reported as `<DataDir>/volumes/<name>/_data`, **not** the runtime's `configuration.source` | `VolumeManager.cs:249` |
| `StatePoller` | Two-consecutive-miss rule against `container ls -a`, plus `IsHeldByUs` to distinguish "runtime lost track" from "removed" | `StatePoller.cs:25-29`, `:127-170`, `:236` |
| `HealthMonitor` | Goes entirely through `ExecManager` with `tty:false`; depends on exec exit codes and drained output | `/Users/michael/local/cider/src/Cider.Core/Health/HealthMonitor.cs:154-172` |
| `RestartSupervisor` | Restarts on the `die` event, which is only raised when `HandleExitAsync` (held process) or `StatePoller` observes the exit | `/Users/michael/local/cider/src/Cider.Core/Restart/RestartSupervisor.cs:119-138`, `:147-174` |
| `LogStore` | Fed only by the held-process pumps; `HasCapture` gating means a transport that cannot hold stdio silently degrades every `docker logs` to the merged fallback | `Logs.cs:26-34`, `Lifecycle.cs:82-88` |
| `DnsForwarderService` | Creates/starts hidden containers directly through the runtime and holds their `IContainerProcess` for their lifetime | `DnsForwarderService.cs:368-394`, `:437`, `_processes` at `:65` |
| `ContainerManager.IsSystemContainer` | Filters Apple's own `buildkit` VM out of every listing by label/runtime-id | `ContainerManager.cs:180-223` |
| `RuntimeErrorTranslator` | `RuntimeErrorKind` → HTTP status, incl. `Timeout` → 500 (not 503) | `/Users/michael/local/cider/src/Cider.Core/Services/RuntimeErrorTranslator.cs:10-21` |

---

## 8. Tests

### 8.1 `tests/Cider.Tests/Fakes/FakeContainerRuntime*.cs`
A full in-memory `IContainerRuntime` split into partials. `FakeContainerRuntime.cs` holds the declaration, the ordered `Calls` log (`:15`) and `ExecFactory` (`:18`).

- `.Containers.cs` (614 ln): container table + `FakeProcess` init/exec processes; test hooks `CreateFailure`(:27), `StartFailure`(:30), `AfterRemove`(:32-37), `CopyToNotRunningFailures`(:39-44, models Apple's "cp says not running just after start"), `Stats`(:47), `SeedContainer`(:61), `VanishContainer`(:85 — models Apple losing a container while its process still runs), `GetSpec`(:103), `ExecProcesses`(:106). Fake `NetworkPrefix="192.168.64."`/`NetworkGateway`(:21-24). Models cancellation-before-create (`:148-149`) and bind-mount-of-a-single-file semantics (`:216-218`).
- `.Images.cs` (487 ln): image table, `PullFailure`/`PullFailureProgress`(:17-24), `LastLoadedTar`(:236), build/tag/save/load/login/df.
- `.Networks.cs` (174), `.Volumes.cs` (93): tables + `ListNetworksFailure`/`CreateNetworkFailure`/`ListVolumesFailure`, `VanishNetwork`/`VanishVolume`.
- `.Builder.cs` (100): `BuilderStatus`, `StartBuilderCalls`, `LastStartBuilderArgs`, `DialBuilderCalls`, `BuilderDials`, `StartBuilderFailure`, `DialBuilderFailure`.
- `.Timing.cs` (74): `DelayNetworkAttachment(runtimeId, count)` (:20) and `FailExecUntilRunning(runtimeId, count)` (:36) — the two Apple race windows.
- `FakeProcess.cs` (424 ln) is the `IContainerProcess` double.

### 8.2 `tests/Cider.Tests/AppleContainer/*` — what is unit-tested about the CLI layer
- `ArgBuilderTests.cs` (298) — spec → exact argv.
- `ContainerParsingTests.cs` (252), `ImageParsingTests.cs` (216), `ResourceParsingTests.cs` (126) — verbatim 1.2.2 JSON fixtures through `RuntimeMapper`; no CLI involved.
- `CliErrorMapperTests.cs` (156) — every observed stderr text → kind.
- `ProgressParserTests.cs` (78) — pull/build lines.
- `AppleContainerRuntimeImageTests.cs` (156) — the pull-buffering rule and "adapter never emits Docker-shaped terminal lines", driven through the internal `ContainerCli` seam (`AppleContainerRuntime.cs:53-61`).
- `AppleContainerRuntimeExposedPortsTests.cs` (162) — the AppRoot/content-store recovery of `ExposedPorts`/`Volumes`.
- `BuilderTests.cs` (253) — `builder status` text parsing, `builder start` flag omission, the dial.
- `CpTimeoutTests.cs` (195) and `ResourceTimeoutTests.cs` (111) — a stand-in process that never exits, driving the real timeout machinery in `ContainerCli.RunAsync` / `CopyIdleGrace`.
- `PtyProcessTests.cs` (475) — 15 tests against `/bin/sh` on a real pty: full-burst delivery, child dropping stdio, CRLF not double-translated, bare `\n` untouched, stderr staying out of the tty stream, EOF only after child exit, no fd inheritance, resize reaching pty+child, no signalling of reaped pids, no polling after close, tty half-close still forwarding stdin, pipe half-close ending stdin.
- `OrphanReaperTests.cs` (182) — exactly-those-and-nothing-else killing.
- `RuntimeHelperTests.cs` (169) — `PtyBootFilterStream` behaviour at several chunk sizes, near-miss prefixes, and later hide-cursor sequences.
- `LibcTests.cs` (49) — errno naming for `OpenPty` failures.
- `AppleContainerRuntimeE2ETests.cs` (334) — `[E2EFact]`, gated on `CIDER_E2E=1`: start-attached stream separation + exit code (`:47`), exec-pty + inspect (`:91`), images/networks/volumes round trip (`:237`).

### 8.3 `tests/Cider.E2E.Tests/*` (one line each)
- `AspireTests.cs` — Aspire 13.5 + DCP driving cider end to end (session network, cert copy between create and start, ports).
- `BasicsTests.cs` — version/info handshake, stdio and exit-code fidelity of a one-shot run.
- `BuildTests.cs` — classic builder under `DOCKER_BUILDKIT=0`; BuildKit refused.
- `CommitTests.cs` — `docker commit`/`import` produce genuinely runnable images.
- `ComposeTests.cs` — `docker compose` up/ps/logs/down with service-name DNS.
- `DaemonRestartTests.cs` — daemon restart with a container still running: reconcile, stop/remove, unknown exit code, log fallback (`RestartableDaemonFixture` at `:8`).
- `EventsTests.cs` — `docker events` live streaming and filters.
- `ExternalRemovalTests.cs` — container deleted via the Apple CLI: poller drops the record, name freed; explicitly notes the adapter's tty cache lives for the process lifetime (`:9-14`).
- `ImageTests.cs` — one row per reference vs. Docker's one image per digest; `rmi <tag>` untags.
- `LifecycleTests.cs` — detached run/inspect/exec/logs/stop/rm (`:15`), piped stdin + EOF (`:86`), timestamped log capture (`:101`).
- `NetworkDnsTests.cs` — user networks, container-name DNS via CoreDNS, `host.docker.internal`, removal semantics.
- `PortTests.cs` — proxy-mode published ports carry real traffic; host-IP-qualified binds; UDP; Apple-mode characterization behind `CIDER_PORT_PUBLISHING=apple`.
- `RestartHealthTests.cs` — restart supervisor + healthcheck probes against real VMs.
- `SyncTests.cs` — `POST /_cider/sync` drops objects removed via the Apple CLI (own fixture with the poller disabled).
- `TestcontainersTests.cs` — Testcontainers for .NET (Docker.DotNet) driving cider out-of-process.
- `TtyTests.cs` — `docker run -it` / `exec -it` under a real pty helper (`:85`, `:116`).
- `VolumeTests.cs` — named volumes, binds, `docker cp` both ways, anonymous volumes.
- `Infrastructure/{Cmd,DaemonFixture,E2EFactAttribute}.cs` — process shelling, daemon-under-test fixture, `CIDER_E2E` gate.

---

## 9. Packaging / DI / config

**Registration** — `/Users/michael/local/cider/src/Cider.Daemon/Hosting/DaemonHost.cs:132-138`:
```csharp
services.AddSingleton<IContainerRuntime>(sp => new AppleContainerRuntime(
    new AppleContainerOptions { CliPath = options.ContainerCliPath, TmpDir = options.TmpDir },
    sp.GetRequiredService<ILogger<AppleContainerRuntime>>()));
```
Singleton, constructed eagerly from `CiderOptions`; **only `CliPath` and `TmpDir` are wired** — every other `AppleContainerOptions` knob uses its default and is not configurable today.

**`AppleContainerOptions`** — `/Users/michael/local/cider/src/Cider.AppleContainer/AppleContainerOptions.cs`: `CliPath="container"`(:7), `CommandTimeout=5 min`(:10), `ResourceTimeout=30 s`(:22), `PullTimeout=30 min`(:25), `CopyTimeout=30 min`(:39), `CopyIdleGrace=10 s`(:59), `TmpDir=Path.GetTempPath()`(:62).

**`CiderOptions`** keys touching the runtime — `/Users/michael/local/cider/src/Cider.Core/Configuration/CiderOptions.cs`: `ContainerCliPath`(:53, env `CIDER_CONTAINER_CLI` :169-173, config key `containerCliPath` :303-305), `DefaultCpus=2`(:56, :313-315), `DefaultMemoryBytes=2 GiB`(:59, :318-320), `TmpDir=<DataDir>/tmp`(:134), `VolumesDir`(:131), `StateDir`(:125), `LogsDir`(:128), `PollIntervalSeconds=3`(:107), `LogMaxBytes=64 MiB`(:110), `PortPublishing`/`UseProxyPortPublishing`(:100-104, env `CIDER_PORT_PUBLISHING` :175-179), `BuildKitEnabled`(:80, env `CIDER_BUILDKIT` :181-185), `BuilderCpus`/`BuilderMemoryBytes`(:86, :92 — feed `StartBuilderAsync`), `DnsEnabled`/`DnsListen`/`DnsForwarderImage`/`DnsUpstreams`/`DnsSearchDomain`(:62-77). `DefaultCpus`/`DefaultMemoryBytes` are applied in `ContainerManager` (`Spec.cs:170-171`, `:470-478`; `Create.cs:186-187`), not in the adapter.

**Lifecycle** — `/Users/michael/local/cider/src/Cider.Daemon/Hosting/DaemonLifecycle.cs`: `EnsureReadyAsync` (:44) → `EnsureDefaultAsync` (:51) → `ReconcileAsync` (:52) → DNS (:54-62) → poller/health/restart (:64-66).

---

## 10. CLI-only behaviours a socket/XPC implementation must reproduce

Each item names where cider currently *depends on the CLI doing it*.

1. **Image reference resolution + variant/platform picking at create time.** cider passes a normalized reference string and lets `container create` resolve it locally or pull. `ArgBuilder.cs:198` (positional image), `ContainerManager.Create.cs:166`. A socket client must resolve ref → local manifest → variant itself (cf. `RuntimeMapper.PickVariant`, `RuntimeMapper.cs:379-409`).
2. **Image config merge — split responsibility.** cider merges entrypoint/cmd/env/workdir/user/labels/exposed-ports/healthcheck itself (`ContainerManager.Create.cs:72-131`, `MergeEnv` `Spec.cs:544-568`) and passes everything explicitly, **but** it can only do that because `InspectImageAsync` gives it a full `ImageConfig` — and the CLI's `image inspect` **lies about `ExposedPorts`/`Volumes`**, which cider patches by reading Apple's blob store directly (`Images.cs:53-125`, `:145-173`). A socket client must supply a complete, honest OCI config or keep the blob-store fallback.
3. **Kernel install / selection.** `container system start --enable-kernel-install` (`AppleContainerRuntime.cs:118`) and the `vmlinux-<ver>` scrape of `system property list` (`:66-67`, `:136-146`) are the only kernel handling cider has; `SystemManager` reports the result as `KernelVersion` (`SystemManager.cs:59`, `:115`).
4. **Mount translation.** `-v src:dst[:ro]`, `--mount type=tmpfs,…`, `--tmpfs` (`ArgBuilder.cs:64-91`) → Apple's `virtiofs` / `volume{name,format}` / `tmpfs` attachment records, which cider reads back through `AppleMountType` (`Cli/Models/ContainerModels.cs:148-163`) and `RuntimeMapper.ToMounts` (`RuntimeMapper.cs:123-172`, note: **anything unknown is treated as a bind** at `:160-167`). Volume-name → `volume.img` resolution is entirely the CLI's.
5. **DNS flags.** `--dns` / `--dns-search` / `--dns-option` (`ArgBuilder.cs:105-121`) become `configuration.dns` (`Cli/Models/ContainerModels.cs:242-252`). Sourced from `ContainerManager.ResolveDnsServersAsync`/`ResolveDnsSearch` (`Spec.cs:420-468`).
6. **Port publishing string format.** `-p [ip:]host:container[/proto]` (`ArgBuilder.cs:93-97`, `FormatPort` `:414-422`) → `configuration.publishedPorts` (`ContainerModels.cs:213-224`, read back by `RuntimeMapper.ToPorts` `:101-121`). Note this path is **off by default**: proxy mode sends no `-p` at all (`Create.cs:180-181`, `Spec.cs:165`, `CiderOptions.cs:100-104`), so only `CIDER_PORT_PUBLISHING=apple` exercises it.
7. **PTY handling.** The CLI is the terminal client: `-t`/`-i` on create/exec, plus the daemon's two-pty `/bin/sh -c 'exec …'` wrapper, `O_CLOEXEC` fds, parent-held slave fds, drain-before-release, and the stderr-as-pipe trick that suppresses Apple's boot spinner (`ProcessLauncher.cs:86-198`, `CliProcess.cs:126-211`, `:314-396`, `PtyStream.cs`, `PtyBootFilterStream.cs`). A socket transport must provide an equivalent duplex tty channel + `SIGWINCH`/window-size propagation, or `TtyTests`, `ExecManager` tty framing (`StdioPump.cs:42`) and `PtyProcessTests` all lose their meaning.
8. **Boot-banner filtering.** `PtyBootFilterStream` only activates when a stream's first bytes are `ESC[?25l` (`PtyBootFilterStream.cs:23-45`, `:187-296`). It is currently dormant-by-design (stderr is a pipe) but is the guard for anything that re-terminalizes the stream.
9. **Exit-code semantics.** "The exit code of the CLI child is the guest process's own exit code" (`CliProcess.cs:8-12`) is load-bearing for `HandleExitAsync` (`Lifecycle.cs:556`), `docker wait` (`Lifecycle.cs:388-430`), `RestartSupervisor.ShouldRestart` (`RestartSupervisor.cs:113`) and `HealthMonitor` (`HealthMonitor.cs:172`). A socket API must deliver a real exit code, not "process gone".
10. **Signal name normalization.** Docker `SIGTERM`/`15` → Apple `TERM` (`ArgBuilder.NormalizeSignal` `:449-469`, numeric table `:12-23`), plus the macOS signal-number table for direct child signalling (`CliProcess.SignalNumber` `:399-416`).
11. **Resource-unit granularity.** CPUs rounded to `max(1, round(x))` (`ArgBuilder.cs:123-128`); memory/shm rounded **up** to whole MiB with an `M` suffix (`FormatMebibytes` `:425-434`). `TmpfsSpec.SizeBytes` silently dropped (`:86-91`).
12. **Privileged emulation.** `--cap-add ALL --masked-path NONE --read-only-path NONE` (`ArgBuilder.cs:148-156`); exec-level privileged is not expressible at all (`AppleContainerRuntime.cs:286-289`).
13. **Hostname.** Not settable — derived by the CLI from the container id (`AppleContainerRuntime.cs:154-159`). cider still tracks the Docker hostname in the record (`Create.cs:101`, `:196`).
14. **Registry auth storage.** `registry login … --password-stdin` (`Images.cs:551-554`); pull/push carry no credentials of their own (`:253-256`, `:353-356`).
15. **Pull/push progress as text.** `--progress plain` line grammar (`ProgressParser.cs:13-28`) and the "is the pull under way yet" heuristic that preserves HTTP 404 (`Images.cs:267-271`, `:330-342`). A typed progress stream would need `ImageManager.PullAsync`'s header rule re-derived (`ImageManager.cs:125-159`).
16. **Build.** The entire classic-builder path is `container build --progress plain` (`ArgBuilder.cs:255-325`), with the resulting image id scraped from `exporting manifest list sha256:…` (`ProgressParser.cs:71-82`) and a synthetic tag minted when the client asked for none (`Images.cs:492-495`, `Ids/SyntheticBuildTag.cs`).
17. **Builder VM lifecycle.** `builder status` is a **plain-text table** (`Builder.cs:63-132`) and `builder start` may pull the shim image (`:45-48`); the buildkitd channel is `exec -i buildkit buildctl dial-stdio` (`:55-58`), which the WIP `BuilderLink` wraps in an HTTP/2 gRPC channel (`/Users/michael/local/cider/src/Cider.Daemon/BuildKit/BuilderLink.cs:14-37`). The README states Apple's own builder BuildKit is reachable *only* over the private XPC protocol (`README.md:297`).
18. **File-based bulk transfers.** `export -o`, `image save -o`, `image load -i` all go through temp files under `TmpDir` (`AppleContainerRuntime.cs:593-605`, `Images.cs:419-434`, `:441-451`) — there is no streaming form. `commit`, `import` and the stopped-container `cp` fallback are built on top of that (`ImageManager.cs:589-602`, `Archive.cs:288-333`).
19. **`container cp` semantics + its hang.** cp is refused on a stopped container (worked around at `Archive.cs:245-252`), refuses `<id>:/` (`Archive.cs:44-58`), reports no progress at all (`AppleContainerOptions.cs:27-39`), and **hangs forever on a nonexistent guest path** — the `CopyIdleGrace` watchdog exists only for that (`AppleContainerOptions.cs:41-58`, `AppleContainerRuntime.cs:466-508`, `:541-586`).
20. **Merged, never-terminating logs.** `container logs -f` has no stream separation and no natural end; disposal is the only exit (`AppleContainerRuntime.cs:433`, `ProcessOutputStream.cs:5-9`, `:75-92`), and the consumer labels everything stdout (`Logs.cs:71`).
21. **Error classification from free-form stderr.** The whole of `CliErrorMapper.cs` — including the "swift-argument-parser usage banner means 400" rule (`:72-87`, `:109-112`) and the "bare *failed to delete one or more* means 404" rule (`:124-132`). A typed XPC error domain replaces this, but `RuntimeErrorKind`/`RuntimeErrorReason` must still be produced, because `ExecManager` (`:246-254`) and `ContainerManager.Archive` (`:245-252`) branch on `IsContainerNotRunning`, and `RuntimeErrorTranslator` maps kinds to HTTP status (`RuntimeErrorTranslator.cs:10-21`).
22. **Startup races the CLI exposes.** `container exec` says "is not running" for seconds after `start -a` already holds init (`AppleContainerRuntime.cs:20-30`), and `inspect` reports `status.networks: []` for ~1–2 s (`Reconcile.cs:260-266`). Both retry loops are keyed to CLI-observed timing.
23. **Orphan reaping.** `CIDER_HELD` + `ppid==1` + argv-shape sweep (`OrphanReaper.cs:29-150`) exists purely because the transport is a child process. A socket transport removes the mechanism *and* the failure mode — but the sweep must still run for a transition period, or pre-swap orphans keep wedging the runtime machine-wide (`:6-13`).
24. **JSON shape coupling.** Everything in `/Users/michael/local/cider/src/Cider.AppleContainer/Cli/Models/` plus the source-generated contract list in `Cli/AppleJson.cs:32-81` is the CLI's `--format json` rendering, including quirks like the bare-hex image id (`RuntimeMapper.ToImageId` `:436-444`), CIDR-suffixed addresses (`StripCidr` `:187-196`), and the discriminated-union `AppleUser`/`AppleMountType` shapes (`ContainerModels.cs:108-163`).
25. **`buildkit` filtering.** Apple's builder VM shows up in `container ls -a` and is excluded by label/runtime-id at the daemon level (`ContainerManager.cs:180-223`) — a socket listing must expose the same labels (`com.apple.container.plugin`, `com.apple.container.resource.role`) or the builder becomes a visible Docker container.