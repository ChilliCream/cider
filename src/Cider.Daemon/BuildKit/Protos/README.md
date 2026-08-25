# Vendored BuildKit protos

The `.proto` files Cider.Daemon compiles with `Grpc.Tools` live under `protos/` at the repo root
(not next to this file) so their paths match the Go import roots BuildKit's own `.proto` files
`import` by — `github.com/moby/buildkit/...`, `github.com/tonistiigi/fsutil/...`,
`github.com/planetscale/vtprotobuf/...`, `google/rpc/...`. This file documents where those pins
came from and how to refresh them.

## Regenerating

```
scripts/vendor-buildkit-protos.sh
```

Re-downloads every vendored file from the pinned refs below into `protos/`, then applies one
deterministic, idempotent post-fetch patch (see "Local patches" below). It is idempotent:
re-running it against an unmodified checkout produces no `git diff`. `dotnet build` then
regenerates the C# message/client/service classes from `protos/**/*.proto` via the `<Protobuf>`
items in `Cider.Daemon.csproj` — nothing under `protos/` is C# and nothing generated is committed.

## Pins

| Ref | Value | Why |
| --- | --- | --- |
| `BUILDKIT_REF` | `v0.26.2` | The version Apple's builder VM embeds: `container builder start` then `container exec -i buildkit buildctl --version` on Apple `container` reports `github.com/moby/buildkit v0.26.2`. |
| `FSUTIL_REF` | `586307ad452f` | buildkit `v0.26.2`'s `go.mod` pins `github.com/tonistiigi/fsutil v0.0.0-20250605211040-586307ad452f`; the twelve hex characters after the timestamp are the module's pseudo-version commit. |
| `VTPROTOBUF_REF` | a commit on `planetscale/vtprotobuf`'s default branch | That repo tags no releases; `wire.proto`/`stat.proto` only need the one extension-options file (`vtproto/ext.proto`), so a specific commit is pinned for reproducibility rather than tracking `main`. |
| `GOOGLEAPIS_REF` | a commit on `googleapis/googleapis`'s default branch | Same reasoning, for `google/rpc/status.proto`. |

Bumping `BUILDKIT_REF`/`FSUTIL_REF` to track a newer Apple builder: update both constants at the
top of `scripts/vendor-buildkit-protos.sh`, re-run it, re-run
`dotnet build Cider.sln -p:CiderTargetFrameworks=net10.0`, and diff the regenerated message shapes
against the field/method facts recorded below (BuildKit is proto3, so old fields don't get
renumbered, but new ones can appear). Expect `protos/` to diverge from upstream only in
`control.proto` (see "Local patches" below); re-check whether upstream has fixed the
`Descriptor`/CS0542 collision before assuming the patch is still needed.

## Local patches

`scripts/vendor-buildkit-protos.sh` applies exactly one patch after fetching, deterministically
and idempotently, on every run:

