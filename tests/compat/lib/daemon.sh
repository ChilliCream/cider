#!/usr/bin/env bash
# tests/compat/lib/daemon.sh
#
# Shared helpers for the compatibility harness: build/start/stop cider
# against an isolated unix socket + data dir, and isolate the `docker` CLI's
# config dir so compat runs never touch the operator's real ~/.docker or
# ~/.cider state.
#
# Usage (from any tests/compat/run-*.sh script):
#   source "$(dirname "${BASH_SOURCE[0]}")/lib/daemon.sh"
#   start_daemon || exit 1
#   trap stop_daemon EXIT
#   ... run tests against $DOCKER_HOST ...
#
# Env overrides (all optional):
#   CIDER_COMPAT_SOCKET          default /tmp/cider-compat.sock
#   CIDER_COMPAT_DATA_DIR        default /tmp/cider-compat-data
#   CIDER_COMPAT_DOCKER_CONFIG   default /tmp/cider-compat-dockercfg
#   CIDER_COMPAT_FRAMEWORK       default net11.0 (set to net10.0 on machines without the .NET 11 preview SDK)
#   CIDER_COMPAT_PING_TIMEOUT    seconds to wait for /_ping, default 60
#   CIDER_COMPAT_DAEMON_LOG      default /tmp/cider-compat-daemon.log
#   CIDER_COMPAT_PID_FILE        default /tmp/cider-compat-daemon.pid
#   CIDER_COMPAT_SKIP_BUILD=1    skip `dotnet build` (assume bin/ is already current)
#
# This file is meant to be *sourced*, not executed. It intentionally does not
# `set -e`: callers own their own error handling, and a sourced `set -e`
# would change the calling script's semantics.

if [[ -n "${CIDER_COMPAT_DAEMON_SH_LOADED:-}" ]]; then
  return 0 2>/dev/null || true
fi
CIDER_COMPAT_DAEMON_SH_LOADED=1

# Resolve the repo root (tests/compat/lib/daemon.sh -> repo root is ../../..)
CIDER_COMPAT_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CIDER_COMPAT_DIR="$(cd "${CIDER_COMPAT_LIB_DIR}/.." && pwd)"
CIDER_REPO_ROOT="$(cd "${CIDER_COMPAT_DIR}/../.." && pwd)"

CIDER_COMPAT_SOCKET="${CIDER_COMPAT_SOCKET:-/tmp/cider-compat.sock}"
CIDER_COMPAT_DATA_DIR="${CIDER_COMPAT_DATA_DIR:-/tmp/cider-compat-data}"
CIDER_COMPAT_DOCKER_CONFIG="${CIDER_COMPAT_DOCKER_CONFIG:-/tmp/cider-compat-dockercfg}"
CIDER_COMPAT_FRAMEWORK="${CIDER_COMPAT_FRAMEWORK:-net11.0}"
CIDER_COMPAT_PING_TIMEOUT="${CIDER_COMPAT_PING_TIMEOUT:-60}"
CIDER_COMPAT_DAEMON_LOG="${CIDER_COMPAT_DAEMON_LOG:-/tmp/cider-compat-daemon.log}"
CIDER_COMPAT_PID_FILE="${CIDER_COMPAT_PID_FILE:-/tmp/cider-compat-daemon.pid}"

export DOCKER_HOST="unix://${CIDER_COMPAT_SOCKET}"
export DOCKER_CONFIG="${CIDER_COMPAT_DOCKER_CONFIG}"

_cider_log() { echo "[daemon.sh] $*" >&2; }

# build_daemon: `dotnet build src/Cider.Daemon -c Release`. Skipped when
# CIDER_COMPAT_SKIP_BUILD=1 (useful when iterating and bin/ is already fresh).
build_daemon() {
  if [[ "${CIDER_COMPAT_SKIP_BUILD:-0}" == "1" ]]; then
    _cider_log "CIDER_COMPAT_SKIP_BUILD=1, skipping dotnet build"
    return 0
  fi
  _cider_log "Building Cider.Daemon (Release)…"
  if ! ( cd "$CIDER_REPO_ROOT" && dotnet build src/Cider.Daemon -c Release ) >>"$CIDER_COMPAT_DAEMON_LOG" 2>&1; then
    _cider_log "dotnet build failed; see $CIDER_COMPAT_DAEMON_LOG"
    tail -n 60 "$CIDER_COMPAT_DAEMON_LOG" >&2 || true
    return 1
  fi
}

