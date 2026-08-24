#!/bin/bash
# Live-driving proof that the release binary actually works: the E2E fixture builds the daemon in-process
# (DaemonFixture -> DaemonHost.Create), so it never exercises the *published* AOT binary. This
# script instead runs publish/cider as a real daemon process on its own socket/data dir and drives
# the same scenario families the E2E suite covers, over the real `docker` CLI, against the real
# Apple `container` runtime. Not a xunit run: a scripted proof that the native binary itself works.
#
# Usage: scripts/aot-live-proof.sh   (run under the shared runtime lock)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$ROOT/publish/cider"
SOCK="/tmp/cider-3wu.sock"
DATA="/tmp/cider-3wu-data"
LOG="/tmp/cider-3wu-daemon.log"
DOCKER_CONFIG_DIR="/tmp/cider-3wu-docker-config"
SCRATCH="/tmp/cider-3wu-scratch"
IMAGE="alpine:3.22"
PASS=0
FAIL=0
FAILED_NAMES=()

log()  { echo "[proof] $*"; }
ok()   { PASS=$((PASS+1)); echo "  OK: $1"; }
bad()  { FAIL=$((FAIL+1)); FAILED_NAMES+=("$1"); echo "  FAIL: $1 -- $2"; }

# macOS has no `timeout(1)`; `perl -e 'alarm ...; exec ...'` is the standard substitute and, unlike
# a hand-rolled background-job-plus-watchdog, does not fight bash's own job control inside a
# `$(...)` capture. This bounds anything that talks to the Apple `container` runtime, which is
# occasionally known to wedge a single container's exec channel (apple-demon flakiness unrelated
# to this ticket) rather than error out promptly.
with_timeout() {
  local secs="$1"; shift
  perl -e 'alarm shift @ARGV; exec @ARGV' "$secs" "$@"
}

