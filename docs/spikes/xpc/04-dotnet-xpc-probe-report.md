<!-- Generated 2026-08-25 by a research agent during planning of the XPC runtime transport; source of truth is the apple/container 1.3.0 tree and the cider tree at that date. -->

All measurements are in, and the final check shows zero `xpcprobe-*` containers remain (the other containers listed — `buildkit`, `cider-dns-bridge-*`, `e2e-dbg-*` — belong to the user's concurrently running e2e session and were never touched). Here is the report.

# XPC probe report: .NET 10 → `com.apple.container.apiserver`

## Verdict

**Proven.** A plain .NET 10 process (unsigned `dotnet` host, no entitlements) talks to container-apiserver 1.3.0 directly over XPC using P/Invoke into `/usr/lib/libSystem.B.dylib` and a hand-built Objective-C global block. Every route tried worked: `ping`, `containerList`, `getDefaultKernel`, `containerCreate`, `containerDelete`. The only server-side authorization is an audit-token EUID match (`Sources/ContainerXPC/XPCServer.swift:175-193`); there is no code-signing or entitlement check.

## Latency (Apple M-series, macOS 26.6.2, .NET 10.0.10, Release)

| Operation | via XPC (min / median / p99 ms) | via CLI (min / median / p99 ms) |
|---|---|---|
| `ping` (warm connection, n=100) | **0.021 / 0.025 / 0.104** | `container system status` ≈ 18.7 |
| connect + `ping` (fresh connection each, n=10) | 0.075 / 0.118 / 0.247 | — |
| `containerList` (3.5 KB payload, n=100) | **0.090 / 0.104 / 0.175** | `container ls -a` 18.5 / 19.0 / 21.1 |
| `containerCreate` alpine:3.20 (n=10) | **7.7 / 11.3 / 17.0** | `container create` 41.3 / 47.5 / 89.6 (python cross-check: 40.8 / 42.3 / 45.2) |
| `containerDelete` force (n=10) | **0.64 / 0.84 / 1.37** | `container delete` 17.7 / 19.0 / 21.6 (python: 14.2 / 15.2 / 15.8) |
| create+delete cycle | 8.7 / 12.2 / 17.8 | 59.0 / 65.7 / 109.3 (python: 56–60) |

Floor: ~25 µs per round trip. The CLI's ~19 ms fixed cost per invocation (process spawn + Swift runtime + XPC) dominates everything but create; create itself is ~4x faster over XPC because the CLI also re-resolves the image, kernel and init image before sending.

## What worked: P/Invoke surface and block layout

Source: `/private/tmp/claude-501/-Users-michael-local-cider/e4a65df1-d1d7-4a51-8638-c7835b21a6d6/scratchpad/xpc-probe/XpcProbe/Xpc.cs`

All symbols resolve from `/usr/lib/libSystem.B.dylib` (libxpc/libsystem_blocks live in the shared cache). Signatures that worked (`LibraryImport`, `StringMarshalling.Utf8`):

```csharp
nint  xpc_connection_create_mach_service(string name, nint targetq /*NULL*/, ulong flags /*0*/);
void  xpc_connection_set_event_handler(nint connection, nint handlerBlock);
void  xpc_connection_activate(nint);  void xpc_connection_resume(nint);  // both verified
void  xpc_connection_cancel(nint);    int  xpc_connection_get_pid(nint);
nint  xpc_connection_send_message_with_reply_sync(nint connection, nint message);
nint  xpc_dictionary_create(nint keys /*0*/, nint values /*0*/, nuint count /*0*/);
void  xpc_dictionary_set_string(nint, string key, string value);
void  xpc_dictionary_set_data(nint, string key, byte* bytes, nuint length);
void  xpc_dictionary_set_uint64/int64(nint, string key, ulong/long);
void  xpc_dictionary_set_bool(nint, string key, [MarshalAs(UnmanagedType.U1)] bool);
nint  xpc_dictionary_get_string(nint, string key);          // MUST be nint: a `string` return would free() dict-owned memory
byte* xpc_dictionary_get_data(nint, string key, out nuint length);
ulong/long/bool xpc_dictionary_get_uint64/int64/get_bool(...) ([return: MarshalAs(U1)] for bool)
nuint xpc_dictionary_get_count(nint);   bool xpc_dictionary_apply(nint, nint applierBlock);
nint  xpc_get_type(nint);  nint xpc_type_get_name(nint type);  void xpc_release(nint);
nint  xpc_copy_description(nint);  void free(nint);           // malloc'd, caller frees
```

Block literal (verified working, 32 bytes: `isa@0, flags@8, reserved@12, invoke@16, descriptor@24`; descriptor 16 bytes `{reserved, size=32}`):

```csharp
struct Literal { nint isa; int flags; int reserved; nint invoke; Descriptor* descriptor; }
isa    = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock")  // 0x1f65ebc30
flags  = 1 << 28  (BLOCK_IS_GLOBAL)
invoke = (nint)(delegate* unmanaged<nint /*block*/, nint /*xpc_object_t*/, void>)&OnConnectionEvent   // [UnmanagedCallersOnly]
```
Applier block for `xpc_dictionary_apply`: `delegate* unmanaged<nint block, nint key, nint value, byte>` returning 1. The event handler is invoked on libdispatch worker threads and reverse-P/Invoke works fine there; it fires exactly once per connection (`XPCErrorDescription = "Connection invalid"`) after `xpc_connection_cancel`. No failure of the block approach was observed.

Protocol as used: request dict with `com.apple.container.xpc.route` = route; payloads as `xpc_data` JSON; errors as JSON `{code,message}` in `com.apple.container.xpc.error`. Reply type checked with `xpc_type_get_name(xpc_get_type(reply))` (`"error"` vs `"dictionary"`).

## Route payloads that worked

- `ping` → 6 strings: `appRoot`, `installRoot`, `apiServerVersion` = `"container-apiserver version 1.3.0 (build: release, commit: d6de569)"`, `apiServerCommit`, `apiServerBuild`, `apiServerAppName`.
- `containerList` → `containers` data: JSON array of `ContainerSnapshot` (`{configuration:{…}, id, status, …}`); `listFilters` is optional (`{"ids":[],"labels":{}}` = `.all`).
- `getDefaultKernel` with `systemPlatform` = `{"os":"linux","architecture":"arm64"}` → `kernel` data (253 bytes), reused verbatim for create:
  `{"path":"file:///Users/michael/Library/Application%20Support/com.apple.container/kernels/vmlinux-6.18.15-186","platform":{"os":"linux","architecture":"arm64"},"commandLine":{"initArgs":[],"kernelArgs":["console=hvc0","tsc=reliable","panic=0"]}}`
- `containerDelete`: `id` string + `forceDelete` bool.

### Minimal `containerCreate` the server accepted

Keys: `containerConfig` (below) + `kernel` (bytes from `getDefaultKernel`). `containerOptions`, `initImage`, `runtimeData` omitted.

```json
{"id":"xpcprobe-min",
 "image":{"reference":"docker.io/library/alpine:3.20",
          "descriptor":{"digest":"sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc",
                        "mediaType":"application/vnd.oci.image.index.v1+json","size":9226}},
 "initProcess":{"executable":"sleep","arguments":["60"],
                "environment":["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],
                "workingDirectory":"/","terminal":false,"user":{"id":{"uid":0,"gid":0}},
                "supplementalGroups":[],"rlimits":[]}}
```
The custom decoder (`Sources/ContainerResource/Container/ContainerConfiguration.swift`) defaults everything else — but note what the defaults mean: **`networks` defaults to `[]` (no network attachment at all, unlike the CLI which attaches `default` with `hostname`=id, `mtu`=1280)**, `creationDate` defaults to 1970-01-01, `resources` to 4 CPU / 1 GiB, `platform` to `{arm64, linux, variant v8}`. A real client should send the full config (the probe's `BuildConfig(minimal:false)` mirrors `container inspect xpcprobe-ref` exactly and was used for the 10 timed cycles).

## Server-side validation surprises (error JSON hit)

- `creationDate` must be a **number of seconds since 2001-01-01** (Swift `JSONDecoder` default), not the ISO string `container inspect` prints: `{"message":"DecodingError.typeMismatch: expected value of type Double. Path: creationDate. …","code":"unknown"}`.
- Missing `kernel`: `{"message":"kernel cannot be empty","code":"invalidArgument"}`.
- Image must already be in the local store (server never pulls): `{"message":"image docker.io/library/alpine:9.99 not found","code":"notFound"}`. The init image (`ghcr.io/apple/containerization/vminit:0.40.1`) must be local too; the CLI pre-fetches both before sending create.
- Invalid id (`^[a-zA-Z0-9][a-zA-Z0-9_.-]+$`, ≤63): `{"message":"container ID xpc probe! is not a valid container ID","code":"invalidArgument"}`.
- Memory < 200 MiB: `{"message":"minimum memory amount allowed is 200 MiB (got 104857600 bytes)","code":"invalidArgument"}`.
- Duplicate id: `{"code":"exists","message":"container already exists: xpcprobe-dup"}`; duplicate hostname across containers: `{"code":"exists","message":"hostname(s) already exist: [\"xpcprobe-h1\"]"}`.
- Delete unknown id: `{"code":"notFound",…}`; message with no route key: `{"code":"invalidArgument","message":"invalid request"}`.
- **Unknown route: the server drops the message without replying** (`XPCServer.swift:205`, no `else`). libxpc turns the destroyed reply port into an immediate (0.22 ms) `XPC_ERROR_CONNECTION_INTERRUPTED` on the sync send; the connection and apiserver (pid 89583, never exited) stay healthy. A client must treat "interrupted" as "unknown route / no reply", not as a crash.

## Caveats and environment notes

- The CLI-made reference container `xpcprobe-ref` disappeared **twice** without my probe ever sending a delete for that id (its inspect JSON is saved in `ref-inspect.json`). The disappearances coincided with a concurrent e2e session on this machine (`e2e-flap-*`, `e2e-dbg-*`, extra `cider-dns-bridge-*`, and a `testcontainers-ryuk` container, all going through the long-running `cider serve` 0.1.4, pid 2361). No log line attributes the deletions (the unified log and `~/.cider` contain no mention of `xpcprobe-ref`), so this is an observation, not a diagnosis — worth checking whether that session's cleanup removes stopped containers it does not own.
- Cleanup verified: final `container ls -a` shows zero `xpcprobe-*` containers; only pre-existing/e2e containers remain, untouched.
- `xpc_connection_get_pid` returns 0 until the first message (as the Swift comment says); afterwards 89583.

## Files (left in place)

- `/private/tmp/claude-501/-Users-michael-local-cider/e4a65df1-d1d7-4a51-8638-c7835b21a6d6/scratchpad/xpc-probe/XpcProbe/Xpc.cs` — P/Invoke, block literal, `ApiServerClient` (send + error translation)
- `/private/tmp/claude-501/-Users-michael-local-cider/e4a65df1-d1d7-4a51-8638-c7835b21a6d6/scratchpad/xpc-probe/XpcProbe/Program.cs` — probe modes `ping|list|create|experiments|cli|all`, config builders, timing
- `/private/tmp/claude-501/-Users-michael-local-cider/e4a65df1-d1d7-4a51-8638-c7835b21a6d6/scratchpad/xpc-probe/XpcProbe/XpcProbe.csproj` (net10.0, `AllowUnsafeBlocks`)
- Raw outputs: `.../xpc-probe/out-list.txt`, `out-create.txt`, `out-experiments.txt`, `out-cli.txt`; reference config: `.../xpc-probe/ref-inspect.json`
- Run: `cd .../xpc-probe/XpcProbe && dotnet build -c Release && dotnet bin/Release/net10.0/XpcProbe.dll all` (`XPCPROBE_VERBOSE=1` prints connection events).