#!/bin/bash
# vendor-buildkit-protos.sh -- vendors the upstream .proto files Cider.Daemon compiles with
# Grpc.Tools to decode and rewrite BuildKit's gRPC control-plane and file-sync protocols.
#
# The proxy needs message types for `moby.buildkit.v1.Control/Solve` (exporter swap),
# `ListWorkers` (label strip), and `moby.filesync.v1.FileSend/DiffCopy` framing. Everything else
# BuildKit speaks over `/grpc` and `/session` (gateway, auth, secrets, ssh, upload, health) is
# forwarded byte-for-byte without decoding -- see src/Cider.Daemon/BuildKit/BuildKitMethods.cs --
# and deliberately NOT vendored here.
#
# Refs are pinned so this script is idempotent: re-running it after a clean checkout produces no
# git diff. BUILDKIT_REF and FSUTIL_REF are the versions Apple's `container` embeds (see
# src/Cider.Daemon/BuildKit/Protos/README.md for how those were determined); VTPROTOBUF_REF and
# GOOGLEAPIS_REF pin a specific commit on each repo's default branch, since neither upstream
# publishes tags for the single file we need from it.
#
# Usage: scripts/vendor-buildkit-protos.sh

set -euo pipefail

BUILDKIT_REF=v0.26.2
FSUTIL_REF=586307ad452f
VTPROTOBUF_REF=8ae5a48058dfef04b459e898e94ec5bc159b13c6
GOOGLEAPIS_REF=2e9c5681901a2eebf7f547f0b60c895b1732415e

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROTOS_DIR="$ROOT/protos"

fetch() {
  local url="$1"
  local dest="$PROTOS_DIR/$2"
  mkdir -p "$(dirname "$dest")"
  curl -fsSL "$url" -o "$dest"
}

# moby/buildkit: Control service, ListWorkers types, LLB op definitions, source policy, and
# the filesync BytesMessage framing. control.proto imports worker.proto, ops.proto, policy.proto,
# google/protobuf/timestamp.proto (well-known, resolved by Grpc.Tools) and google/rpc/status.proto.
fetch "https://raw.githubusercontent.com/moby/buildkit/${BUILDKIT_REF}/api/services/control/control.proto" \
  "github.com/moby/buildkit/api/services/control/control.proto"
fetch "https://raw.githubusercontent.com/moby/buildkit/${BUILDKIT_REF}/api/types/worker.proto" \
  "github.com/moby/buildkit/api/types/worker.proto"
fetch "https://raw.githubusercontent.com/moby/buildkit/${BUILDKIT_REF}/solver/pb/ops.proto" \
  "github.com/moby/buildkit/solver/pb/ops.proto"
fetch "https://raw.githubusercontent.com/moby/buildkit/${BUILDKIT_REF}/sourcepolicy/pb/policy.proto" \
  "github.com/moby/buildkit/sourcepolicy/pb/policy.proto"
fetch "https://raw.githubusercontent.com/moby/buildkit/${BUILDKIT_REF}/session/filesync/filesync.proto" \
  "github.com/moby/buildkit/session/filesync/filesync.proto"

# tonistiigi/fsutil: wire framing filesync.proto's Packet depends on.
fetch "https://raw.githubusercontent.com/tonistiigi/fsutil/${FSUTIL_REF}/types/wire.proto" \
  "github.com/tonistiigi/fsutil/types/wire.proto"
fetch "https://raw.githubusercontent.com/tonistiigi/fsutil/${FSUTIL_REF}/types/stat.proto" \
  "github.com/tonistiigi/fsutil/types/stat.proto"

# planetscale/vtprotobuf: the (message/field) options extension wire.proto/stat.proto import.
# Only the single ext.proto file is needed; it lives under an `include/` prefix in the repo but
# is vendored at its Go import path so the `import` statements in wire.proto/stat.proto resolve.
fetch "https://raw.githubusercontent.com/planetscale/vtprotobuf/${VTPROTOBUF_REF}/include/github.com/planetscale/vtprotobuf/vtproto/ext.proto" \
  "github.com/planetscale/vtprotobuf/vtproto/ext.proto"

# googleapis: google.rpc.Status, used by control.proto's error details.
fetch "https://raw.githubusercontent.com/googleapis/googleapis/${GOOGLEAPIS_REF}/google/rpc/status.proto" \
  "google/rpc/status.proto"

# Patch: control.proto declares `message Descriptor` (an OCI content descriptor, used only by the
# build-history messages this project never decodes). Grpc.Tools' C# codegen unconditionally
# generates a `Descriptor` static property on every message class, so a message *named* Descriptor
# collides with its own generated member (CS0542) -- a long-standing, "not planned" upstream
# protoc/C# issue (protocolbuffers/protobuf#12291), not a bug in this vendoring. Renaming is
# wire-compatible (protobuf's wire format encodes field numbers, not type names) and confined to
# this one file: `Descriptor` appears nowhere else in the vendored tree. Applied deterministically
# here, on every run, so the script stays idempotent.
control_proto="$PROTOS_DIR/github.com/moby/buildkit/api/services/control/control.proto"
perl -pi -e 's/\bDescriptor\b/BuildHistoryDescriptor/g' "$control_proto"

echo "Vendored BuildKit protos into $PROTOS_DIR"