- `control.proto`'s `message Descriptor` — the OCI content descriptor used by
  `BuildHistoryRecord`'s `logs`/`trace`/`externalError` fields and related build-history
  messages — is renamed to `BuildHistoryDescriptor`. protoc's C# generator emits a static
  `Descriptor` property on every message class, so a message literally named `Descriptor`
  collides with its own generated member (`CS0542`); this is a long-standing, unresolved
  upstream issue (protocolbuffers/protobuf#12291), not a bug in this vendoring. The rename is
  wire-compatible — protobuf's wire format encodes field numbers and types, not type names, and
  neither change here — but it does change the fully-qualified proto type name (now
  `moby.buildkit.v1.BuildHistoryDescriptor`), which matters if this type is ever referenced by a
  `google.protobuf.Any` type URL.

Known accepted deviation: `dotnet build` emits one `protoc` warning for `stat.proto`'s unused
import of `vtproto/ext.proto` (the file only needs the extension declarations, not any type from
it). This is authentic upstream content — nothing in this project's MSBuild can suppress a
`protoc` warning — so it is recorded here rather than treated as a regression.

## What is vendored, and what is not

Vendored (message and, for two files, service code is generated):

- `github.com/moby/buildkit/api/services/control/control.proto` — the `Control` service:
  `Solve`, `Session`, `ListWorkers`, plus `DiskUsage`/`Prune`/`Status`/`Info`/build-history RPCs the
  proxy does not touch. Service code generated (`GrpcServices="Both"`).
- `github.com/moby/buildkit/session/filesync/filesync.proto` — `FileSync` and `FileSend`, and
  `BytesMessage`. Service code generated (`GrpcServices="Both"`).
- `github.com/moby/buildkit/api/types/worker.proto`, `github.com/moby/buildkit/solver/pb/ops.proto`,
  `github.com/moby/buildkit/sourcepolicy/pb/policy.proto` — message types `control.proto` and
  `worker.proto` reference (`ListWorkers`' `WorkerRecord`, `SolveRequest.Definition`,
  `SolveRequest.SourcePolicy`). Messages only (`GrpcServices="None"`).
- `github.com/tonistiigi/fsutil/types/wire.proto`, `github.com/tonistiigi/fsutil/types/stat.proto` —
  `fsutil.types.Packet`, the frame type `FileSync.DiffCopy`/`TarStream` stream. Messages only.
- `github.com/planetscale/vtprotobuf/vtproto/ext.proto` — the proto2 `(message/field)` option
  extensions `wire.proto`/`stat.proto` declare against (`mempool`, `unique`, ...); needed only so
  those two files parse, nothing in our code references its types. Messages only.
- `google/rpc/status.proto` — `google.rpc.Status`, used by `control.proto`'s error detail fields.
  Messages only. (`google/protobuf/timestamp.proto`, `control.proto`'s other well-known import, is
  not vendored — Grpc.Tools resolves it from its own bundled well-known-types.)

Deliberately NOT vendored — the proxy forwards these byte-for-byte without decoding, so only their
wire method paths matter (recorded as constants in `../BuildKitMethods.cs`, not as generated
types):

- `frontend/gateway/pb/gateway.proto` (`moby.buildkit.v1.frontend.LLBBridge`) — every gateway RPC a
  frontend build issues back over the attached session.
- `session/auth/auth.proto` (`moby.filesync.v1.Auth`), `session/secrets/secrets.proto`
  (`moby.buildkit.secrets.v1.Secrets`), `session/sshforward/ssh.proto` (`moby.sshforward.v1.SSH`),
  `session/upload/upload.proto` (`moby.upload.v1.Upload`).
- `grpc.health.v1.Health/Check` — answered directly by the `Grpc.HealthCheck` NuGet package
  (`Grpc.Health.V1` + `HealthServiceImpl`), which ships its own generated types; `health.proto`
  itself is not vendored.

Never hand-edit anything Grpc.Tools generates from these files — regenerate via the script and
`dotnet build` instead.

## Facts worth knowing without re-reading the upstream `.proto` (control.proto, buildkit `v0.26.2`)

- `SolveRequest` (fields 1-15): `Ref=1`, `Definition=2` (`pb.Definition`),
  `ExporterDeprecated=3`, `ExporterAttrsDeprecated=4`, `Session=5`, `Frontend=6`,
  `FrontendAttrs=7`, `Cache=8` (`CacheOptions`), `Entitlements=9`, `FrontendInputs=10`,
  `Internal=11`, `SourcePolicy=12`, `Exporters=13` (`repeated Exporter`), `EnableSessionExporter=14`,
  `SourcePolicySession=15`. `Exporter{Type=1 (string), Attrs=2 (map<string,string>)}`. The moby
  exporter swap this project exists for reads/writes `Exporters[].Type` (and, for the deprecated
  single-exporter path, `ExporterDeprecated`/`ExporterAttrsDeprecated`).
- `SolveResponse{ExporterResponse=1}` — a `map<string,string>` of the exporter's result digests.
- `BytesMessage{data=1 (bytes)}` — the frame type for `Control.Session` (bidi) and
  `FileSend.DiffCopy` (bidi stream).
- `service FileSend { rpc DiffCopy(stream BytesMessage) returns (stream BytesMessage); }`.