# start_daemon: build if needed, launch the built `cider serve` binary
# directly in the background against an isolated socket + data dir, wait
# for /_ping.
start_daemon() {
  mkdir -p "$CIDER_COMPAT_DOCKER_CONFIG"

  # A leftover PID file from a previous run must never survive into this
  # run's stop_daemon: PIDs are recycled by the OS, so a stale file could
  # cause cleanup to signal an unrelated process (including, worst case, the
  # operator's real installed daemon if it happened to reuse that PID).
  rm -f "$CIDER_COMPAT_PID_FILE"

  # `docker compose` (and buildx, etc.) are CLI *plugins*, discovered under
  # $DOCKER_CONFIG/cli-plugins -- which we just pointed at an isolated,
  # empty directory for config isolation. Without this, `docker compose ...`
  # fails with "docker: unknown command: docker compose" the moment
  # DOCKER_CONFIG is isolated (confirmed empirically). Symlink in the real
  # cli-plugins dir (read-only reuse of the already-installed plugin
  # binaries; does not touch config.json/contexts/credentials, so isolation
  # for everything else -- and future scripts that source this file for
  # docker CLI isolation -- is preserved) so plugin subcommands resolve.
  local real_docker_config="${CIDER_COMPAT_REAL_DOCKER_CONFIG:-$HOME/.docker}"
  if [[ -d "$real_docker_config/cli-plugins" && ! -e "$CIDER_COMPAT_DOCKER_CONFIG/cli-plugins" ]]; then
    ln -s "$real_docker_config/cli-plugins" "$CIDER_COMPAT_DOCKER_CONFIG/cli-plugins"
  fi

  rm -f "$CIDER_COMPAT_SOCKET"
  rm -rf "$CIDER_COMPAT_DATA_DIR"
  mkdir -p "$CIDER_COMPAT_DATA_DIR"
  : >"$CIDER_COMPAT_DAEMON_LOG"

  build_daemon || return 1

  # Launch the built apphost binary directly rather than through `dotnet
  # run`: `dotnet run` execs a wrapper process whose $! is *not* the actual
  # `cider serve` PID, which is what previously forced stop_daemon to go
  # hunting for the real process via pgrep/pkill pattern matching (risking a
  # false match against an unrelated cider process, e.g. the operator's real
  # installed daemon). Running the binary itself means $! *is* the daemon's
  # PID, so cleanup can kill exactly the process this function started.
  local daemon_bin="$CIDER_REPO_ROOT/src/Cider.Daemon/bin/Release/$CIDER_COMPAT_FRAMEWORK/cider"
  if [[ ! -x "$daemon_bin" ]]; then
    _cider_log "daemon binary not found at $daemon_bin (build failed?)"
    return 1
  fi

  _cider_log "Starting daemon: socket=$CIDER_COMPAT_SOCKET data-dir=$CIDER_COMPAT_DATA_DIR framework=$CIDER_COMPAT_FRAMEWORK"
  (
    cd "$CIDER_REPO_ROOT" || exit 1
    nohup "$daemon_bin" serve --socket "$CIDER_COMPAT_SOCKET" --data-dir "$CIDER_COMPAT_DATA_DIR" \
      >>"$CIDER_COMPAT_DAEMON_LOG" 2>&1 &
    echo $! >"$CIDER_COMPAT_PID_FILE"
  )

  local waited=0
  while (( waited < CIDER_COMPAT_PING_TIMEOUT )); do
    if [[ -S "$CIDER_COMPAT_SOCKET" ]] && curl -fsS --max-time 2 --unix-socket "$CIDER_COMPAT_SOCKET" http://localhost/_ping >/dev/null 2>&1; then
      _cider_log "daemon is up (waited ${waited}s)"
      return 0
    fi
    sleep 1
    ((waited++))
  done

  _cider_log "daemon did not answer /_ping within ${CIDER_COMPAT_PING_TIMEOUT}s; log tail:"
  tail -n 60 "$CIDER_COMPAT_DAEMON_LOG" >&2 || true
  # Callers install `trap stop_daemon EXIT` only after start_daemon returns
  # success, so on this failure path nothing else will reap the daemon we
  # just launched -- stop it ourselves before returning.
  stop_daemon
  return 1
}

