#!/usr/bin/env bash
# install-local.sh -- one command to build, publish and install cider from this checkout.
# Default flow, in order:
#
#   1. Preconditions: Apple silicon, .NET 11 preview SDK, Apple `container` CLI.
#   2. Native AOT publish to the STABLE path ~/.cider/bin/cider (never a repo-relative path --
#      the launchd agent this writes embeds the executable path, so it must survive rebuilds and
#      the repo itself being moved or deleted).
#   3. `cider install` -- writes the launchd agent and creates the `cider` docker context.
#   4. Make cider the DEFAULT socket: `docker context use cider`, and repoint the root-owned
#      /var/run/docker.sock at cider's socket via INTERACTIVE sudo, after saving whatever it
#      pointed at before into ~/.cider/system-socket.backup.json (same format
#      src/Cider.Daemon/Install/SystemSocketLink.cs writes/reads, so `cider uninstall` -- and this
#      script's own --uninstall -- can put it back). Opt out with --no-default-socket.
#   5. Verify: socket answers, `cider status`, a real `docker run` through the cider context, and
#      -- unless --no-default-socket -- a plain `docker run` with no DOCKER_CONTEXT set.
#
# Re-running is idempotent: it republishes over the same binary, `cider install` re-bootstraps the
# existing launchd agent, and stage 4 is a no-op once /var/run/docker.sock already points at us.
#
# --uninstall reverses all of it: `cider uninstall` (unloads the agent, drops the docker context,
# and restores the system socket from the backup file, never `rm -f`), switches the docker context
# back to `orbstack`, and removes the published binary.
#
# Usage:
#   scripts/install-local.sh [--no-default-socket] [--no-daemon] [--yes] [--force] [--dry-run]
#   scripts/install-local.sh --uninstall [--yes] [--dry-run]
#   scripts/install-local.sh --help
#
# Flags:
#   --no-default-socket  Stop after `cider install` (stage 3). Does not touch
#                         /var/run/docker.sock or the active docker context.
#   --no-daemon           Publish the binary only; skip `cider install` and everything after it.
#   --force                Allow repointing /var/run/docker.sock even when it is a real socket
#                         file rather than a symlink (it cannot be restored afterwards -- see
#                         `cider install --help`'s --force-system-socket).
#   --yes, -y               Skip the confirmation before the sudo step that repoints
#                         /var/run/docker.sock. Everything else never prompts.
#   --dry-run             Print every command this script would run, including the sudo ones,
#                         without running any of them. Nothing is built, installed or changed.
#   --uninstall            Reverse the install: `cider uninstall`, restore the previous system
#                         socket target, switch back to the `orbstack` docker context, remove the
#                         published binary.
#   -h, --help             Show this help and exit 0.
#
# Exit codes: 0 ok, 1 a step failed, 2 bad usage/arguments.
#
# bash 3.2 compatible (macOS ships bash 3.2 as /bin/bash); shellcheck clean.
set -euo pipefail

# ---------------------------------------------------------------------------
# cwd-independent: resolve everything from the script's own location, never the caller's pwd.
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Stable install location: NOT under $REPO_ROOT. The launchd plist `cider install` writes embeds
# this exact path (Environment.ProcessPath), so it must survive the repo being rebuilt, moved, or
# deleted entirely.
DATA_DIR="$HOME/.cider"
BIN_DIR="$DATA_DIR/bin"
BIN="$BIN_DIR/cider"
SOCK="$DATA_DIR/docker.sock"
BACKUP_FILE="$DATA_DIR/system-socket.backup.json"
DOCKER_SOCK="/var/run/docker.sock"
CIDER_CONTEXT="cider"
ORBSTACK_CONTEXT="orbstack"

DRY_RUN=false
NO_DAEMON=false
NO_DEFAULT_SOCKET=false
FORCE=false
ASSUME_YES=false
DO_UNINSTALL=false

