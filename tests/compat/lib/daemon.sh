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

# start_daemon: build if needed, launch `dotnet run ... serve` in the
# background against an isolated socket + data dir, wait for /_ping.
start_daemon() {
  mkdir -p "$CIDER_COMPAT_DOCKER_CONFIG"

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

  _cider_log "Starting daemon: socket=$CIDER_COMPAT_SOCKET data-dir=$CIDER_COMPAT_DATA_DIR framework=$CIDER_COMPAT_FRAMEWORK"
  (
    cd "$CIDER_REPO_ROOT" || exit 1
    nohup dotnet run --project src/Cider.Daemon -c Release --no-build \
      --framework "$CIDER_COMPAT_FRAMEWORK" -- serve --socket "$CIDER_COMPAT_SOCKET" --data-dir "$CIDER_COMPAT_DATA_DIR" \
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
  return 1
}

# stop_daemon: kill the dotnet wrapper *and* its child `cider` process
# (dotnet run does not reliably forward signals to the app process it
# launches), wait briefly for graceful shutdown (which unlinks the socket),
# then force-kill and unlink defensively.
stop_daemon() {
  local wrapper_pid="" child_pid=""
  [[ -f "$CIDER_COMPAT_PID_FILE" ]] && wrapper_pid="$(cat "$CIDER_COMPAT_PID_FILE" 2>/dev/null || true)"

  if [[ -n "$wrapper_pid" ]]; then
    child_pid="$(pgrep -P "$wrapper_pid" 2>/dev/null | head -n1 || true)"
  fi

  _cider_log "stopping daemon (wrapper pid ${wrapper_pid:-?}, child pid ${child_pid:-?})"

  # Send SIGTERM to the app process first so it can unlink the socket / exit
  # cleanly, then the wrapper.
  [[ -n "$child_pid" ]] && kill -TERM "$child_pid" 2>/dev/null || true
  [[ -n "$wrapper_pid" ]] && kill -TERM "$wrapper_pid" 2>/dev/null || true

  for _ in $(seq 1 10); do
    if [[ -n "$child_pid" ]] && kill -0 "$child_pid" 2>/dev/null; then
      sleep 1
    else
      break
    fi
  done

  [[ -n "$child_pid" ]] && kill -9 "$child_pid" 2>/dev/null || true
  [[ -n "$wrapper_pid" ]] && kill -9 "$wrapper_pid" 2>/dev/null || true

  # Idempotent belt-and-suspenders: catch any stray process bound to our
  # socket path even if the pid file was stale or missing.
  pkill -f "cider serve --socket ${CIDER_COMPAT_SOCKET}" 2>/dev/null || true
  pkill -f "Cider.Daemon.*--socket ${CIDER_COMPAT_SOCKET}" 2>/dev/null || true

  rm -f "$CIDER_COMPAT_PID_FILE"
  rm -f "$CIDER_COMPAT_SOCKET"
}

# wait_for_ping: re-check liveness mid-run (used by scripts that expect a
# long-lived daemon and want to fail fast with a clear message if it died).
wait_for_ping() {
  curl -fsS --max-time 2 --unix-socket "$CIDER_COMPAT_SOCKET" http://localhost/_ping >/dev/null 2>&1
}

# curl_ad: convenience wrapper — `curl_ad -s /containers/json?all=1`
curl_ad() {
  curl --unix-socket "$CIDER_COMPAT_SOCKET" "$@"
}