d() { with_timeout 60 env DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" docker "$@"; }

cleanup() {
  log "cleanup"
  d compose -p e2eproof down -v --remove-orphans >/dev/null 2>&1 || true
  d rm -f proof-life proof-tty proof-port proof-net-peer >/dev/null 2>&1 || true
  d volume rm -f proof-vol >/dev/null 2>&1 || true
  d network rm proof-net >/dev/null 2>&1 || true
  d rmi -f e2eproof/built:1 >/dev/null 2>&1 || true

  # Belt-and-suspenders: if the daemon-mediated `d rm -f` above timed out on a wedged container
  # (a known Apple `container` flakiness, not specific to this daemon or binary), fall back to
  # killing the runtime process directly and removing it with the Apple CLI so nothing orphaned
  # survives this script, per the "no orphaned container start -a" rule.
  for name in proof-life proof-tty proof-port proof-net-peer; do
    pkill -9 -f "container-runtime-linux.*--uuid $name\$" >/dev/null 2>&1 || true
    pkill -9 -f "container start -a $name\$" >/dev/null 2>&1 || true
    container rm -f "$name" >/dev/null 2>&1 || true
  done
  container network rm proof-net >/dev/null 2>&1 || true
  # Apple's own volume store is global (not scoped to cider's --data-dir), so a fresh daemon's
  # `d volume rm` 404s on a volume record it never created itself if a previous interrupted run
  # left `proof-vol` behind at the Apple layer; remove it directly too.
  container volume rm proof-vol >/dev/null 2>&1 || true

  # A graceful SIGTERM lets DaemonLifecycle tear down its CoreDNS forwarder container(s) itself;
  # only fall back to force-removing a `cider-dns-*` container if that did not happen (e.g. this
  # script itself got SIGKILLed earlier and never reached this trap at all).
  if [ -n "${DAEMON_PID:-}" ] && kill -0 "$DAEMON_PID" 2>/dev/null; then
    kill "$DAEMON_PID" 2>/dev/null
    for _ in $(seq 1 20); do kill -0 "$DAEMON_PID" 2>/dev/null || break; sleep 0.5; done
    kill -9 "$DAEMON_PID" 2>/dev/null || true
  fi
  for forwarder in $(container ls -a --format json 2>/dev/null | grep -o '"id":"cider-dns-[^"]*"' | cut -d'"' -f4); do
    container rm -f "$forwarder" >/dev/null 2>&1 || true
  done
  rm -f "$SOCK"
  rm -rf "$DATA" "$DOCKER_CONFIG_DIR" "$SCRATCH"
}
trap cleanup EXIT

[ -x "$BIN" ] || { echo "no published binary at $BIN"; exit 1; }
file "$BIN"

rm -rf "$DATA" "$DOCKER_CONFIG_DIR" "$SCRATCH"
mkdir -p "$DOCKER_CONFIG_DIR" "$SCRATCH"
if [ -d "$HOME/.docker/cli-plugins" ]; then
  ln -s "$HOME/.docker/cli-plugins" "$DOCKER_CONFIG_DIR/cli-plugins"
fi
rm -f "$SOCK" "$LOG"

log "starting published AOT daemon: $BIN serve --socket $SOCK --data-dir $DATA"
"$BIN" serve --socket "$SOCK" --data-dir "$DATA" > "$LOG" 2>&1 &
DAEMON_PID=$!
log "daemon pid=$DAEMON_PID, waiting for /_ping"

READY=0
for _ in $(seq 1 60); do
  if curl -s --unix-socket "$SOCK" http://localhost/_ping >/dev/null 2>&1; then READY=1; break; fi
  kill -0 "$DAEMON_PID" 2>/dev/null || { echo "daemon exited early:"; cat "$LOG"; exit 1; }
  sleep 0.5
done
[ "$READY" = 1 ] || { echo "daemon never answered /_ping"; cat "$LOG"; exit 1; }
log "daemon is up"

# ---- version/info ----
v=$(d version --format '{{.Server.Os}}/{{.Server.Arch}}' 2>&1) && ok "docker version -> $v" || bad "docker version" "$v"

# ---- run / logs / exec / exec -it / stop / rm ----
out=$(d run -d --name proof-life "$IMAGE" sh -c 'echo L1; sleep 120' 2>&1) && ok "run -d" || bad "run -d" "$out"
sleep 1
out=$(d logs proof-life 2>&1) && [[ "$out" == *L1* ]] && ok "logs" || bad "logs" "$out"
out=$(d exec proof-life sh -c 'echo ok' 2>&1) && [[ "$out" == "ok" ]] && ok "exec" || bad "exec" "$out"

# exec -it: real pty via python helper, exactly like TtyTests.PtyHelper.
PTY_HELPER="$SCRATCH/pty_spawn.py"
cat > "$PTY_HELPER" <<'PYEOF'
import os, pty, select, struct, subprocess, sys, termios, fcntl, time
rows, cols = 24, 100
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, 0, 0))
child = subprocess.Popen(sys.argv[1:], stdin=slave, stdout=slave, stderr=slave, close_fds=True)
os.close(slave)
payload = sys.stdin.buffer.read()
out = bytearray()
sent = not payload
start = time.time(); last_out = start; deadline = start + 60
while time.time() < deadline:
    ready, _, _ = select.select([master], [], [], 0.5)
    if ready:
        try:
            chunk = os.read(master, 4096)
        except OSError:
            break
        if not chunk:
            break
        out += chunk; last_out = time.time(); continue
    if not sent:
        if (out and time.time() - last_out > 1.0) or time.time() - start > 15:
            os.write(master, payload); sent = True; last_out = time.time()
        continue
    if child.poll() is not None:
        break
    if time.time() - last_out > 10:
        break
try:
    code = child.wait(timeout=10)
except subprocess.TimeoutExpired:
    child.kill(); code = -1
while True:
    ready, _, _ = select.select([master], [], [], 0.2)
    if not ready:
        break
    try:
        chunk = os.read(master, 4096)
    except OSError:
        break
    if not chunk:
        break
    out += chunk
sys.stdout.buffer.write(bytes(out)); sys.stdout.buffer.flush()
sys.stderr.write("child exit %s\n" % code)
PYEOF

