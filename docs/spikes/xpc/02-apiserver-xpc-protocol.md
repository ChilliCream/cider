<!-- Generated 2026-08-25 by a research agent during planning of the XPC runtime transport; source of truth is the apple/container 1.3.0 tree and the cider tree at that date. -->

I have everything I need. Here is the complete protocol map.

---

# apple/container 1.3.0 — XPC protocol map for a .NET client

Source: `/Users/michael/local/apple-container` @ tag `1.3.0` (commit `d6de569`, 2026-08-20). Installed runtime confirmed 1.3.0 (`container --version` → `1.3.0 (build: release, commit: d6de569)`).

---

## 1. Transport

### 1.1 Mach services

| Mach service | Server binary | Declared at |
|---|---|---|
| `com.apple.container.apiserver` | `/usr/local/bin/container-apiserver` | `/Users/michael/local/apple-container/Sources/APIServer/APIServer+Start.swift:92`; client side `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ContainerClient.swift:30` |
| `com.apple.container.core.container-core-images` | plugin `container-core-images` | `/Users/michael/local/apple-container/Sources/Plugins/CoreImages/ImagesHelper.swift:38` and `:77`; client `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ClientImage.swift:97`, `/Users/michael/local/apple-container/Sources/Services/ContainerImagesService/Client/RemoteContentStoreClient.swift:25` |
| `com.apple.container.core.machine-apiserver` | plugin `machine-apiserver` | `/Users/michael/local/apple-container/Sources/Services/MachineAPIService/Client/MachineClient.swift:28` |
| `com.apple.container.network.container-network-vmnet.<networkId>` | plugin instance | naming rule at `/Users/michael/local/apple-container/Sources/ContainerPlugin/Plugin.swift:62-71`; used at `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Networks/NetworksService.swift:340` |
| `com.apple.container.runtime.<runtime>.<containerId>` | per-container runtime instance | `/Users/michael/local/apple-container/Sources/Services/Runtime/RuntimeClient/RuntimeClient.swift:13,30` |

**Yes — images are a separate mach service / separate launchd plugin process.** `ClientImage` and `RemoteContentStoreClient` connect to `com.apple.container.core.container-core-images`, never to the apiserver. Mach service names for plugins are `com.apple.container.<serviceType>.<pluginName>[.<instanceId>]` where `serviceType` comes from `config.toml` (`/usr/local/libexec/container/plugins/container-core-images/config.toml` declares `type = "core"`).

The apiserver plist is written by the CLI at `/Users/michael/local/apple-container/Sources/ContainerCommands/System/SystemStart.swift:115-127` with `label: "com.apple.container.apiserver"`, `machServices: ["com.apple.container.apiserver"]`, `LimitLoadToSessionType: [Aqua, Background, System]`, `RunAtLoad: true`. A .NET client should just `xpc_connection_create_mach_service("com.apple.container.apiserver", …)`; launchd on-demand-launches it.

### 1.2 Request construction

`/Users/michael/local/apple-container/Sources/ContainerXPC/XPCMessage.swift:22-51`:

- Every request is a **flat `xpc_dictionary`**.
- Route key: `"com.apple.container.xpc.route"` (`XPCMessage.routeKey`, line 24), value is an **xpc string** = the raw value of `XPCRoute` (e.g. `"containerCreate"`).
- Error key: `"com.apple.container.xpc.error"` (line 26).
- All other keys are the **raw string** of the `XPCKeys` enum case (e.g. `"id"`, `"containerConfig"`, `"exitCode"`) — see `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/XPC+.swift:22-146`. There is no prefix on payload keys.

Value types actually used (all from `XPCMessage.swift`):

| Type | xpc primitive | Setter/getter |
|---|---|---|
| string | `xpc_string` | `:165-179` |
| data | `xpc_data` (**raw JSON bytes, not base64**) | `:120-163` |
| bool | `xpc_bool` | `:181-191` |
| uint64 | `xpc_uint64` | `:193-203` |
| int64 | `xpc_int64` | `:205-215` |
| date | `xpc_date` (**Int64 nanoseconds since Unix epoch**) | `:217-229` |
| fd | `xpc_fd` (single value) | `:231-248` |
| fd array | `xpc_array` of `xpc_fd`, reader hard-codes indices 0 and 1 | `:250-283` |
| endpoint | `xpc_endpoint` (progress callbacks) | `:291-301` |

`XPCMessage.int(key:)` is a convenience over `int64` (`XPC+.swift:242-248`).

Note the asymmetry: XPC `date` values are ns-since-**Unix** epoch (`XPCMessage.swift:219-227`), but `Date` inside **JSON payloads** is seconds-since-**2001-01-01** (see §2.0).

### 1.3 Replies and errors

- Reply is built with `xpc_dictionary_create_reply` (`XPCMessage.swift:58-62`) and sent via `xpc_connection_send_message` (`XPCServer.swift:209`). The reply therefore echoes the route key.
- Success with no data = reply dictionary containing only the route key (many handlers do `return message.reply()`).
- Errors: `com.apple.container.xpc.error` holds **UTF-8 JSON bytes** of `{"code": String, "message": String}` — struct `ContainerXPCError` at `/Users/michael/local/apple-container/Sources/ContainerXPC/XPCMessage.swift:114-117`, written at `:99-111`, read at `:78-90`. Note errors are delivered **in a normal reply dictionary**, not as an XPC error object — a .NET client must check for the error key on every reply.
- `message` gets ` (cause: "…")` appended when the Swift error had a cause (`:100-103`).
- Fallback if encoding fails: `{"code":"internalError","message":"the daemon failed to encode the original error"}` (`:94-97`).

**`ContainerizationError.Code` strings** (`code` field). Confirmed exhaustively from the shipped binary symbols (`nm` on `/usr/local/bin/container-apiserver`, `ContainerizationError.Code` static getters):

```
cancelled, unknown, invalidArgument, timeout, notFound,
exists, unsupported, internalError, invalidState, interrupted, empty
```

Codes actually thrown by apiserver route handlers (`git grep -oh "ContainerizationError(\.[a-zA-Z]*"`): `empty, exists, internalError, interrupted, invalidArgument, invalidState, notFound, timeout, unknown, unsupported`.

Transport-level XPC failures (service not running, connection invalid/interrupted) are surfaced client-side as `ContainerizationError(.interrupted | .invalidState, "XPC connection error: …")` — `/Users/michael/local/apple-container/Sources/ContainerXPC/XPCClient.swift:140-159`. The CLI special-cases the string `"XPC connection error"` to advise `container system start` (`/Users/michael/local/apple-container/Sources/ContainerCommands/Application.swift:136-142`).

### 1.4 Timeouts

`/Users/michael/local/apple-container/Sources/ContainerXPC/XPCClient.swift:27` — `xpcRegistrationTimeout = .seconds(60)`; this is the default for `ContainerClient.xpcSend` (`ContainerClient.swift:42`) and `NetworkClient.xpcSend` (`NetworkClient.swift:62`). Timeouts are implemented purely client-side by racing a `Task.sleep` against the reply (`XPCClient.swift:101-137`); on expiry it throws `internalError: "XPC timeout for request to <service>/<route>"`.

Per-call overrides:

| Call | Timeout | Cite |
|---|---|---|
| `containerList` | 10 s | `ContainerClient.swift:92-95` |
| `containerCopyIn` / `containerCopyOut` | 300 s | `ContainerClient.swift:326`, `:345` |
| `networkList` | **1 s** | `NetworkClient.swift:99` |
| `ping` from `system status` | 10 s | `SystemStatus.swift:84` |
| everything else on `ContainerClient` | 60 s | default |
| `containerBootstrap`, `containerKill`, `containerStop`, `containerDelete`, `containerLogs`, `containerDial`, `containerStats`, `containerExport`, `containerWait`, `containerStartProcess`, `containerResize` | **no timeout** — they call `xpcClient.send(request)` directly with `responseTimeout: nil` | e.g. `ContainerClient.swift:151, 170, 188, 204, 273, 299, 361, 384`; `ClientProcess.swift:75, 85, 96, 105` |

`containerWait` intentionally has no timeout (it blocks until the process exits).

### 1.5 `XPCClientSession`

`/Users/michael/local/apple-container/Sources/ContainerXPC/XPCClientSession.swift:25-51`. It adds **no streaming and no new framing**. It is just `XPCClient` + a disconnect-notification list, installed at construction time so a server crash before the first `send()` is not missed (`:29-36`). `send()` delegates verbatim (`:45-47`). The server-side counterpart `XPCServerSession` (`/Users/michael/local/apple-container/Sources/ContainerXPC/XPCServerSession.swift:25-38`) is one actor per accepted connection, giving handlers `onDisconnect` for resource cleanup; `XPCServer.route(_:)` (`XPCServer.swift:29-33`) wraps session-unaware handlers, and **every apiserver route uses `XPCServer.route`**, i.e. no apiserver route currently uses the session. All apiserver interaction is strict request/reply.

Long-lived streaming happens only via **out-of-band fds** (stdio, logs, `dial`) and the **progress endpoint** (§5).

### 1.6 Caller checks by the server

`/Users/michael/local/apple-container/Sources/ContainerXPC/XPCServer.swift:163-204`:

1. Message must be `XPC_TYPE_DICTIONARY`, else `invalidArgument: "invalid request"` (`:165-173`).
2. **EUID check only**: `xpc_dictionary_get_audit_token` → `audit_token_to_euid(token)` must equal `geteuid()` of the server, else `invalidState: "unauthorized request"` (`:175-193`). The `xpc_dictionary_get_audit_token` prototype is vendored at `/Users/michael/local/apple-container/Sources/CAuditToken/include/AuditToken.h`.
3. Route string must be present, else `invalidArgument: "invalid request"` (`:195-203`).
4. **Unknown routes are silently dropped — no reply at all** (`:205` `if let handler = routes[route]` with no `else`). A .NET client sending a bad route will hang until its own timeout fires. Important.

**There is no entitlement check, no code-signing check, and no pid check.** Any process running as the same uid can drive the apiserver. `XPCClient.remotePid()` exists (`XPCClient.swift:68-70`) but is client-side only.

Unexpected non-`ContainerizationError` throws get mapped to `.invalidArgument` if the type name or message contains `"VolumeError"`/`"Volume"`, else `.unknown` with `String(describing:)` (`XPCServer.swift:232-241`).

---

## 2. Every route on `com.apple.container.apiserver`

Route enum: `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/XPC+.swift:148-191`. Key enum: `:22-146`.

### 2.0 Codable rules a .NET model MUST match exactly

Verified against real on-disk files written by the shipped 1.3.0 daemon (`~/Library/Application Support/com.apple.container/containers/*/config.json`, `kernel.json`, `runtime-configuration.json`, `networks/default/entity.json`).

1. **No custom JSON strategies anywhere on the wire.** `git grep dateEncodingStrategy` finds hits only in `Sources/ContainerCommands/OutputRendering.swift` (CLI *display*), `MachineInspect.swift`, `MachineList.swift`, and tests. Every `JSONEncoder()`/`JSONDecoder()` used for XPC payloads is stock.

2. **`Date` = `Double` seconds since 2001-01-01T00:00:00Z (Apple reference date)**, i.e. `.deferredToDate`. Real sample: `"creationDate": 809330969.025174`. **`.NET`: `unixSeconds = value + 978307200`.** Do not confuse with the CLI's pretty JSON, which uses ISO-8601 (`OutputRendering.swift:26-27`) — that is display only.

3. **Enums with associated values use Swift's synthesized single-key-object form.** Confirmed live:
   - `ProcessConfiguration.User` → `{"id":{"uid":0,"gid":0}}` or `{"raw":{"userString":"65532:65532"}}`
   - `Filesystem.FSType` → `{"virtiofs":{}}`, `{"tmpfs":{}}`, `{"block":{"format":"ext4","cache":{"on":{}},"sync":{"fsync":{}}}}`, `{"volume":{"name":…,"format":…,"cache":{…},"sync":{…}}}`
   - Payload-free cases still emit an **empty object**, not a string: `{"on":{}}`, `{"fsync":{}}`.
4. **Enums with `String` raw values are plain strings**: `RuntimeStatus` (`"stopped"|"running"|"stopping"|"unknown"`), `NetworkMode` (`"nat"|"hostOnly"`), `PublishProtocol` (`"tcp"|"udp"`).
5. **`Data` is never base64 on the wire** — JSON payloads live in xpc `data` values as raw UTF-8 bytes. There are no `Data`-typed Codable fields in these models.
6. **`URL` encodes as its `absoluteString`**, i.e. a percent-encoded `file://` URL: `"path": "file:///Users/michael/Library/Application%20Support/com.apple.container/kernels/vmlinux-6.18.15-186"` (in `Kernel`). But `XPCKeys.archive`, `.sourcePath`, `.destinationPath`, `.contentPath`, `.filePath` are **plain path strings** (server does `URL(fileURLWithPath:)`, `ContainersHarness.swift:402`).
7. **Network value types encode as bare strings** (confirmed from live `container ls --format json`): `CIDRv4` `"192.168.64.2/24"`, `CIDRv6` `"fd3e:…:3baa/64"`, `IPv4Address` `"192.168.64.1"`, `MACAddress` `"f6:b2:c1:2d:3b:aa"`. `IPAddress` (used by `PublishPort.hostAddress`) has hand-written `encode(to:)`/`init(from:)` in `ContainerizationExtras` (confirmed by symbol table — no `__derived` encode witness) and is almost certainly the same bare-string form (`"0.0.0.0"`); **round-trip test this one**, it's the only unverified encoding.
8. **`FilePath` (`PublishSocket`) encodes as a plain string** via a hand-written encoder (`/Users/michael/local/apple-container/Sources/ContainerResource/Container/PublishSocket.swift:41-46`), decoder accepts either a plain absolute path or a `file:` URL (`:68-113`). `FilePermissions` is `RawRepresentable<CInt>` → a JSON integer.
9. **`ResourceLabels` encodes as a bare `[String:String]`** (`/Users/michael/local/apple-container/Sources/ContainerResource/Common/ResourceLabels.swift:52-61`), and **validates on decode** — key ≤128 chars, matching a Docker/OCI label-key regex, `key=value` ≤4096 chars.
10. **Optionals are omitted** when nil (synthesized `encodeIfPresent`); e.g. `ContainerStatus.startedDate`, `ContainerCreateOptions.rootFsOverride`, `DNSConfiguration.domain`.
11. **Which fields are required on decode** matters. Types with a **custom `init(from:)`** tolerate missing keys: `ContainerConfiguration` (only `id`, `image`, `initProcess` required — `ContainerConfiguration.swift:60-94`), `ContainerConfiguration.Resources` (all optional — `:125-131`), `NetworkConfiguration` (`:59-90`), `VolumeConfiguration` (`:43-56`), `Attachment` (`:47-66`), `PublishPort` (`:49-59`). Types with **synthesized** Codable require every non-optional field: `ProcessConfiguration` (all 8), `Filesystem` (all 4), `ContainerListFilters` (`ids` and `labels` must be present, `{}` will **fail**), `ContainerStopOptions` (`timeoutInSeconds`), `ContainerCreateOptions` (`autoRemove`), `AttachmentConfiguration`/`AttachmentOptions` (`network`, `hostname`), `DNSConfiguration` (`nameservers`, `searchDomains`, `options`), `ImageDescription` (`reference`, `descriptor`), `ContainerSnapshot` (`configuration`, `status`, `networks`).

### 2.1 Container routes → `ContainersHarness` → `ContainersService`

Registered at `/Users/michael/local/apple-container/Sources/APIServer/APIServer+Start.swift:293-309`. Handlers in `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Containers/ContainersHarness.swift`; helpers `stdio()`, `stopOptions()`, `signal()`, `processConfig()`, `setFileHandle()` at `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Containers/ContainersService.swift:1160-1204`.

| Route | Request keys (type) | Reply keys (type) | Handler → service |
|---|---|---|---|
| `containerList` | `listFilters` (data, `ContainerListFilters`, optional → `.all`) | `containers` (data, `[ContainerSnapshot]`) | `ContainersHarness.swift:35-46` → `ContainersService.swift:164` |
| `containerCreate` | `containerConfig` (data, `ContainerConfiguration`, **required**), `kernel` (data, `Kernel`, **required**), `containerOptions` (data, `ContainerCreateOptions`, optional → `.default`), `initImage` (string, opt), `runtimeData` (data, opt) | — | `:178-209` → `ContainersService.swift:267` |
| `containerBootstrap` | `id` (string, req, validated by `ManagedContainer.nameValid`), `stdin`/`stdout`/`stderr` (fd, each optional), `dynamicEnv` (data, `[String:String]`, opt) | — | `:49-67` → `ContainersService.swift:391` |
| `containerCreateProcess` | `id` (string), `processIdentifier` (string), `processConfig` (data, `ProcessConfiguration`, req), `stdin`/`stdout`/`stderr` (fd, opt) | — | `:212-238` → `ContainersService.swift:468` |
| `containerStartProcess` | `id` (string), `processIdentifier` (string) | — | `:241-263` → `ContainersService.swift:505` |
| `containerWait` | `id` (string), `processIdentifier` (string) | `exitCode` (**int64**), `exitedAt` (**date**) | `:102-123` → `ContainersService.swift:675` |
| `containerStop` | `id` (string), `stopOptions` (data, `ContainerStopOptions`, **required** — `empty StopOptions` if absent) | — | `:70-81` → `ContainersService.swift:605` |
| `containerKill` | `id` (string), `processIdentifier` (string), `signal` (**string**, req) | — | `:154-175` → `ContainersService.swift:570` |
| `containerDelete` | `id` (string, name-validated), `forceDelete` (bool) | — | `:266-277` → `ContainersService.swift:814` |
| `containerResize` | `id` (string), `processIdentifier` (string), `width` (uint64), `height` (uint64) | — | `:126-151` → `ContainersService.swift:701` |
| `containerLogs` | `id` (string, name-validated) | `logs` (**xpc array of 2 fds**: `[0]`=`stdio.log`, `[1]`=`vminitd.log`) | `:296-311` → `ContainersService.swift:727-763` |
| `containerDial` | `id` (string), `port` (uint64, vsock port) | `fd` (fd) | `:84-99` → `ContainersService.swift:648` |
| `containerStats` | `id` (string) | `statistics` (data, `ContainerStats`) | `:368-381` → `ContainersService.swift:790` |
| `containerDiskUsage` | `id` (string, name-validated) | `containerSize` (uint64) | `:280-293` → `ContainersService.swift:877` |
| `containerCopyIn` | `id`, `sourcePath`, `destinationPath` (strings, req), `fileMode` (uint64), `createParents` (bool) | — | `:314-338` → `ContainersService.swift:766` |
| `containerCopyOut` | `id`, `sourcePath`, `destinationPath` (strings, req), `createParents` (bool) | — | `:341-365` → `ContainersService.swift:778` |
| `containerExport` | `id` (string, name-validated), `archive` (string, **plain path**) | — | `:384-406` → `ContainersService.swift:900` |
| `containerState` | **NOT REGISTERED** — enum case exists (`XPC+.swift:160`) but `git grep containerState` yields exactly one hit. Use `containerList` with `{"ids":["x"],"labels":{}}`. | | |
| `containerEvent` | **NOT REGISTERED and never sent.** Dead enum case (`XPC+.swift:162`). No event/push channel exists. | | |