# cleanup_forwarders: cider-0o3 -- release this run's own CoreDNS forwarder
# VM(s), the compat-side mirror of DaemonFixture.CleanupForwarderAsync on the
# E2E side. A forwarder's engine id is <network>-<hash>, where <hash> is the
# first 8 hex chars of SHA-256(data dir) (DnsForwarderService.ForwarderName /
# DataDirHash), so this run's own hash identifies exactly the forwarder(s)
# its own daemon created -- never a still-live daemon's, including another
# concurrent compat/E2E run's (cider-24v: never remove what this run did not
# create). Without this, every forwarder this run's daemon ever started
# stays behind as a running VM the new reaper can never reap: it carries
# DataDirPathLabel pointing at $CIDER_COMPAT_DATA_DIR, and as long as that
# directory still exists on disk (nothing here removed it -- see the
# `rm -rf "$CIDER_COMPAT_DATA_DIR"` below) it reads as "live" forever.
#
# Fail-safe like snapshot_images/cleanup_new_images above: a failed `container
# ls` listing is never treated as "nothing to clean" vs. "everything to
# clean" -- it just leaves whatever forwarders exist in place rather than
# guessing.
cleanup_forwarders() {
  command -v container >/dev/null 2>&1 || return 0
  command -v jq >/dev/null 2>&1 || {
    _cider_log "cleanup_forwarders: jq not found on PATH; leaving any forwarders in place"
    return 0
  }

  local hash
  hash="$(printf '%s' "$CIDER_COMPAT_DATA_DIR" | shasum -a 256 | cut -c1-8)"

  local listing
  if ! listing="$(container ls -a --format json 2>/dev/null)"; then
    _cider_log "cleanup_forwarders: 'container ls -a' failed; leaving any forwarders in place rather than guessing"
    return 0
  fi
  [[ -z "$listing" ]] && return 0

  local names
  names="$(printf '%s' "$listing" | jq -r --arg suffix "-$hash" \
    '.[] | (.configuration.id // .id) | select(type == "string" and endswith($suffix))' 2>/dev/null)" || {
    _cider_log "cleanup_forwarders: could not parse 'container ls -a' output; leaving any forwarders in place"
    return 0
  }
  [[ -z "$names" ]] && return 0

  local name
  while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    _cider_log "releasing this run's DNS forwarder $name"
    container stop "$name" >/dev/null 2>&1 || true
    container delete -f "$name" >/dev/null 2>&1 || true
  done <<<"$names"
}

# stop_daemon: kill exactly the PID start_daemon captured via $! at launch
# (nothing else -- no pgrep/pkill pattern matching, which risks matching an
# unrelated cider process such as the operator's real installed daemon),
# wait briefly for graceful shutdown (which unlinks the socket), then
# force-kill and unlink defensively. Also releases this run's own DNS
# forwarder VM(s) (see cleanup_forwarders above) and removes the isolated
# data dir -- it is already rm -rf'd at the top of start_daemon, so nothing
# depends on it surviving past this point, and removing it here means any
# forwarder cleanup_forwarders happened to miss becomes reapable by
# IsOrphanedForwarder's Directory.Exists check on the next daemon start
# instead of being pinned live forever.
stop_daemon() {
  local pid=""
  [[ -f "$CIDER_COMPAT_PID_FILE" ]] && pid="$(cat "$CIDER_COMPAT_PID_FILE" 2>/dev/null || true)"

  _cider_log "stopping daemon (pid ${pid:-?})"

  if [[ -n "$pid" ]]; then
    kill -TERM "$pid" 2>/dev/null || true

    for _ in $(seq 1 10); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 1
    done

    kill -9 "$pid" 2>/dev/null || true
  fi

  rm -f "$CIDER_COMPAT_PID_FILE"
  rm -f "$CIDER_COMPAT_SOCKET"

  cleanup_forwarders

  rm -rf "$CIDER_COMPAT_DATA_DIR"
}

