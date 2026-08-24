#!/usr/bin/env bash
# tests/compat/diff-vs-orbstack.sh
#
# Differential check: run an identical scenario against a real dockerd
# (OrbStack, via `docker context orbstack`) and against cider, then
# report fields OrbStack's output has that ours is missing/empty/wrong-typed.
# OrbStack is closed source; it's used here purely as a reference Docker
# Engine implementation, not something we vendor or depend on at runtime.
#
# Output: tests/compat/reports/orbstack-diff.md
#
# This is a differential *report*, not an allowlist-gated suite: the script
# exits non-zero only if it couldn't complete the scenario on either side
# (unreachable engine, container failed to start, etc.), not because fields
# differ — differing fields are the whole point of the report.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Capture the real docker CLI config dir *before* lib/daemon.sh overrides
# DOCKER_CONFIG for isolation. The `orbstack` context is only registered
# under the operator's real config (confirmed empirically: an isolated
# DOCKER_CONFIG cannot resolve `--context orbstack` — "context not found").
# The cider side deliberately keeps the isolated DOCKER_CONFIG so this
# script never touches the operator's real docker CLI state.
REAL_DOCKER_CONFIG="${DOCKER_CONFIG:-$HOME/.docker}"

# shellcheck source=lib/daemon.sh
source "${SCRIPT_DIR}/lib/daemon.sh"

RAW_ORB_DIR=/tmp/cider-compat-orbstack-raw
RAW_CIDER_DIR=/tmp/cider-compat-daemon-raw
REPORT="${SCRIPT_DIR}/reports/orbstack-diff.md"
NORMALIZE_PY="${SCRIPT_DIR}/lib/normalize_diff.py"

ORB_CONTAINER=compat-diff
ORB_VOLUME=compat-v

# orb_docker: talk to the real OrbStack dockerd via its context, using the
# operator's real (non-isolated) docker CLI config so context lookup works.
orb_docker() {
  DOCKER_CONFIG="$REAL_DOCKER_CONFIG" docker --context orbstack "$@"
}

# cider_docker: talk to our daemon. DOCKER_HOST/DOCKER_CONFIG are already
# exported by lib/daemon.sh (isolated).
cider_docker() {
  docker "$@"
}

cleanup() {
  local rc=$?
  echo "[diff-vs-orbstack] cleaning up…" >&2
  orb_docker rm -f "$ORB_CONTAINER" >/dev/null 2>&1 || true
  orb_docker volume rm "$ORB_VOLUME" >/dev/null 2>&1 || true
  cider_docker rm -f "$ORB_CONTAINER" >/dev/null 2>&1 || true
  cider_docker volume rm "$ORB_VOLUME" >/dev/null 2>&1 || true
  stop_daemon
  exit $rc
}
trap cleanup EXIT

mkdir -p "$RAW_ORB_DIR" "$RAW_CIDER_DIR" "${SCRIPT_DIR}/reports"
rm -f "$RAW_ORB_DIR"/*.json "$RAW_CIDER_DIR"/*.json 2>/dev/null || true

# ---------------------------------------------------------------------------
# 1. Verify OrbStack is reachable before doing anything else.
# ---------------------------------------------------------------------------
if ! orb_docker version >/dev/null 2>&1; then
  echo "[diff-vs-orbstack] cannot reach OrbStack via 'docker --context orbstack' — is OrbStack running?" >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# 2. Start our daemon.
# ---------------------------------------------------------------------------
start_daemon || { echo "[diff-vs-orbstack] daemon failed to start" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 3. Run the identical scenario against both engines.
# ---------------------------------------------------------------------------
run_scenario() {
  local label=$1 dockerfn=$2 outdir=$3

  echo "[diff-vs-orbstack] === $label ===" >&2

  "$dockerfn" pull -q alpine:3.22 >/dev/null 2>&1 || true
  "$dockerfn" rm -f "$ORB_CONTAINER" >/dev/null 2>&1 || true
  "$dockerfn" volume rm "$ORB_VOLUME" >/dev/null 2>&1 || true

  if ! "$dockerfn" run -d --name "$ORB_CONTAINER" -p 0:80 -e A=1 -l x=y alpine:3.22 sleep 300 >/dev/null 2>"$outdir/run.stderr"; then
    echo "[diff-vs-orbstack] $label: 'docker run' failed:" >&2
    cat "$outdir/run.stderr" >&2
    return 1
  fi

  "$dockerfn" inspect "$ORB_CONTAINER" >"$outdir/inspect.json" 2>"$outdir/inspect.stderr" || echo "[diff-vs-orbstack] $label: inspect failed" >&2
  "$dockerfn" version --format '{{json .}}' >"$outdir/version.json" 2>"$outdir/version.stderr" || echo "[diff-vs-orbstack] $label: version failed" >&2
  "$dockerfn" info --format '{{json .}}' >"$outdir/info.json" 2>"$outdir/info.stderr" || echo "[diff-vs-orbstack] $label: info failed" >&2

  # `docker ps --format json` emits NDJSON (one object per line), not a
  # single JSON array — wrap it into an array for uniform handling.
  "$dockerfn" ps -a --format '{{json .}}' 2>"$outdir/ps.stderr" | python3 -c '
import json, sys
items = [json.loads(l) for l in sys.stdin if l.strip()]
json.dump(items, sys.stdout)
' >"$outdir/ps.json" || echo "[diff-vs-orbstack] $label: ps failed" >&2

  "$dockerfn" network inspect bridge >"$outdir/network-bridge.json" 2>"$outdir/network-bridge.stderr" || echo "[diff-vs-orbstack] $label: network inspect failed" >&2

  "$dockerfn" volume create "$ORB_VOLUME" >/dev/null 2>"$outdir/volume-create.stderr" || echo "[diff-vs-orbstack] $label: volume create failed" >&2
  "$dockerfn" volume inspect "$ORB_VOLUME" >"$outdir/volume-inspect.json" 2>"$outdir/volume-inspect.stderr" || echo "[diff-vs-orbstack] $label: volume inspect failed" >&2

  "$dockerfn" rm -f "$ORB_CONTAINER" >/dev/null 2>&1 || true
  "$dockerfn" volume rm "$ORB_VOLUME" >/dev/null 2>&1 || true
}

run_scenario "OrbStack" orb_docker "$RAW_ORB_DIR" || exit 1
run_scenario "cider" cider_docker "$RAW_CIDER_DIR" || exit 1

# ---------------------------------------------------------------------------
# 4. Normalize + diff, write the report.
# ---------------------------------------------------------------------------
python3 "$NORMALIZE_PY" "$RAW_ORB_DIR" "$RAW_CIDER_DIR" "$REPORT"
status=$?

echo "[diff-vs-orbstack] report written to $REPORT" >&2
exit $status