# ---------------------------------------------------------------------------
# argument parsing
# ---------------------------------------------------------------------------
print_help() {
  # Keep in sync with the flag table in the header comment above.
  sed -n '2,48p' "$SCRIPT_DIR/install-local.sh" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --no-default-socket) NO_DEFAULT_SOCKET=true ;;
    --no-daemon) NO_DAEMON=true ;;
    --force) FORCE=true ;;
    --yes | -y) ASSUME_YES=true ;;
    --dry-run) DRY_RUN=true ;;
    --uninstall) DO_UNINSTALL=true ;;
    -h | --help) print_help; exit 0 ;;
    *)
      echo "install-local.sh: unknown argument '$1'" >&2
      echo "Run 'scripts/install-local.sh --help' for usage." >&2
      exit 2
      ;;
  esac
  shift
done

# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------
info() { printf '==> %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'install-local.sh: %s\n' "$*" >&2; exit 1; }

# Runs (or, in --dry-run, just prints) a command that changes machine state.
run() {
  if [ "$DRY_RUN" = true ]; then
    printf '+ %s\n' "$*"
    return 0
  fi
  printf '+ %s\n' "$*"
  "$@"
}

# Same as run(), but for a command whose failure is not fatal to the overall script (best-effort
# cleanup/reporting steps).
run_best_effort() {
  if [ "$DRY_RUN" = true ]; then
    printf '+ %s\n' "$*"
    return 0
  fi
  printf '+ %s\n' "$*"
  "$@" || warn "'$*' did not succeed (continuing)"
}

confirm() {
  # $1: prompt. Returns 0 (proceed) when --yes/--dry-run, or the user answers y/Y.
  if [ "$ASSUME_YES" = true ] || [ "$DRY_RUN" = true ]; then
    return 0
  fi
  printf '%s [y/N] ' "$1"
  reply=""
  read -r reply || true
  case "$reply" in
    y | Y | yes | YES) return 0 ;;
    *) return 1 ;;
  esac
}

# ---------------------------------------------------------------------------
# preconditions
# ---------------------------------------------------------------------------
preflight() {
  info "checking preconditions"
  local ok=true

  local arch
  arch="$(uname -m)"
  if [ "$arch" != "arm64" ]; then
    warn "this Mac reports '$arch', not 'arm64'; cider needs Apple silicon (Apple \`container\` cannot run on Intel)."
    ok=false
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    warn "no 'dotnet' on PATH. Install the .NET 11 preview SDK: https://dotnet.microsoft.com/download/dotnet/11.0"
    ok=false
  elif ! dotnet --list-sdks 2>/dev/null | grep -Eq '^11\.'; then
    warn "the .NET 11 preview SDK is not installed (only $(dotnet --list-sdks 2>/dev/null | wc -l | tr -d ' ') SDK(s) found)."
    warn "Native AOT publish needs it specifically -- install from https://dotnet.microsoft.com/download/dotnet/11.0"
    ok=false
  fi

  if ! command -v container >/dev/null 2>&1; then
    warn "no 'container' (Apple container) CLI on PATH. Install it from https://github.com/apple/container/releases"
    ok=false
  fi

  if ! command -v docker >/dev/null 2>&1; then
    warn "no 'docker' CLI on PATH; verification and the docker-context steps below will not work."
    ok=false
  fi

  if [ ! -d "$REPO_ROOT/src/Cider.Daemon" ]; then
    die "src/Cider.Daemon not found under $REPO_ROOT -- is this script still inside the cider checkout?"
  fi

  if [ "$ok" != true ]; then
    if [ "$DRY_RUN" = true ]; then
      warn "one or more preconditions are not met (see above); continuing because of --dry-run."
    else
      die "preconditions not met (see warnings above); nothing was built or installed."
    fi
  else
    info "preconditions OK (Apple silicon, .NET 11 SDK, container CLI, docker CLI)"
  fi
}

