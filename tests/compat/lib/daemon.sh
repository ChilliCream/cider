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

# stop_daemon: kill exactly the PID start_daemon captured via $! at launch
# (nothing else -- no pgrep/pkill pattern matching, which risks matching an
# unrelated cider process such as the operator's real installed daemon),
# wait briefly for graceful shutdown (which unlinks the socket), then
# force-kill and unlink defensively.
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
_CIDER_COMPAT_IMAGES_BEFORE=""

snapshot_images() {
  _CIDER_COMPAT_IMAGES_BEFORE="$(mktemp)"
  docker images -aq --no-trunc 2>/dev/null | sort -u >"$_CIDER_COMPAT_IMAGES_BEFORE"
}

cleanup_new_images() {
  if [[ -z "$_CIDER_COMPAT_IMAGES_BEFORE" || ! -f "$_CIDER_COMPAT_IMAGES_BEFORE" ]]; then
    return 0
  fi

  local after new_ids
  after="$(mktemp)"
  docker images -aq --no-trunc 2>/dev/null | sort -u >"$after"
  new_ids="$(comm -13 "$_CIDER_COMPAT_IMAGES_BEFORE" "$after")"
  if [[ -n "$new_ids" ]]; then
    # shellcheck disable=SC2086  # word-splitting is the point: one id per rmi argument
    docker rmi -f $new_ids >/dev/null 2>&1 || true
  fi

  rm -f "$_CIDER_COMPAT_IMAGES_BEFORE" "$after"
  _CIDER_COMPAT_IMAGES_BEFORE=""
}

# curl_ad: convenience wrapper — `curl_ad -s /containers/json?all=1`
curl_ad() {
  curl --unix-socket "$CIDER_COMPAT_SOCKET" "$@"
}