# wait_for_ping: re-check liveness mid-run (used by scripts that expect a
# long-lived daemon and want to fail fast with a clear message if it died).
wait_for_ping() {
  curl -fsS --max-time 2 --unix-socket "$CIDER_COMPAT_SOCKET" http://localhost/_ping >/dev/null 2>&1
}

# snapshot_images / cleanup_new_images: cider-0o3 -- every compat run shares
# one Apple content store with the operator's own images and every other
# concurrent run (there is exactly one apiserver per user; see cider-0o3's
# task notes for why that cannot be isolated per run), so images this run
# built, tagged or pulled must not outlive it, same rule DaemonFixture
# applies on the E2E side (cider-24v: never remove what the run did not
# create). By id (--no-trunc), so a multi-tag image is recognised as
# pre-existing, or removed, under every one of its tags at once, and a
# caller that never calls snapshot_images gets a no-op cleanup_new_images
# rather than a guess at what may be safely removed.
#
# Since cider-ede.31 a plain `docker rmi` no longer sweeps the store's blob
# content on the XPC transport (only `docker image prune` does, once per
# call), so this may free the image *records* here without reclaiming the
# disk space their blobs used the way it implicitly did before that fix --
# an explicit store-wide prune from every compat run's teardown would
# itself be a shared-infrastructure sweep racing every other concurrent
# run's in-flight builds on that one store, which this harness must not risk.
#
# Fail-safe (cider-0o3 blocker fix): `docker images` against the daemon this
# script just started -- the least reliable moment for it -- can exit
# non-zero (e.g. one dangling blob reference breaking the listing). This
# file deliberately does not `set -o pipefail` (see header), so a failing
# left-hand side of a pipeline is invisible to `$?` unless checked directly;
# snapshot_images therefore captures the docker command into a temp file and
# checks *its* exit status, never the pipeline's last stage. Teardown must
# never guess at what it may safely remove (DaemonFixture's stance, mirrored
# here): cleanup_new_images only ever runs when the snapshot is known-good.
_CIDER_COMPAT_IMAGES_BEFORE=""
_CIDER_COMPAT_SNAPSHOT_OK=0

# Repo tag prefixes this harness (and the E2E fixture,
# tests/Cider.E2E.Tests/Infrastructure/DaemonFixture.cs's OwnedImageTagPrefixes)
# actually tags things with -- the only tags a new image may be removed
# under. This list must name exactly the same prefixes as that C# array
# (cider-3n2): this harness only ever mints "cider-compat/..." tags itself,
# but the two definitions state one contract and must agree, not drift the
# way they did before cider-3n2. If OwnedImageTagPrefixes changes, update
# this regex to match, and vice versa.
#
# Deliberately does NOT include `cider-build-`: that synthetic untagged-build
# marker (Cider.Core.Ids.SyntheticBuildTag) is stripped by
# ImageManager.VisibleReferences before any image listing is built, so it is
# never visible through `docker images` either -- not to this harness, not to
# the C# fixture, not to a real `docker` client. Such an image always lists
# here as `<none>:<none>` and is excluded by the `:<none>$` check below
# regardless of what this regex says, so keeping `cider-build-` in the list
# only misrepresented what this filter reclaims (cider-3n2).
#
# An id that carries a tag outside this list, or no tag at all (<none>, i.e.
# untagged), is left alone: it may be another concurrent run's in-flight
# build, or a base image (alpine, nginx, ryuk, ...) newly pulled into the
# shared cache, which stays by design -- re-pulling it is the cost this
# filter buys. The leak this task actually measures (this harness's own
# synthetic tags) is still cleaned.
_CIDER_COMPAT_OWNED_TAG_RE='^(e2e/|e2e-|cider-e2e|cider-compat)'