# ---------------------------------------------------------------------------
# stage 2: native AOT publish to the stable path
# ---------------------------------------------------------------------------
publish() {
  info "publishing cider (Native AOT, osx-arm64) to $BIN"

  if [ "$DRY_RUN" = true ]; then
    printf '+ %s\n' "dotnet publish $REPO_ROOT/src/Cider.Daemon -c Release -r osx-arm64 -f net11.0 --self-contained -p:PublishAot=true -o <tmpdir>"
    printf '+ %s\n' "mv <tmpdir>/cider $BIN"
    return 0
  fi

  local tmp
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/cider-publish.XXXXXX")"
  trap 'rm -rf "$tmp"' RETURN

  run dotnet publish "$REPO_ROOT/src/Cider.Daemon" \
    -c Release -r osx-arm64 -f net11.0 --self-contained -p:PublishAot=true -o "$tmp"

  [ -x "$tmp/cider" ] || die "publish did not produce an executable at $tmp/cider"

  mkdir -p "$BIN_DIR"
  # Publish to a sibling file and rename into place: on the same volume `mv` is an atomic rename,
  # so a launchd agent that execs $BIN never observes a half-written binary.
  cp "$tmp/cider" "$BIN.new"
  chmod 755 "$BIN.new"
  mv -f "$BIN.new" "$BIN"

  info "published: $("$BIN" version 2>&1 | head -1)"
}

# ---------------------------------------------------------------------------
# stage 3: `cider install`
# ---------------------------------------------------------------------------
cider_install() {
  info "running cider install (launchd agent + '$CIDER_CONTEXT' docker context)"
  run "$BIN" install --data-dir "$DATA_DIR" --socket "$SOCK"
}

# ---------------------------------------------------------------------------
# stage 4: make cider the default docker socket
# ---------------------------------------------------------------------------

# Classifies $DOCKER_SOCK: prints one of "absent" "symlink:<target>" "realfile" on stdout.
classify_system_socket() {
  if [ -L "$DOCKER_SOCK" ]; then
    printf 'symlink:%s\n' "$(readlink "$DOCKER_SOCK")"
  elif [ -e "$DOCKER_SOCK" ]; then
    printf 'realfile\n'
  else
    printf 'absent\n'
  fi
}

# Writes <data-dir>/system-socket.backup.json in the exact shape SystemSocketLink.cs reads/writes
# (camelCase: path, existed, wasSymlink, previousTarget, linkedTarget, savedAt), so `cider
# uninstall` -- and this script's own --uninstall -- can restore from it.
write_backup() {
  # $1 existed(true/false) $2 wasSymlink(true/false) $3 previousTarget(may be empty) $4 linkedTarget
  python3 - "$DOCKER_SOCK" "$1" "$2" "$3" "$4" "$BACKUP_FILE" <<'PY'
import datetime
import json
import sys

path, existed, was_symlink, previous, linked, out = sys.argv[1:7]
data = {
    "path": path,
    "existed": existed == "true",
    "wasSymlink": was_symlink == "true",
    "previousTarget": previous if previous else None,
    "linkedTarget": linked,
    "savedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
}
with open(out, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY
}

default_socket() {
  info "making '$CIDER_CONTEXT' the default docker context"
  run docker context use "$CIDER_CONTEXT"

  local state target
  state="$(classify_system_socket)"
  case "$state" in
    symlink:*) target="${state#symlink:}" ;;
    *) target="" ;;
  esac

  if [ "$state" = "symlink:$SOCK" ]; then
    info "$DOCKER_SOCK already points at $SOCK; nothing to change."
    return 0
  fi

  if [ "$state" = "realfile" ] && [ "$FORCE" != true ]; then
    warn "$DOCKER_SOCK is a real file/socket, not a symlink, so it cannot be restored later."
    warn "Refusing to replace it. Stop whatever owns it, or re-run with --force to replace it anyway"
    warn "(uninstall will then only be able to remove cider's link, not bring the original back)."
    return 1
  fi

  case "$state" in
    absent) info "/var/run/docker.sock does not exist today; this will create it." ;;
    realfile) info "/var/run/docker.sock is a real file/socket today (--force given); it will be replaced." ;;
    symlink:*) info "/var/run/docker.sock -> $target today; this will repoint it to $SOCK." ;;
  esac

  echo
  echo "This changes the SYSTEM-WIDE Docker socket that plain 'docker' commands use everywhere on"
  echo "this Mac (not just for cider), requires sudo, and changes engines your other tools reach:"
  echo "  from: ${target:-"(nothing / not a symlink)"}"
  echo "  to:   $SOCK"
  echo
  echo "The two engines share nothing -- 'docker ps'/'docker images' will look empty right after the"
  echo "switch, and every image needs re-pulling under cider."
  echo
  echo "NOTE: macOS clears /private/var/run at boot, so this symlink does NOT survive a reboot (your"
  echo "docker context will still say '$CIDER_CONTEXT' though). Re-run this script after a restart."
  echo

  if ! confirm "Repoint $DOCKER_SOCK to cider now?"; then
    warn "skipped repointing $DOCKER_SOCK (declined). '$CIDER_CONTEXT' is still the active docker context."
    return 1
  fi

  if [ "$DRY_RUN" != true ]; then
    mkdir -p "$DATA_DIR"
    case "$state" in
      absent) write_backup false false "" "$SOCK" ;;
      realfile) write_backup true false "" "$SOCK" ;;
      symlink:*) write_backup true true "$target" "$SOCK" ;;
    esac
    info "saved previous target to $BACKUP_FILE"
  else
    printf '+ %s\n' "write $BACKUP_FILE (previousTarget=${target:-null})"
  fi

  run sudo ln -sf "$SOCK" "$DOCKER_SOCK"
  info "$DOCKER_SOCK -> $SOCK"
}