**⚠️ `containerKill` signal-type bug.** `ContainerClient.kill(id:signal:)` sets `signal` as a **string** (`ContainerClient.swift:168`), but `ClientProcessImpl.kill(_ signal: Int32)` sets it as an **int64** (`ClientProcess.swift:83`). The server reads it with `xpc_dictionary_get_string` (`ContainersService.swift:1161-1166`), which returns NULL for an int64 → `invalidArgument: "missing signal in xpc message"`. **Always send `signal` as a string.** The runtime parses it with `Signal(String)` (`/Users/michael/local/apple-container/Sources/Services/RuntimeLinux/Server/RuntimeService.swift:573`), which accepts both bare (`"KILL"`, `"TERM"` — the CLI default is `"KILL"`, `ContainerKill.swift:37`) and `SIG`-prefixed (`"SIGTERM"` — the runtime's own stop default, `RuntimeService.swift:528`).

### 2.2 Payload structs

**`ContainerConfiguration`** — `/Users/michael/local/apple-container/Sources/ContainerResource/Container/ContainerConfiguration.swift:20-158`, custom `init(from:)` at `:75-109`.

| Field | Type | Optional / default on decode |
|---|---|---|
| `id` | String | **required** |
| `image` | `ImageDescription` | **required** |
| `initProcess` | `ProcessConfiguration` | **required** |
| `mounts` | `[Filesystem]` | `[]` |
| `publishedPorts` | `[PublishPort]` | `[]` |
| `publishedSockets` | `[PublishSocket]` | `[]` |
| `labels` | `[String:String]` | `[:]` |
| `sysctls` | `[String:String]` | `[:]` |
| `networks` | `[AttachmentConfiguration]` | `[]` |
| `dns` | `DNSConfiguration?` | nil |
| `rosetta` | Bool | false |
| `platform` | `ContainerizationOCI.Platform` | `.current` |
| `resources` | `Resources` | `.init()` |
| `runtimeHandler` | String | `"container-runtime-linux"` |
| `virtualization` | Bool | false |
| `ssh` | Bool | false |
| `readOnly` | Bool | false |
| `useInit` | Bool | false |
| `capAdd` / `capDrop` | `[String]` | `[]` |
| `shmSize` | `UInt64?` | nil |
| `stopSignal` | `String?` | nil |
| `maskedPaths` / `readonlyPaths` | `[String]?` | nil (**new in 1.2.x**) |
| `creationDate` | Date | epoch 1970 if absent |

Nested: `DNSConfiguration` (`:111-130`) = `nameservers: [String]` (req), `domain: String?`, `searchDomains: [String]` (req), `options: [String]` (req); default nameserver list is `["1.1.1.1"]` (`:112`). `Resources` (`:132-147`) = `cpus: Int` (dflt 4), `memoryInBytes: UInt64` (dflt 1024 MiB), `storage: UInt64?`, `cpuOverhead: Int` (dflt 1). Server rejects `memoryInBytes < 200 MiB` (`ContainersService.swift:~400`).

**`ProcessConfiguration`** — `/Users/michael/local/apple-container/Sources/ContainerResource/Container/ProcessConfiguration.swift:17-72`, **synthesized Codable, all fields required**: `executable: String`, `arguments: [String]`, `environment: [String]`, `workingDirectory: String`, `terminal: Bool`, `user: User`, `supplementalGroups: [UInt32]`, `rlimits: [Rlimit]`. `Rlimit` = `{limit: String, soft: UInt64, hard: UInt64}` (`:28-38`). `User` enum (`:40-52`) → `{"id":{"uid":u,"gid":g}}` or `{"raw":{"userString":"…"}}`.

**`ContainerSnapshot`** — `/Users/michael/local/apple-container/Sources/ContainerResource/Container/ContainerSnapshot.swift:20-46`: `configuration: ContainerConfiguration`, `status: RuntimeStatus`, `networks: [Attachment]`, `startedDate: Date?`. `id`/`platform` are computed, **not encoded**.

**`ContainerCreateOptions`** — `.../ContainerCreateOptions.swift:17-28`: `autoRemove: Bool` (req), `rootFsOverride: Filesystem?`.

**`ContainerListFilters`** — `.../ContainerListFilters.swift:19-39`: `ids: [String]` (req), `status: RuntimeStatus?`, `labels: [String:String]` (req). `labels` values are **regexes** compiled server-side (`ContainersService.swift:181-190`); `exclude(x)` → `"^(?!x$)"` (`:20-22`). `withoutMachines()` adds `com.apple.container.plugin` → `^(?!machine$)` (`:42-45`).

**`ContainerStopOptions`** — `.../ContainerStopOptions.swift:19-32`: `timeoutInSeconds: Int32` (req), `signal: String?`. Default `{timeoutInSeconds: 5, signal: null}`. Server falls back to `configuration.stopSignal`, then `"SIGTERM"` (`ContainersService.swift:633-636`, `RuntimeService.swift:528`).

**`ContainerStats`** — `.../ContainerStats.swift:19-51` (`XPCKeys.statistics`): `id: String` (req) plus all-optional `memoryUsageBytes`, `memoryLimitBytes`, `cpuUsageUsec`, `networkRxBytes`, `networkTxBytes`, `blockReadBytes`, `blockWriteBytes`, `numProcesses` (`UInt64?`).

**`Filesystem`** — `.../Filesystem.swift:28-157`, synthesized: `type: FSType`, `source: String`, `destination: String`, `options: [String]` (a plain string array; `"ro"` marks read-only, `:22-26`). `FSType` cases at `:41-51`.

**`PublishPort`** — `.../PublishPort.swift:37-81`: `hostAddress: IPAddress`, `hostPort: UInt16`, `containerPort: UInt16`, `proto: "tcp"|"udp"`, `count: UInt16` (defaults to 1 on decode, `:56`). Validated: `count > 0 && UInt16.max - port >= count - 1` (`:76-80`).

**`PublishSocket`** — `.../PublishSocket.swift:21-129`: `containerPath` (string, must be absolute), `hostPath` (string, must be absolute), `permissions` (int, opt).

**`AttachmentConfiguration`** / **`AttachmentOptions`** — `.../AttachmentConfiguration.swift:19-42`: `{network: String, options: {hostname: String, macAddress: String?, mtu: UInt32?}}`.

**`Attachment`** (in `ContainerSnapshot.networks`) — `.../Attachment.swift:19-95`: `network`, `hostname`, `ipv4Address` (CIDR string), `ipv4Gateway` (IP string), `ipv6Address` (CIDR string?), `macAddress?`, `mtu: UInt32?`, `variant: String?`. Decoder also accepts legacy keys `address`/`gateway` (`:66-75`).

**`NetworkConfiguration`** — `.../NetworkConfiguration.swift:21-125`: encodes `name`, `creationDate`, `mode`, `ipv4Subnet?`, `ipv6Subnet?`, `labels`, `plugin`, `options` (`:107-118`). Decoder accepts `id` as alias for `name` and `subnet` as alias for `ipv4Subnet`, and a legacy `pluginInfo: {plugin, variant?}` object (`:74-105`). Name must match `^[a-z0-9](?:[a-z0-9._-]{0,61}[a-z0-9])?$` (`NetworkResource.swift:36-39`).

**`NetworkResource`** — `.../NetworkResource.swift:20-68`: encodes `{id, configuration, status}`; decoder **ignores `id`** (`:63-67`). **`NetworkStatus`** — `.../NetworkStatus.swift:19-35`: `ipv4Subnet` (CIDR, req), `ipv4Gateway` (IP, req), `ipv6Subnet` (CIDR?).

**`VolumeConfiguration`** — `.../VolumeConfiguration.swift:19-56` + Codable `:57-84`: `name`, `driver`, `format`, `source` (all req), `creationDate` (accepts `createdAt` alias), `labels`, `options`, `sizeInBytes: UInt64?`. Name regex `^[A-Za-z0-9][A-Za-z0-9_.-]*$`, ≤255 (`:120-124`). Anonymous marker label `com.apple.container.resource.anonymous` (`:87`). **`VolumeResource`** — `.../VolumeResource.swift:19-73`: `{id, configuration}`, decoder ignores `id`.

**`Kernel`** (from `Containerization`, `XPCKeys.kernel`) — confirmed live from `~/.../containers/*/kernel.json`:
```json
{"path":"file:///…/kernels/vmlinux-6.18.15-186",
 "platform":{"os":"linux","architecture":"arm64"},
 "commandLine":{"kernelArgs":["console=hvc0","tsc=reliable","panic=0"],"initArgs":[]}}
```
`SystemPlatform` (`XPCKeys.systemPlatform`) = `{"os":"linux","architecture":"arm64"}`; `SystemPlatform.current` maps host arm64→`.linuxArm`, amd64→`.linuxAmd` (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ClientKernel.swift:100-111`).

**`ImageDescription`** — `.../Image/ImageDescription.swift:20-31`: `{reference: String, descriptor: Descriptor}`. `Descriptor` (ContainerizationOCI) live sample `{"mediaType":…,"digest":"sha256:…","size":2319}` plus optional `urls`, `annotations`, `platform`.

**`ContainerStatus`** — `.../ContainerStatus.swift:19-29`: `{state: RuntimeStatus, networks: [Attachment], startedDate: Date?}` (used by the CLI's `ManagedContainer` display wrapper, not by an XPC route).

### 2.3 Kernel routes → `KernelHarness` → `KernelService`

Registered `APIServer+Start.swift:270-271`; handler `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Kernel/KernelHarness.swift`.

| Route | Request | Reply | Cite |
|---|---|---|---|
| `getDefaultKernel` | `systemPlatform` (data, `SystemPlatform`, **required**) | `kernel` (data, `Kernel`) | `KernelHarness.swift:61-71`; client `ClientKernel.swift:76-97` |
| `installKernel` | `kernelFilePath` (string, req), `systemPlatform` (data, req), `kernelForce` (bool), `kernelTarURL` (string, opt — if absent the file path is treated as a kernel binary), `kernelDigest` (string, opt, **added in 1.2.x**), `progressUpdateEndpoint` (endpoint, opt) | — | `KernelHarness.swift:34-58`; client `ClientKernel.swift:33-73` |

`getDefaultKernel` maps a server `notFound` into a friendlier message client-side (`ClientKernel.swift:90-96`).

### 2.4 Network routes → `NetworksHarness` → `NetworksService`

Registered `APIServer+Start.swift:351-355`. **`networkCreate` is only registered on macOS 26+** (`if #available(macOS 26, *)`, `:351-353`) — on older macOS it is an unregistered route, i.e. the request hangs with no reply.

| Route | Request | Reply | Cite |
|---|---|---|---|
| `networkCreate` | `networkConfig` (data, `NetworkConfiguration`, req); `networkId` (string) is set by the client at `NetworkClient.swift:78` but **the server never reads it** | `networkResource` (data, `NetworkResource`) | `NetworksHarness.swift:44-57` |
| `networkList` | none | `networkResources` (data, `[NetworkResource]`) | `NetworksHarness.swift:34-41` |
| `networkDelete` | `networkId` (string, req) | — | `NetworksHarness.swift:60-68` |

There is no `networkInspect` route — the CLI filters `networkList` client-side (`NetworkInspect.swift:24`), as does `NetworkClient.get(id:)` (`NetworkClient.swift:113-119`).

`networkResources` is simply the reply key carrying the `[NetworkResource]` array; `networkResource` (singular) carries the one created. Default network is named `"default"` and created at boot with `mode: .nat`, `plugin: "container-network-vmnet"`, label `com.apple.container.resource.role=builtin` (`APIServer+Start.swift:324-347`). `"none"` is a reserved name meaning "no attachment" (`NetworkClient.swift:47`), handled entirely client-side.

### 2.5 Volume routes → `VolumesHarness` → `VolumesService`

Registered `APIServer+Start.swift:371-375`; handler `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Volumes/VolumesHarness.swift`.

| Route | Request | Reply | Cite |
|---|---|---|---|
| `volumeCreate` | `volumeName` (string, req), `volumeDriver` (string, dflt `"local"`), `volumeDriverOpts` (data, `[String:String]`), `volumeLabels` (data, `[String:String]`) | `volume` (data, `VolumeConfiguration`) | `:43-70` |
| `volumeList` | none | `volumes` (data, `[VolumeConfiguration]`) | `:33-40` |
| `volumeInspect` | `volumeName` (string) | `volume` (data) | `:83-94` |
| `volumeDelete` | `volumeName` (string) | — | `:73-80` |
| `volumeDiskUsage` | `volumeName` (string) | `volumeSize` (uint64) | `:97-106` |

`--size` is passed as `driverOpts["size"]`, **not** as `XPCKeys.volumeSize` (`/Users/michael/local/apple-container/Sources/ContainerCommands/Volume/VolumeCreate.swift:48-52`). `XPCKeys.volumeReadonly` and `.volumeContainerId` are declared (`XPC+.swift:128-129`) but unused. `VolumeError` cases surface as `.invalidArgument` via the `XPCServer` string sniff (`XPCServer.swift:234-237`); the client detects "already exists" by substring match (`Utility.swift:390-396`).

### 2.6 Health / disk-usage / plugin routes

| Route | Request | Reply | Cite |
|---|---|---|---|
| `ping` | none | `appRoot`, `installRoot`, `logRoot` (opt), `apiServerVersion`, `apiServerCommit`, `apiServerBuild`, `apiServerAppName` — **all strings** | `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/HealthCheck/HealthCheckHarness.swift:40-53` |
| `systemDiskUsage` | none | `diskUsageStats` (data, `DiskUsageStats`) | `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/DiskUsage/DiskUsageHarness.swift:34-50` |
| `pluginList` | none | `plugins` (data, `[Plugin]`) | `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/Plugin/PluginsHarness.swift:83-91` |
| `pluginGet` | `pluginName` (string) | `plugin` (data, `Plugin`) | `:44-56` |
| `pluginLoad` / `pluginUnload` / `pluginRestart` | `pluginName` (string) | — | `:32-41`, `:71-80`, `:59-68` |

`DiskUsageStats` = `{images, containers, volumes}` each `ResourceUsage {total: Int, active: Int, sizeInBytes: UInt64, reclaimable: UInt64}` (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/DiskUsage.swift:19-57`). `Plugin` = `{binaryURL: URL, config: PluginConfig, resourceURL: URL?}` (`/Users/michael/local/apple-container/Sources/ContainerPlugin/Plugin.swift:21-38`).

---

## 3. Client-side work the apiserver does NOT do

This is the bulk of a .NET port. `container create/run` does **8+ XPC round trips across two mach services before it ever calls `containerCreate`**.

### 3.1 Container ID
`Utility.createContainerID(name:)` — `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/Utility.swift:35-40`: `name ?? UUID().uuidString.lowercased()`. Validated client-side *and* server-side against `ManagedContainer.nameValid` — `^[a-zA-Z0-9][a-zA-Z0-9_.-]+$`, ≤63 chars (`/Users/michael/local/apple-container/Sources/ContainerResource/Container/ManagedContainer.swift:30-37`). Note the `+` quantifier: **single-character IDs are rejected**.

### 3.2 Image resolution & config merge — `containerConfigFromFlags`
`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/Utility.swift:67-267`:

1. Resolve platform from `--platform`/`--os`/`--arch` (`DefaultPlatform.resolveWithDefaults`, `:80-85`).
2. `ClientImage.fetch(reference:…)` — `list` on the images service, local match, else `pull` (`ClientImage.swift:354-378`). **Reference normalization is client-side**: prepend the configured registry domain if no domain, add `:latest`, add `library/` for docker.io (`ClientImage.swift:116-136`).
3. `img.getCreateSnapshot(platform:)` → `snapshotGet`, and on `notFound` → `imageUnpack` + `snapshotGet` (`ClientImage.swift:458-469`).
4. **Kernel selection**: `--kernel <path>` → `Kernel(path:platform:)` built locally; else `ClientKernel.getDefaultKernel(for: .current)` (route `getDefaultKernel`). Then `--kernel-arg` values are **appended client-side** to `kernel.commandLine.kernelArgs` (`Utility.swift:331-349`). The `kernel` blob sent to `containerCreate` is fully composed by the client.
5. **Init image / vminitd**: `management.initImage ?? containerSystemConfig.vminit.image`; the CLI fetches **and unpacks** it itself (`Utility.swift:126-140`) and passes the reference in `XPCKeys.initImage`. The apiserver then resolves it to a block filesystem (`ContainersService.getInitBlock`, `:1089`).
6. **Entrypoint/cmd/env/workdir/user merge**: `Parser.process` — `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/Parser.swift:263-330`. Env = image `config.env` (**only entries containing `=`**, `:130`) + `--env-file` + `--env`, deduped by key with last-wins (`:137-142`). Workdir = `--workdir` ?? image `workingDir` ?? `/`. Args: `--entrypoint` overrides image entrypoint **and suppresses image `cmd`**; positional args replace `cmd`; empty result → `invalidArgument`. User: `--user` → `.raw`, else `--uid`+`--gid` → `.id`, else image `config.user` → `.raw`, else `.id(0,0)`; a lone `--gid` becomes a supplemental group (`Parser.swift:69-95`). `stopSignal` is copied from the image config (`Utility.swift:260`). **The apiserver never reads an image config.**
7. Resources: `Parser.resources` (`:105-124`) — memory is parsed then **rounded to MiB** (`memoryStringAsMiB(...).mib()`, `:120`).
8. `shmSize` parsed to bytes (`Utility.swift:190-194`).
9. **Rosetta**: `config.rosetta = --rosetta || (host arm64 && target amd64)` — auto-enabled (`Utility.swift:231`); `--rosetta` on non-arm64 throws `unsupported` (`:233-235`).
10. `capAdd`/`capDrop` normalized to uppercase `CAP_*` (`Parser.swift:1047`), `maskedPaths`/`readonlyPaths` support the sentinel `NONE` (`Parser.swift:1090-1133`).

### 3.3 Mounts / volumes / tmpfs
`Utility.swift:163-188`. `Parser.tmpfsMounts` (`Parser.swift:342-372`) → `Filesystem.tmpfs(destination:options:)` with `source="tmpfs"`, dedup by normalized destination. `Parser.volumes`/`Parser.mounts` return `VolumeOrFilesystem` (`Parser.swift:51-54`); `.filesystem` passes through, `.volume` triggers **`getOrCreateVolume`** — a `volumeCreate` XPC call, falling back to `volumeInspect` on "already exists" (`Utility.swift:371-403`) — then becomes `Filesystem.volume(name:format:source:destination:options:)` using the volume's on-disk `source` and `format`. Anonymous volumes get label `com.apple.container.resource.anonymous` (`:372`). Mount sources are made absolute against CWD client-side (`Filesystem.swift:159-165`). **The daemon never creates a volume implicitly.**

### 3.4 Networks & DNS
`Utility.swift:198-229`. `--network name[,mac=…][,mtu=…]` parsed by `Parser.network` (`Parser.swift:850-911`; mtu 1280–65535). `"none"` → `config.networks = []` and must be the only value. Otherwise the CLI calls `networkList` to find the builtin network id (`NetworkClient.builtin`, `NetworkClient.swift:142-146`), builds `[AttachmentConfiguration]` via `Utility.getAttachmentConfigurations` (`:269-329`) — **the first attachment gets an FQDN hostname** `"<id>.<dnsDomain>."` (or `"<id>."` if the id already contains a dot), the rest get the bare container id; default mtu 1280. It then **validates each network exists** with another `networkList` per attachment (`:214-216`). Non-default networks require macOS 26 (`:300-305`). MAC format validated client-side (`Utility.swift:59-65`).

DNS: `--no-dns` → `config.dns = nil`; else `DNSConfiguration(nameservers: --dns, domain: --dns-domain ?? systemConfig.dns.domain, searchDomains: --dns-search, options: --dns-option)` (`Utility.swift:219-229`). Host-side `/etc/resolver/containerization.<domain>` files and pf redirect anchors are managed by `HostDNSResolver` (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/HostDNSResolver.swift:23-61`; nameserver 127.0.0.1, port 2053, or 1053 with a `localhost:` option) and `PacketFilter` (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/PacketFilter.swift:21-50`, anchor `com.apple.container` in `/etc/pf.anchors`). Both need **root** and are used by `container system dns`, not by run/create.

### 3.5 Publish ports / sockets
Parsed client-side into `config.publishedPorts` / `config.publishedSockets` (`Utility.swift:239-249`; `Parser.publishPort` `:640`, `Parser.publishSocket` `:774-830`). Limits: ≤64 port specs, no overlapping host ports (`Utility.swift:240-245`). `--publish-socket host:container` pre-creates the host directory and refuses an existing socket file (`Parser.swift:795-816`).

**The actual forwarding is entirely server-side**, in the runtime plugin, not the CLI: `RuntimeService.startSocketForwarders` (`/Users/michael/local/apple-container/Sources/Services/RuntimeLinux/Server/RuntimeService.swift:953-1024`, using `Sources/SocketForwarder/{TCPForwarder,UDPForwarder}.swift`) and `publishedSockets` at `RuntimeService.swift:1110`. A .NET client only has to fill the config fields. `containerDial(id, port)` returns a **connected vsock fd** for ad-hoc use (`ContainerClient.swift:292-314`).

### 3.6 TTY / stdio — `ProcessIO`
`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ProcessIO.swift:46-157`. The client passes **three separate fds** (`stdin`, `stdout`, `stderr` keys), never a pty master:

- `tty && interactive` → the client puts its **own** `STDIN_FILENO` into raw mode via `Terminal(descriptor:)` + `setraw()` (`:47-54`). The guest-side pty is created by the runtime from `ProcessConfiguration.terminal`.
- `stdin` pipe only when `--interactive`; host stdin is set non-blocking and pumped into the pipe (`:58-84`). `stdio[0]` = pipe **read** end.
- `stdout` pipe unless `--detach`; `stdio[1]` = pipe **write** end (`:86-115`).
- `stderr` pipe **only when `!detach && !tty`** — with a TTY, stderr is merged into stdout (`:117-142`). The server mirrors this: `RuntimeService.swift:250-258` sets `stderr = nil` when `config.initProcess.terminal`.
- Bootstrap, `createProcess`: same 3-key convention; a nil handle simply omits the key (`ContainerClient.swift:130-144`, `:240-254`). `startProcess` passes **no fds**.
- After `start()`, the client closes its copies of the far ends (`closeAfterStart`, `:231-235`).
- Server side: stdout/stderr are **tee'd** into `stdio.log` via `MultiWriter` regardless of whether the client passed fds (`RuntimeService.swift:241-258`).

`handleProcess` (`:159-229`) is the attach loop: `start()` → `wait()` → drain io (3 s timeout, `:241-259`); with a TTY it sends an initial `containerResize` and then one per `SIGWINCH`; without a TTY it forwards `SIGTERM/INT/USR1/USR2/WINCH` as `containerKill` (`:28-34`, `:198-214`).

### 3.7 logs / stats / copy / export
- `logs`: two fds, index 0 = `<bundle>/stdio.log` (`/Users/michael/local/apple-container/Sources/Services/Runtime/RuntimeClient/Bundle+Log.swift:22-24`), index 1 = `<bundle>/vminitd.log` (`/Users/michael/local/apple-container/Sources/ContainerResource/Container/Bundle.swift:37-39`). **Plain, unstructured, un-timestamped UTF-8 text** — stdout and stderr interleaved into one file. `-n` and `-f` (seek-to-end + readability handler, re-seek on truncation) are pure client logic (`/Users/michael/local/apple-container/Sources/ContainerCommands/Container/ContainerLogs.swift:60-140`).
- `stats`: single-shot. The CLI takes **two samples 2 s apart** and computes CPU% itself (`ContainerStats.swift:162-198`); streaming is a client loop with ANSI alt-screen (`:110-154`).
- `copyIn`/`copyOut`: the daemon does the work over the runtime's own channel (`ContainersService.swift:766-787`); the CLI only resolves `container:path` vs local path, absolutizes, and handles trailing-`/` directory semantics (`ContainerCopy.swift:32-118`). No archive crosses XPC.
- `export`: the client picks a temp path, calls `containerExport` with it, then moves or streams the file (`ContainerExport.swift:46-75`). The daemon writes an **EXT4→tar** export via `EXT4Reader.export(archive:)`; since 1.2.x it snapshots the disk first if the container is **running** (`ContainersService.swift:900-920`). `Archiver` (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/Archiver.swift:31-37`) defaults to `paxRestricted` + `gzip` but is used for kernel tars, not `container export`.

---

## 4. Exact call sequences

`(A)` = `com.apple.container.apiserver`, `(I)` = `com.apple.container.core.container-core-images`.

### `container create <image> [args]` — `ContainerCreate.swift:58-114`
```
(A) ping                       ← Application.loadContainerSystemConfig, Application.swift:148-158
(I) imageList                  ← ClientImage.fetch → get → list
(I) imagePull                  ← only if not found locally  [+ progressUpdateEndpoint]
(I) snapshotGet                ← getCreateSnapshot
(I) imageUnpack + snapshotGet  ← only if snapshotGet → notFound
(A) getDefaultKernel           ← unless --kernel
(I) imageList / imagePull      ← init image (vminitd)
(I) snapshotGet [+imageUnpack] ← init image
(A) volumeCreate | volumeInspect   ← once per -v named/anon volume
(A) networkList                ← builtin network id
(A) networkList                ← once more per attachment, to validate
(I) contentGet ×3              ← index → manifest → config blobs, for entrypoint/cmd/env/user
(A) containerCreate            {containerConfig, kernel, containerOptions, initImage?}
```
Prints the id. No process is started.

### `container start [-a] [-i] <id>` — `ContainerStart.swift:44-115`
```
(A) containerList {ids:[id]}   ← client.get(id:)   — short-circuits with a print if already running
(A) containerBootstrap         {id, stdin?, stdout?, stderr?, dynamicEnv}
      dynamicEnv = {"SSH_AUTH_SOCK": …} if set in the host env  (:90-93)
(A) containerStartProcess      {id, processIdentifier=id}
   detached → return after startProcess
   attached → (A) containerWait {id, processIdentifier=id}   [blocking]
            + (A) containerResize on SIGWINCH  (tty)
            + (A) containerKill  on signals    (no tty)
```
On any failure after bootstrap: `(A) containerStop` (`:107`).

### `container run -d <image>` — `ContainerRun.swift:65-181`
```
(A) containerList {ids:[id]}   ← must NOT exist, else `exists`
… identical create sequence …
(A) containerCreate
(A) containerBootstrap         {id, dynamicEnv}   — no stdio keys when detached
(A) containerStartProcess      {id, processIdentifier=id}
```
Prints the id. Non-detached adds `containerWait` (+resize/kill) exactly as `start -a`. On failure: `(A) containerDelete` (`:174`).

### `container exec -i <id> <cmd>` — `ContainerExec.swift:46-118`
```
(A) containerList {ids:[id]}   ← client.get(id:), then ensureRunning (ProcessUtils.swift:25-29)
    — process config is derived CLIENT-SIDE from container.configuration.initProcess:
      executable = args[0]; arguments = rest; terminal = --tty;
      environment += --env/--env-file; workingDirectory = --workdir;
      user/groups from Parser.user                       (:59-79)
(A) containerCreateProcess     {id, processIdentifier=<new lowercase uuid>, processConfig, stdin?, stdout?, stderr?}
(A) containerStartProcess      {id, processIdentifier}
(A) containerWait              {id, processIdentifier}   [blocking]
    + containerResize / containerKill as above
```

### `container stop [-s SIG] [-t N] <ids…>` — `ContainerStop.swift:58-105`
```
(A) containerList {labels:{"com.apple.container.plugin":"^(?!machine$)"}}   ← only with --all
(A) containerStop {id, stopOptions:{timeoutInSeconds, signal}}              ← concurrent, one per id
```

### `container delete [-f] <ids…>` — `ContainerDelete.swift:56-98`
```
(A) containerList  ← only with --all (skips running unless --force)
(A) containerDelete {id, forceDelete}   ← concurrent, one per id
```

### `container ls -a` — `ContainerList.swift:45-52`
```
(A) containerList {listFilters:{ids:[], status:null|"running", labels:{"com.apple.container.plugin":"^(?!machine$)"}}}
```
Single call. Table/JSON formatting is client-side (`ManagedContainer` wrapper, `ManagedContainer+ListDisplayable.swift:21-40`).

### `container inspect <ids…>` — `ContainerInspect.swift:37-54`
```
(A) containerList {}   ← unfiltered; filtered by id CLIENT-SIDE, then pretty JSON
```

### `container logs -f <id>` — `ContainerLogs.swift:48-58`
```
(A) containerLogs {id}   → reply key `logs` = xpc array of 2 fds
```
One call; `-f` is a local `readabilityHandler` loop on the returned fd.

### Image commands
```
pull    (A) ping ; (I) imagePull [+progressUpdateEndpoint] ; (I) imageUnpack     ImagePull.swift:56-91
inspect (A) ping ; (I) imageList ; then per platform (I) contentGet ×3           ImageInspect.swift + ClientImage+ImageResource.swift:28-60
list    (A) ping ; (I) imageList ; then (I) contentGet ×3 per image              ImageList.swift:32-73
tag     (A) ping ; (I) imageList ; (I) imageTag {imageReference, imageNewReference}  ImageTag.swift:23-27
delete  (A) ping ; (I) imageList ; (I) imageDelete {imageReference, garbageCollect:false} per image ; (I) imageCleanupOrphanedBlobs   ImageDelete.swift:36-65
load    (I) imageLoad {filePath, forceLoad} ; (I) imageUnpack per image          ImageLoad.swift:77-91
save    (A) ping ; (I) imageList ; (I) contentGet (manifest check) ; (I) imageSave {imageDescriptions, filePath, ociPlatform?}   ImageSave.swift:51-111
```

### Network / volume / system
```
network create  (A) networkCreate {networkId, networkConfig}   (macOS 26+ only)  NetworkCreate.swift:50-64
network list    (A) networkList                                                  NetworkList.swift:24-26
network inspect (A) networkList  ← filtered client-side                          NetworkInspect.swift:21-24
network delete  (A) networkList ; (A) networkDelete {networkId} per net          NetworkDelete.swift:38-100
volume create   (A) volumeCreate {volumeName, volumeDriver, volumeDriverOpts, volumeLabels}   VolumeCreate.swift:45-59
volume list     (A) volumeList                                                   VolumeList.swift:27-28
volume inspect  (A) volumeList  ← filtered client-side (not volumeInspect!)       VolumeInspect.swift:23-25
volume delete   (A) volumeList ; (A) volumeDelete {volumeName} per vol           VolumeDelete.swift:39-88
system status   launchctl print (ServiceManager.isRegistered) ; (A) ping timeout=10s   SystemStatus.swift:74-104
system df       (A) systemDiskUsage → fans out to (I) imageDiskUsage server-side  SystemDF.swift:21-22; DiskUsageService.swift:22-65
```

---

## 5. Progress reporting

**Yes — the client must host an XPC listener.** It is an **anonymous connection + endpoint**, not a mach service.

Client (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ProgressUpdateClient.swift:23-96`):
1. `xpc_connection_create(nil, nil)` — anonymous listener (`:39`).
2. Event handler accepts the incoming `XPC_TYPE_CONNECTION` (the "reversed connection"), installs a message handler on it, activates it (`:43-61`).
3. `xpc_endpoint_create(...)` → set into the **request** under key `progressUpdateEndpoint` (`:65`, `:70-75`).
4. After the reply arrives, the client calls `finish()` → `xpc_connection_cancel` (`:78-83`). Cancellation is how the server learns to stop.

Server (`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ProgressUpdateService.swift:23-82`): `init?(message:)` returns nil when the key is absent — **progress is entirely opt-in** (`:28-36`); `xpc_connection_create_from_endpoint`, then `xpc_connection_send_message` (fire-and-forget, **no reply expected**) with a plain dictionary of update keys (`:41-80`).

Update keys (all `XPCKeys`, `XPC+.swift:86-101`) — string-valued: `progressUpdateSetDescription`, `progressUpdateSetSubDescription`, `progressUpdateSetItemsName`. Int-valued (`int64`; **`0` means "absent"** — the reader skips zeros, `ProgressUpdateClient.swift:110-157`): `progressUpdateAddTasks`, `progressUpdateSetTasks`, `progressUpdateAddTotalTasks`, `progressUpdateSetTotalTasks`, `progressUpdateAddItems`, `progressUpdateSetItems`, `progressUpdateAddTotalItems`, `progressUpdateSetTotalItems`, `progressUpdateAddSize`, `progressUpdateSetSize`, `progressUpdateAddTotalSize`, `progressUpdateSetTotalSize`. One dictionary may carry several keys = several events. `.custom` events are dropped (`ProgressUpdateService.swift:75-78`).

Routes that honor it: `imagePull` (`ImagesServiceHarness.swift:53`), `imagePush` (`:79`), `imageUnpack` (`:234`), `installKernel` (`KernelHarness.swift:49`). **`containerCreate` does not** — the progress bar you see during `container run` is driven entirely by the client's own image/kernel/init-image steps (`Utility.swift:88-142`).

**A first .NET port can simply omit `progressUpdateEndpoint`.**

---

## 6. Images service (`com.apple.container.core.container-core-images`)

Separate launchd plugin: binary `/usr/local/libexec/container/plugins/container-core-images/bin/…`, config `/usr/local/libexec/container/plugins/container-core-images/config.toml` (`type = "core"`, `loadAtBoot = true`). Entry point `/Users/michael/local/apple-container/Sources/Plugins/CoreImages/ImagesHelper.swift:28-138` — it builds **one** `XPCServer` (`:61-65`) hosting **both** the images routes (`:94-105`) and the content-store routes (`:129-135`).

Route enum: `/Users/michael/local/apple-container/Sources/Services/ContainerImagesService/Client/ImageServiceXPCRoutes.swift:21-44`. Key enum: `/Users/michael/local/apple-container/Sources/Services/ContainerImagesService/Client/ImageServiceXPCKeys.swift:22-55`. Same envelope (`com.apple.container.xpc.route` / `.error`), same EUID check.

### Image routes → `ImagesServiceHarness`

| Route | Request | Reply | Cite |
|---|---|---|---|
| `imageList` | none | `imageDescriptions` (data, `[ImageDescription]`) | `ImagesServiceHarness.swift:110-116` |
| `imagePull` | `imageReference` (string, req), `ociPlatform` (data, `Platform`, opt), `insecureFlag` (bool), `maxConcurrentDownloads` (int64), `progressUpdateEndpoint` (endpoint, opt) | `imageDescription` (data) | `:37-61` |
| `imagePush` | `imageReference` (req), `ociPlatform` (opt), `insecureFlag` (bool), `progressUpdateEndpoint` (opt) | — | `:64-84` |
| `imageTag` | `imageReference` (req), `imageNewReference` (req) | `imageDescription` (data) | `:87-107` |
| `imageDelete` | `imageReference` (req), `garbageCollect` (bool) | — | `:119-131` |
| `imageSave` | `imageDescriptions` (data, `[ImageDescription]`, req), `filePath` (string, req), `ociPlatform` (opt) | — | `:134-160` |
| `imageLoad` | `filePath` (string, req), `forceLoad` (bool) | `imageDescriptions` (data), `rejectedMembers` (data, `[String]`) | `:163-182` |
| `imageUnpack` | `imageDescription` (data, req), `ociPlatform` (opt), `progressUpdateEndpoint` (opt) | — | `:220-239` |
| `snapshotGet` | `imageDescription` (data, req), `ociPlatform` (data, **required**) | `filesystem` (data, `Filesystem`) | `:262-284` |
| `snapshotDelete` | `imageDescription` (data, req), `ociPlatform` (opt) | — | `:242-259` |
| `imageCleanupOrphanedBlobs` | none | `digests` (data, `[String]`), `imageSize` (uint64) | `:185-192` |
| `imageDiskUsage` | `activeImageReferences` (data, `Set<String>`, opt) | `totalCount` (int64), `activeCount` (int64), `imageSize` (uint64), `reclaimableSize` (uint64) | `:195-213` |
| `imageBuild` | **declared but never registered** (`ImageServiceXPCRoutes.swift:27`) | | |

### Content-store routes → `ContentServiceHarness`
`/Users/michael/local/apple-container/Sources/Services/ContainerImagesService/Server/ContentServiceHarness.swift`, client `/Users/michael/local/apple-container/Sources/Services/ContainerImagesService/Client/RemoteContentStoreClient.swift`.

| Route | Request | Reply | Cite |
|---|---|---|---|
| `contentGet` | `digest` (string) | `contentPath` (string, **absolute host path to the blob file**) — or an `error` with code `notFound` | `:34-48` |
| `contentDelete` | `digests` (data, `[String]`) | `digests` (data, deleted), `imageSize` (uint64) | `:51-63` |
| `contentClean` | `digests` (data, `[String]` = keep-list) | `digests` (data), `imageSize` (uint64) | `:66-78` |
| `contentSize` | none | `imageSize` (uint64) | `:116-121` |
| `contentIngestStart` | none | `ingestSessionId` (string), `directory` (string path) | `:81-89` |
| `contentIngestComplete` | `ingestSessionId` (string) | `digests` (data, `[String]`) | `:103-113` |
| `contentIngestCancel` | `ingestSessionId` (string) | — | `:92-100` |

### How `ClientImage.get` / `config()` read image configs — **this is the key design point**

`ClientImage.get(reference:)` does **`imageList` + client-side matching** (`ClientImage.swift:185-245`); there is no server-side lookup route. Matching prefers images whose index descriptor annotation `containerizationImageName` equals the normalized reference, then falls back to `reference == input || reference == normalized`.

`ClientImage.index()` / `.manifest(for:)` / `.config(for:)` (`ClientImage.swift:43-73`) go through `RemoteContentStoreClient`, which sends **`contentGet` to get a file path**, then **`mmap`s/reads the blob from disk locally** and `JSONDecoder`-decodes it (`RemoteContentStoreClient.swift:34-64` → `LocalContent(path:)`). So:

> **Reading an image config is: 1 XPC call per blob to translate digest→path, then a normal file read.** Three round trips (index → manifest → config) plus three local file reads. A .NET client must be able to open files under `~/Library/Application Support/com.apple.container/content/…`. Blob bytes never traverse XPC.

`getFullImageSize` (`ClientImage.swift:197-218`) and `toImageResource` (`ClientImage+ImageResource.swift:28-56`, image `created` parsed as ISO-8601 with/without fractional seconds) build on the same mechanism.

---

## 7. Version / compatibility

### `ping` reply
`/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Server/HealthCheck/HealthCheckHarness.swift:40-53`; consumer `/Users/michael/local/apple-container/Sources/Services/ContainerAPIService/Client/ClientHealthCheck.swift:31-63`. All values are **xpc strings**; only `logRoot` may be absent.

| Key | Value | Live 1.3.0 sample |
|---|---|---|
| `appRoot` | `URL.absoluteString` (file URL) | `file:///Users/michael/Library/Application%20Support/com.apple.container/` |
| `installRoot` | `URL.absoluteString` | `file:///usr/local/` |
| `logRoot` | `FilePath.string` (plain path), **optional** | — |
| `apiServerVersion` | full banner, not a bare semver | `container-apiserver version 1.3.0 (build: release, commit: d6de569)` |
| `apiServerCommit` | full git sha or `"unspecified"` | `d6de5694200468d99a61662bfb9bb3aba763e3e5` |
| `apiServerBuild` | `debug` \| `release` | `release` |
| `apiServerAppName` | constant | `container-apiserver` |

The client **throws `internalError`** if any of `appRoot`, `installRoot`, `apiServerVersion`, `apiServerCommit`, `apiServerBuild`, `apiServerAppName` is missing (`ClientHealthCheck.swift:35-53`) — so all six are effectively required. To get a semver, parse `apiServerVersion`; there is no numeric protocol-version field.

### Protocol-visible changes 1.1.0 → 1.2.2 → 1.3.0

Tags in this clone: `1.1.0` (2026-07-01, `5973b9c`), `1.2.0`, `1.2.1`, `1.2.2` (2026-08-07, `0190097`), `1.3.0` (2026-08-20, `d6de569`). Source layout (`Sources/ContainerXPC`, `Sources/ContainerResource`, `Sources/Services/ContainerAPIService`, `Sources/Services/ContainerImagesService`) is **identical across all three tags** — no path moves (`git ls-tree -d --name-only <tag> Sources/`), and `Sources/ContainerXPC` has the same 5 files at every tag.

**1.1.0 → 1.2.2**

| Change | Kind | Cite |
|---|---|---|
| `XPCKeys.kernelDigest` **added** | new request key on `installKernel` (optional string, expected sha256 of the kernel tar) | `git diff 1.1.0 1.2.2 -- Sources/Services/ContainerAPIService/Client/XPC+.swift` → `XPC+.swift:115` |
| `ContainerConfiguration.maskedPaths: [String]?` **added** | new Codable field | `ContainerConfiguration.swift:47`, `:96`, `:106` |
| `ContainerConfiguration.readonlyPaths: [String]?` **added** | new Codable field | same |
| `kernelTarURL` now accepts a **bare filesystem path**, not just a URL with a scheme | request-value semantics loosened | `KernelHarness.swift:90-98` |
| `containerExport` now works on a **running** container (snapshots the disk first); previously `invalidState` unless stopped | behavioral | `ContainersService.swift:900-920` |
| `ManagedContainer.nameValid` gained a **≤63-char limit** | tighter validation on `containerCreate`/`Bootstrap`/`Delete`/`DiskUsage`/`Logs`/`Export` | `ManagedContainer.swift:30-37` |
| Explicit `ManagedContainer.nameValid` guards added to `bootstrap`, `create`, `delete`, `diskUsage`, `logs`, `export` harness handlers | new `invalidArgument` failure mode | `ContainersHarness.swift:57, 199, 271, 284, 304, 392` |
| `XPCMessage.error()` / `set(error:)` no longer `precondition`-crash on malformed payloads; malformed → `internalError: "received a malformed error payload from the XPC peer"`, encode failure → fallback JSON | robustness, no wire change | `XPCMessage.swift:78-111` |
| `ContainerClient.create` rethrows `ContainerizationError` unwrapped instead of wrapping in `internalError` | client-side error fidelity | `ContainerClient.swift:74-75` |

**No route added or removed.** `ImagesServiceXPCRoute` / `ImagesServiceXPCKeys` diffs are **empty**.

**1.2.2 → 1.3.0** — **zero protocol-visible changes.**
- `git diff --stat 1.2.2 1.3.0 -- Sources/ContainerXPC` → empty.
- `git diff --stat 1.2.2 1.3.0 -- Sources/ContainerResource` → empty.
- `git diff --stat 1.2.2 1.3.0 -- Sources/Services/ContainerImagesService Sources/Plugins/CoreImages` → empty.
- `git diff 1.2.2 1.3.0 -- .../Client/XPC+.swift` → empty (no route, no key changed).
- Only changes under `ContainerAPIService`: `ClientImage.pull/fetch` default `scheme` `.auto` → `.https` (client default only; the wire field is still `insecureFlag` bool) — `ClientImage.swift:250, 357`; `VolumesService.volumeDiskUsage` now validates the volume name (new `invalidVolumeName` failure) — `VolumesService.swift:405-411`; plus `Parser.swift` / `Flags.swift` CLI-only additions and a new `Utility+PluginLoader.swift`. `RequestScheme.swift` did **not** move (present at both tags).

**Net: the wire protocol has been stable since 1.2.0.** A client written against 1.3.0 works against 1.2.x, and against 1.1.0 minus `kernelDigest` / `maskedPaths` / `readonlyPaths` (all optional).

---

## 8. Minimum route set for a first cider port

All on `com.apple.container.apiserver`. Notation: `key` → xpc type. Every request dictionary also carries `"com.apple.container.xpc.route"` → string. Every reply echoes the route key and may carry `"com.apple.container.xpc.error"` → data(JSON) instead of the success keys — **check that first, always.**

> **`containerState` is not a route.** Use `containerList` with an id filter. Listed below as "containerList/State".

---

### 8.1 `ping`

**Request**
```
"com.apple.container.xpc.route" : string = "ping"
```
**Reply**
```
appRoot          : string = "file:///Users/michael/Library/Application%20Support/com.apple.container/"
installRoot      : string = "file:///usr/local/"
logRoot          : string = "/Users/michael/Library/Logs/com.apple.container"      // optional
apiServerVersion : string = "container-apiserver version 1.3.0 (build: release, commit: d6de569)"
apiServerCommit  : string = "d6de5694200468d99a61662bfb9bb3aba763e3e5"
apiServerBuild   : string = "release"
apiServerAppName : string = "container-apiserver"
```

---

### 8.2 `containerList` (also serves as "get state")

**Request**
```
"com.apple.container.xpc.route" : string = "containerList"
listFilters : data = UTF-8 JSON
```
```json
{ "ids": ["myapp"], "status": "running", "labels": { "com.apple.container.plugin": "^(?!machine$)" } }
```
`ids` and `labels` are **required** (send `[]` / `{}`); `status` is optional (`"unknown"|"stopped"|"running"|"stopping"`); `labels` values are regexes. Omitting `listFilters` entirely = `.all`.

**Reply**
```
containers : data = UTF-8 JSON array of ContainerSnapshot
```
```json
[{
  "configuration": {
    "id": "myapp",
    "image": { "reference": "docker.io/library/alpine:3.20",
               "descriptor": { "mediaType": "application/vnd.oci.image.index.v1+json",
                               "digest": "sha256:d9e853e8…", "size": 9226 } },
    "initProcess": { "executable": "sleep", "arguments": ["60"],
                     "environment": ["PATH=/usr/local/sbin:…"], "workingDirectory": "/",
                     "terminal": false, "user": { "id": { "uid": 0, "gid": 0 } },
                     "supplementalGroups": [], "rlimits": [] },
    "mounts": [], "publishedPorts": [], "publishedSockets": [],
    "labels": {}, "sysctls": {},
    "networks": [{ "network": "default", "options": { "hostname": "myapp", "mtu": 1280 } }],
    "dns": { "nameservers": [], "searchDomains": [], "options": [] },
    "rosetta": false, "platform": { "os": "linux", "architecture": "arm64" },
    "resources": { "cpus": 4, "memoryInBytes": 1073741824, "cpuOverhead": 1 },
    "runtimeHandler": "container-runtime-linux",
    "virtualization": false, "ssh": false, "readOnly": false, "useInit": false,
    "capAdd": [], "capDrop": [],
    "creationDate": 809330969.025174
  },
  "status": "running",
  "networks": [{ "network": "default", "hostname": "myapp",
                 "ipv4Address": "192.168.64.2/24", "ipv4Gateway": "192.168.64.1",
                 "ipv6Address": "fd3e:bc7a:df05:1995:f4b2:c1ff:fe2d:3baa/64",
                 "macAddress": "f6:b2:c1:2d:3b:aa", "variant": "reserved" }],
  "startedDate": 809331000.5
}]
```
If the key is absent, treat as `[]` (`ContainerClient.swift:96-99`). **Dates are seconds since 2001-01-01 — add `978307200` for Unix seconds.**

---

### 8.3 `containerCreate`

**Request**
```
"com.apple.container.xpc.route" : string = "containerCreate"
containerConfig  : data   = UTF-8 JSON ContainerConfiguration     (required)
kernel           : data   = UTF-8 JSON Kernel                     (required)
containerOptions : data   = UTF-8 JSON ContainerCreateOptions     (optional → {autoRemove:false})
initImage        : string = "ghcr.io/apple/containerization/vminit:0.41.0"   (optional)
runtimeData      : data   = opaque, runtime-specific              (optional)
```
`containerConfig`:
```json
{
  "id": "myapp",
  "image": { "reference": "docker.io/library/alpine:3.20",
             "descriptor": { "mediaType": "application/vnd.oci.image.index.v1+json",
                             "digest": "sha256:d9e853e8…", "size": 9226 } },
  "initProcess": { "executable": "/bin/sh", "arguments": ["-c","sleep 3600"],
                   "environment": ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],
                   "workingDirectory": "/", "terminal": false,
                   "user": { "id": { "uid": 0, "gid": 0 } },
                   "supplementalGroups": [], "rlimits": [] },
  "mounts": [
    { "type": { "virtiofs": {} }, "source": "/Users/michael/data", "destination": "/data", "options": ["ro"] },
    { "type": { "tmpfs": {} },    "source": "tmpfs",               "destination": "/run",  "options": [] }
  ],
  "publishedPorts": [
    { "hostAddress": "0.0.0.0", "hostPort": 8080, "containerPort": 80, "proto": "tcp", "count": 1 }
  ],
  "publishedSockets": [],
  "labels": { "com.chillicream.cider.system": "app" },
  "sysctls": {},
  "networks": [{ "network": "default", "options": { "hostname": "myapp.test.", "mtu": 1280 } }],
  "dns": { "nameservers": ["1.1.1.1"], "domain": "test", "searchDomains": [], "options": [] },
  "rosetta": false,
  "platform": { "os": "linux", "architecture": "arm64" },
  "resources": { "cpus": 2, "memoryInBytes": 1073741824, "cpuOverhead": 1 },
  "runtimeHandler": "container-runtime-linux",
  "virtualization": false, "ssh": false, "readOnly": false, "useInit": false,
  "capAdd": [], "capDrop": [], "creationDate": 809330969.025174
}
```
`kernel` (get this from `getDefaultKernel`, do not synthesize):
```json
{ "path": "file:///Users/michael/Library/Application%20Support/com.apple.container/kernels/vmlinux-6.18.15-186",
  "platform": { "os": "linux", "architecture": "arm64" },
  "commandLine": { "kernelArgs": ["console=hvc0","tsc=reliable","panic=0"], "initArgs": [] } }
```
`containerOptions`: `{"autoRemove": false}` (add `"rootFsOverride": <Filesystem>` to skip image unpack).

**Reply** — route key only. Errors: `exists` (id or hostname taken), `notFound` (runtime plugin missing), `invalidArgument` (bad id, memory < 200 MiB).

**Preconditions the daemon will not do for you:** the image snapshot must already exist (call `snapshotGet`/`imageUnpack` on the images service first), any named volume must already exist, and `networks[].network` must name an existing network.

---

### 8.4 `containerBootstrap`

**Request**
```
"com.apple.container.xpc.route" : string = "containerBootstrap"
id         : string = "myapp"                 (required, must pass ^[a-zA-Z0-9][a-zA-Z0-9_.-]+$ and ≤63)
stdin      : fd                               (optional — omit for detached)
stdout     : fd                               (optional)
stderr     : fd                               (optional — omit when initProcess.terminal is true)
dynamicEnv : data = UTF-8 JSON [String:String]  (optional)
```
```json
{ "SSH_AUTH_SOCK": "/private/tmp/com.apple.launchd.xxx/Listeners" }
```
**Reply** — route key only. This boots the VM and creates the init process, but does **not** start it. Errors: `notFound`, `invalidState`, `invalidArgument`.

---

### 8.5 `containerStartProcess`

**Request**
```
"com.apple.container.xpc.route" : string = "containerStartProcess"
id                : string = "myapp"
processIdentifier : string = "myapp"      // == id for the init process; the exec uuid otherwise
```
**Reply** — route key only.

---

### 8.6 `containerWait`

**Request**
```
"com.apple.container.xpc.route" : string = "containerWait"
id                : string = "myapp"
processIdentifier : string = "myapp"
```
**Reply**
```
exitCode : int64 = 0
exitedAt : date  = xpc_date, Int64 NANOSECONDS since the UNIX epoch (e.g. 1787654321000000000)
```
Blocks until exit; **send with no timeout.** Note `ClientProcessImpl.wait()` reads only `exitCode` (`ClientProcess.swift:105-107`) — `exitedAt` is available but unused by the CLI.

---

### 8.7 `containerStop`

**Request**
```
"com.apple.container.xpc.route" : string = "containerStop"
id          : string = "myapp"
stopOptions : data   = UTF-8 JSON   (REQUIRED — omitting it yields invalidArgument "empty StopOptions")
```
```json
{ "timeoutInSeconds": 5, "signal": "SIGTERM" }
```
`signal` may be `null`; the daemon then uses `configuration.stopSignal`, then `"SIGTERM"`. `timeoutInSeconds` is `Int32` and required.

**Reply** — route key only. Idempotent: stopping an already-stopped container succeeds (`ContainersService.swift:625-631`).

---

### 8.8 `containerKill`

**Request**
```
"com.apple.container.xpc.route" : string = "containerKill"
id                : string = "myapp"
processIdentifier : string = "myapp"
signal            : string = "KILL"        // ⚠ MUST be a STRING, never int64
```
Accepted forms: `"KILL"`, `"TERM"`, `"SIGKILL"`, `"SIGTERM"`, `"HUP"`, `"QUIT"`, `"USR1"`, `"USR2"`, `"WINCH"`, `"INT"`.

**Reply** — route key only. When `processIdentifier == id` and the signal resolves to `SIGKILL`, the daemon also runs its full container-exit cleanup (`ContainersService.swift:598-600`).

---

### 8.9 `containerDelete`

**Request**
```
"com.apple.container.xpc.route" : string = "containerDelete"
id          : string = "myapp"    (name-validated)
forceDelete : bool   = false      // true also stops a running container
```
**Reply** — route key only. Errors: `notFound`, `invalidState` (running without force), `invalidArgument` (bad id).

---

### 8.10 Two extras you will want immediately

`getDefaultKernel` — you cannot build a valid `containerCreate` without it.
```
route            : "getDefaultKernel"
systemPlatform   : data = {"os":"linux","architecture":"arm64"}
→ kernel         : data = <Kernel JSON, see §8.3>
```

`containerLogs` — the only way to read output from a detached container.
```
route : "containerLogs"
id    : string = "myapp"
→ logs : xpc_array[ xpc_fd, xpc_fd ]     // [0] stdio.log (stdout+stderr merged), [1] vminitd.log
```
Both are plain UTF-8 text files, no framing, no timestamps. Use `xpc_array_dup_fd(obj, 0)` / `(obj, 1)`.

---

### 8.11 Gotchas checklist for the .NET implementation

1. `System.Text.Json` default camelCase policy is **wrong here** — Swift emits the property name verbatim. Use exact-name mapping.
2. `Date` = `double` seconds since **2001-01-01**, not Unix, not ISO-8601. XPC `date` values *are* Unix nanoseconds. Two different conventions in the same protocol.
3. Enums with payloads are single-key objects; payload-free cases are `{}`, not `""`.
4. `xpc` `data` values are raw JSON bytes — do **not** base64-encode.
5. An **unknown route gets no reply at all**; always set your own timeout.
6. `signal` must be a string.
7. `stopOptions` is mandatory on `containerStop`.
8. `listFilters.ids` and `.labels` must be present even when empty.
9. Errors ride inside a normal reply dictionary under `com.apple.container.xpc.error`; check it before reading success keys.
10. Container ids need ≥2 characters and ≤63.
11. `networkCreate` is unregistered below macOS 26 → the call hangs.
12. Images live behind a **different mach service**, and reading an image config means `contentGet` + a local file read.