snapshot_images() {
  local tmp
  tmp="$(mktemp)"
  if ! docker images -aq --no-trunc >"$tmp" 2>/dev/null; then
    _cider_log "snapshot_images: 'docker images' failed; refusing to enable image cleanup for this run"
    rm -f "$tmp"
    _CIDER_COMPAT_IMAGES_BEFORE=""
    _CIDER_COMPAT_SNAPSHOT_OK=0
    return 1
  fi

  sort -u -o "$tmp" "$tmp"
  _CIDER_COMPAT_IMAGES_BEFORE="$tmp"
  _CIDER_COMPAT_SNAPSHOT_OK=1
}

cleanup_new_images() {
  if [[ "$_CIDER_COMPAT_SNAPSHOT_OK" != "1" || -z "$_CIDER_COMPAT_IMAGES_BEFORE" || ! -f "$_CIDER_COMPAT_IMAGES_BEFORE" ]]; then
    return 0
  fi

  local after new_ids
  after="$(mktemp)"
  if ! docker images -aq --no-trunc >"$after" 2>/dev/null; then
    _cider_log "cleanup_new_images: teardown-side 'docker images' failed; skipping image cleanup rather than guessing"
    rm -f "$_CIDER_COMPAT_IMAGES_BEFORE" "$after"
    _CIDER_COMPAT_IMAGES_BEFORE=""
    _CIDER_COMPAT_SNAPSHOT_OK=0
    return 0
  fi
  sort -u -o "$after" "$after"

  # Belt-and-braces: an empty pre-run snapshot with a non-empty after-list
  # would otherwise diff as "every current image is new". A genuinely empty
  # store at snapshot time is possible but vanishingly unlikely on a
  # developer machine; treat it as a signal something upstream is off and
  # refuse to delete rather than guess.
  if [[ ! -s "$_CIDER_COMPAT_IMAGES_BEFORE" && -s "$after" ]]; then
    _cider_log "cleanup_new_images: pre-run snapshot is empty but the store is not; refusing to delete anything"
    rm -f "$_CIDER_COMPAT_IMAGES_BEFORE" "$after"
    _CIDER_COMPAT_IMAGES_BEFORE=""
    _CIDER_COMPAT_SNAPSHOT_OK=0
    return 0
  fi

  new_ids="$(comm -13 "$_CIDER_COMPAT_IMAGES_BEFORE" "$after")"
  if [[ -n "$new_ids" ]]; then
    # Ownership filter: only remove a new id all of whose repo:tag entries
    # are test-owned (see _CIDER_COMPAT_OWNED_TAG_RE above); an id that is
    # untagged or carries any other tag may belong to another concurrent
    # run or be a shared base image, so it is left in the store. The
    # candidate id list is passed to awk as a *file* (not a `-v` string):
    # macOS's system awk (onetrueawk) mishandles a `-v` value containing
    # embedded newlines, which a multi-id new_ids always has.
    local ids_file owned_ids
    ids_file="$(mktemp)"
    printf '%s\n' "$new_ids" >"$ids_file"
    owned_ids="$(
      docker images -a --no-trunc --format '{{.ID}} {{.Repository}}:{{.Tag}}' 2>/dev/null \
        | awk -v idsfile="$ids_file" -v re="$_CIDER_COMPAT_OWNED_TAG_RE" '
            BEGIN {
              while ((getline line < idsfile) > 0) if (line != "") want[line] = 1
              close(idsfile)
            }
            ($1 in want) {
              tagged[$1] = 1
              if ($2 ~ /:<none>$/ || $2 !~ re) { unowned[$1] = 1 }
            }
            END {
              for (id in want) {
                if (id in tagged && !(id in unowned)) print id
              }
            }
          '
    )"
    rm -f "$ids_file"

    if [[ -n "$owned_ids" ]]; then
      # shellcheck disable=SC2086  # word-splitting is the point: one id per rmi argument
      docker rmi -f $owned_ids >/dev/null 2>&1 || true
    fi
  fi

  rm -f "$_CIDER_COMPAT_IMAGES_BEFORE" "$after"
  _CIDER_COMPAT_IMAGES_BEFORE=""
  _CIDER_COMPAT_SNAPSHOT_OK=0
}

# curl_ad: convenience wrapper — `curl_ad -s /containers/json?all=1`
curl_ad() {
  curl --unix-socket "$CIDER_COMPAT_SOCKET" "$@"
}