# ---------------------------------------------------------------------------
# verification
# ---------------------------------------------------------------------------
verify() {
  # $1: "full" to also verify the system socket and a plain (no DOCKER_CONTEXT) docker run --
  # only meaningful once default_socket() has actually succeeded. "context-only" otherwise.
  local mode="${1:-context-only}"
  info "verifying"

  if [ "$DRY_RUN" = true ]; then
    printf '+ %s\n' "test -S $SOCK"
    printf '+ %s\n' "$BIN status"
    printf '+ %s\n' "DOCKER_CONTEXT=$CIDER_CONTEXT docker run --rm alpine:3.22 echo ok"
    if [ "$mode" = full ]; then
      printf '+ %s\n' "readlink $DOCKER_SOCK"
      printf '+ %s\n' "docker run --rm alpine:3.22 echo ok"
    fi
    return 0
  fi

  [ -S "$SOCK" ] || die "no socket at $SOCK -- cider does not appear to be running. Check $DATA_DIR/daemon.log."
  info "socket answers: $SOCK"

  "$BIN" status || die "'cider status' reported a problem (see above)."

  if command -v docker >/dev/null 2>&1; then
    info "docker run --rm alpine:3.22 echo ok  (through the '$CIDER_CONTEXT' context)"
    DOCKER_CONTEXT="$CIDER_CONTEXT" docker run --rm alpine:3.22 echo ok

    if [ "$mode" = full ]; then
      local link
      link="$(readlink "$DOCKER_SOCK" 2>/dev/null || true)"
      [ "$link" = "$SOCK" ] || die "$DOCKER_SOCK does not point at $SOCK (readlink: '${link:-<none>}')."
      info "$DOCKER_SOCK -> $link"

      info "docker run --rm alpine:3.22 echo ok  (plain, no DOCKER_CONTEXT set -- shows which engine 'docker version' actually reaches)"
      docker version
      docker run --rm alpine:3.22 echo ok
    fi
  fi
}