out=$(DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" \
  python3 "$PTY_HELPER" docker exec -i -t proof-life sh <<< $'tty\nexit\n' 2>&1)
if [[ "$out" == *"/dev/pts/"* || "$out" == *"/dev/ttys"* || "$out" == *"/dev/console"* ]]; then
  ok "exec -it (real pty attached)"
else
  bad "exec -it" "$out"
fi

out=$(d stop -t 3 proof-life 2>&1) && ok "stop" || bad "stop" "$out"
out=$(d rm -f proof-life 2>&1) && ok "rm" || bad "rm" "$out"

# ---- port publishing ----
out=$(d run -d --name proof-port -p 0:8080 "$IMAGE" sh -c \
  'while true; do { printf "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"; sleep 1; } | nc -l -p 8080 >/dev/null; done' 2>&1) \
  && ok "run -p 0:8080" || bad "run -p 0:8080" "$out"
sleep 1
hostport=$(d port proof-port 8080 2>&1 | sed -n 's/.*:\([0-9]*\)$/\1/p' | head -1)
if [ -n "$hostport" ]; then
  body=$(curl -s --max-time 5 "http://127.0.0.1:$hostport/" 2>&1)
  [ "$body" = "hi" ] && ok "published port carries traffic ($hostport)" || bad "published port traffic" "got: $body"
else
  bad "docker port" "no host port reported"
fi
d rm -f proof-port >/dev/null 2>&1

# ---- volume + docker cp ----
out=$(d volume create proof-vol 2>&1) && ok "volume create" || bad "volume create" "$out"
out=$(d run --rm -v proof-vol:/data "$IMAGE" sh -c 'echo hi > /data/f' 2>&1) && ok "write to volume" || bad "write to volume" "$out"
d run -d --name proof-life "$IMAGE" sleep 60 >/dev/null 2>&1
sleep 3
# Apple `container cp <name>:<path>` (which the daemon shells out to for both cp directions'
# stat-before-transfer step) hangs rather than 404s when <path> does not yet exist in the guest —
# reproduced identically against the framework-dependent build, so it is a pre-existing Apple CLI
# quirk, not an AOT regression. VolumeTests.Docker_cp_moves_files_in_both_directions works around
# it the same way the E2E suite does: stat an existing file for cp-out (/etc/hostname) and an
# existing directory for cp-in (/tmp/).
out=$(d cp proof-life:/etc/hostname "$SCRATCH/cp-out.txt" 2>&1) && [ -f "$SCRATCH/cp-out.txt" ] \
  && ok "docker cp container->host" || bad "docker cp container->host" "$out"
echo "cp-payload" > "$SCRATCH/cp-in.txt"
out=$(d cp "$SCRATCH/cp-in.txt" proof-life:/tmp/ 2>&1) && ok "docker cp host->container" || bad "docker cp host->container" "$out"
out=$(d exec proof-life cat /tmp/cp-in.txt 2>&1) && [[ "$out" == "cp-payload" ]] \
  && ok "cp payload readable inside container" || bad "cp payload readable inside container" "$out"
d rm -f proof-life >/dev/null 2>&1

# ---- network + DNS ----
out=$(d network create proof-net 2>&1) && ok "network create" || bad "network create" "$out"
out=$(d run -d --name proof-net-peer --network proof-net "$IMAGE" sleep 60 2>&1) && ok "run on user network" || bad "run on user network" "$out"
sleep 1
out=$(d run --rm --network proof-net "$IMAGE" nslookup proof-net-peer 2>&1) && [[ "$out" == *"Address"* ]] && ok "container-name DNS" || bad "container-name DNS" "$out"
d rm -f proof-net-peer >/dev/null 2>&1
d network rm proof-net >/dev/null 2>&1

# ---- classic (non-BuildKit) build ----
BUILDCTX="$SCRATCH/build-ctx"
mkdir -p "$BUILDCTX"
printf 'FROM alpine:3.22\nRUN echo hello > /hello\nCMD ["cat","/hello"]\n' > "$BUILDCTX/Dockerfile"
out=$(cd "$BUILDCTX" && DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" DOCKER_BUILDKIT=0 docker build -t e2eproof/built:1 . 2>&1)
[[ "$out" == *"Successfully tagged"* ]] && ok "classic docker build" || bad "classic docker build" "$out"
out=$(d run --rm e2eproof/built:1 2>&1) && [[ "$out" == "hello" ]] && ok "run built image" || bad "run built image" "$out"

# ---- compose up/down ----
COMPOSEDIR="$SCRATCH/compose"
mkdir -p "$COMPOSEDIR"
cat > "$COMPOSEDIR/docker-compose.yml" <<'YAML'
services:
  web:
    image: alpine:3.22
    command: ["sleep", "60"]
YAML
out=$(cd "$COMPOSEDIR" && DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" docker compose -p e2eproof up -d 2>&1) \
  && ok "compose up" || bad "compose up" "$out"
out=$(cd "$COMPOSEDIR" && DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" docker compose -p e2eproof ps 2>&1) \
  && [[ "$out" == *web* ]] && ok "compose ps" || bad "compose ps" "$out"
out=$(cd "$COMPOSEDIR" && DOCKER_HOST="unix://$SOCK" DOCKER_CONTEXT= DOCKER_CONFIG="$DOCKER_CONFIG_DIR" docker compose -p e2eproof down -v --remove-orphans 2>&1) \
  && ok "compose down" || bad "compose down" "$out"

echo
log "RESULT: $PASS passed, $FAIL failed"
if [ "$FAIL" -gt 0 ]; then
  printf '  failed: %s\n' "${FAILED_NAMES[@]}"
  echo "---- daemon log tail ----"
  tail -60 "$LOG"
  exit 1
fi
exit 0