# ---------------------------------------------------------------------------
# --uninstall
# ---------------------------------------------------------------------------
uninstall() {
  info "uninstalling cider"

  if [ -x "$BIN" ]; then
    run_best_effort "$BIN" uninstall --data-dir "$DATA_DIR"
  else
    warn "no binary at $BIN; skipping 'cider uninstall' (nothing to bootout/kickstart)."
  fi

  # `cider uninstall` above already restores the system socket from $BACKUP_FILE when it can
  # (sudo -n, non-interactive). If that needed a password it leaves the backup file in place and
  # prints the manual command instead of touching anything -- finish that here, interactively.
  if [ "$DRY_RUN" = true ]; then
    if [ -f "$BACKUP_FILE" ] || [ "$DRY_RUN" = true ]; then
      printf '+ %s\n' "read previousTarget from $BACKUP_FILE"
      printf '+ %s\n' "sudo ln -sf <previousTarget> $DOCKER_SOCK   # or: sudo rm -f $DOCKER_SOCK if nothing was there before"
      printf '+ %s\n' "rm -f $BACKUP_FILE"
    fi
  elif [ -f "$BACKUP_FILE" ]; then
    local previous rc
    set +e
    previous="$(python3 - "$BACKUP_FILE" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
if not data.get("existed"):
    print("")
elif data.get("wasSymlink") and data.get("previousTarget"):
    print(data["previousTarget"])
else:
    sys.exit(2)
PY
)"
    rc=$?
    set -e
    if [ $rc -eq 2 ]; then
      warn "$BACKUP_FILE says $DOCKER_SOCK was a real file/socket before cider replaced it; that cannot be"
      warn "restored automatically. Recreate it by hand (e.g. restart the engine that owned it)."
    elif [ $rc -ne 0 ]; then
      warn "could not read $BACKUP_FILE; leaving $DOCKER_SOCK as-is. Restore it by hand if needed."
    else
      echo
      echo "cider's own restore needed a password it could not prompt for. This will finish it:"
      if [ -n "$previous" ]; then
        echo "  sudo ln -sf $previous $DOCKER_SOCK"
      else
        echo "  sudo rm -f $DOCKER_SOCK   (nothing was there before cider linked it)"
      fi
      echo
      if confirm "Restore $DOCKER_SOCK now?"; then
        if [ -n "$previous" ]; then
          run sudo ln -sf "$previous" "$DOCKER_SOCK"
        else
          run sudo rm -f "$DOCKER_SOCK"
        fi
        rm -f "$BACKUP_FILE"
      else
        warn "left $DOCKER_SOCK untouched and kept $BACKUP_FILE; re-run --uninstall to finish, or restore by hand."
      fi
    fi
  fi

  run_best_effort docker context use "$ORBSTACK_CONTEXT"

  if [ "$DRY_RUN" != true ]; then
    local link
    link="$(readlink "$DOCKER_SOCK" 2>/dev/null || true)"
    info "$DOCKER_SOCK now points at: ${link:-<not a symlink / absent>}"
  fi

  if [ -f "$BIN" ]; then
    run rm -f "$BIN"
    rmdir "$BIN_DIR" 2>/dev/null || true
  fi

  echo
  echo "Uninstalled. The published binary, launchd agent and '$CIDER_CONTEXT' docker context are"
  echo "gone, and the active docker context is '$ORBSTACK_CONTEXT'. cider's own state (~/.cider) was"
  echo "left in place -- remove it yourself if you want the container/network/volume records gone too."
}

# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
if [ "$DO_UNINSTALL" = true ]; then
  uninstall
  exit 0
fi

preflight
publish

if [ "$NO_DAEMON" = true ]; then
  echo
  echo "Published only (--no-daemon): $BIN"
  echo "Run it directly with '$BIN serve', or re-run without --no-daemon to install it as a service."
  exit 0
fi

cider_install

if [ "$NO_DEFAULT_SOCKET" = true ]; then
  verify
  echo
  echo "Done. cider is installed and running, with its own '$CIDER_CONTEXT' docker context."
  echo "  use it:   DOCKER_CONTEXT=$CIDER_CONTEXT docker ...      (or: docker context use $CIDER_CONTEXT)"
  echo "  undo:     scripts/install-local.sh --uninstall"
  exit 0
fi

default_socket_rc=0
default_socket || default_socket_rc=$?
if [ "$default_socket_rc" -eq 0 ]; then
  verify full
else
  verify context-only
fi

echo
if [ "$DRY_RUN" = true ]; then
  echo "Dry run only -- nothing was built, installed, or changed. Re-run without --dry-run to do it for real."
elif [ "$default_socket_rc" -eq 0 ]; then
  echo "Done. cider is installed, running, and is now the DEFAULT docker socket on this Mac."
  echo "  use it:   docker ...                                    (plain docker now reaches cider)"
  echo "  undo:     scripts/install-local.sh --uninstall           (restores $ORBSTACK_CONTEXT and /var/run/docker.sock)"
  echo "  reboot?   re-run this script -- /var/run is cleared at boot, the docker context is not."
else
  echo "Done. cider is installed and running under its own '$CIDER_CONTEXT' docker context, but it is"
  echo "NOT the default socket yet (see the warning above)."
  echo "  use it:   DOCKER_CONTEXT=$CIDER_CONTEXT docker ...      (or: docker context use $CIDER_CONTEXT)"
  echo "  undo:     scripts/install-local.sh --uninstall"
fi
