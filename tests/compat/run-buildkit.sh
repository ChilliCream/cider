#!/usr/bin/env bash
# tests/compat/run-buildkit.sh
#
# BuildKit compat smoke test: drives the *default* buildx builder (the
# `docker` driver, talking straight to cider's own /grpc + /session --
# cider-ger.5-.11 -- never `docker buildx create`) through real
# docker/buildx/compose commands against an isolated cider daemon, mirroring
# the scenarios in tests/Cider.E2E.Tests/BuildKitTests.cs. See cider-ger.12.
#
# No case-level allowlist (like run-compose-e2e.sh): this suite is small
# enough to be pass/fail per scenario, reported as a table.
#
# Report: tests/compat/reports/buildkit.md
# Exit code: non-zero if any scenario FAILs, or if the Apple builder VM
# ('buildkit') is not left `running` afterwards -- straight through the
# `container` CLI, never through cider, since cider (per cider-ger.3/T4b)
# hides that VM from `docker ps` entirely and this check exists specifically
# to catch a regression of that hiding.
#
# Env overrides:
#   CIDER_E2E_CONTEXT_MB   size of the large-context scenario, default 20

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/daemon.sh
source "$SCRIPT_DIR/lib/daemon.sh"

mkdir -p "$CIDER_COMPAT_DIR/reports"
REPORT="$CIDER_COMPAT_DIR/reports/buildkit.md"
WORK="/tmp/cider-compat-buildkit-work"

export DOCKER_BUILDKIT=1
unset BUILDX_BUILDER BUILDX_NO_DEFAULT_LOAD

RESULT_NAMES=()
RESULT_STATUS=()
RESULT_NOTES=()

record() {
  RESULT_NAMES+=("$1")
  RESULT_STATUS+=("$2")
  RESULT_NOTES+=("${3:-}")
}

# run_with_timeout SECS CMD...: this machine has neither GNU `timeout` nor
# `gtimeout` (confirmed absent), so a hung build is bounded by hand: run in
# the background, race it against a sleeping killer, wait on whichever pid
# actually finishes first is not portable in bash 3.2 either -- simplest
# correct primitive is "kill the job after N seconds if it's still alive".
run_with_timeout() {
  local secs="$1"; shift
  # Run the command under job control so it gets its own process group
  # (pgid == pid, since it's a single simple command), then drop job
  # control again so the rest of the function behaves as before. This lets
  # the watcher kill the *whole group* on timeout, not just the direct
  # child -- a hung buildx/docker build can spawn its own subprocesses
  # (e.g. the CLI's transfer/progress helpers) that would otherwise
  # survive past `trap stop_daemon EXIT`.
  set -m
  "$@" &
  local pid=$!
  set +m
  (
    sleep "$secs" 2>/dev/null
    kill -9 -- "-$pid" 2>/dev/null   # whole process group
    pkill -9 -P "$pid" 2>/dev/null  # fallback: any direct children
    kill -9 "$pid" 2>/dev/null      # fallback: the process itself
  ) &
  local watcher=$!
  local status=0
  wait "$pid" 2>/dev/null || status=$?
  kill "$watcher" 2>/dev/null
  wait "$watcher" 2>/dev/null
  return $status
}

start_daemon || { echo "daemon failed to start" >&2; exit 1; }
trap 'stop_daemon' EXIT

rm -rf "$WORK"
mkdir -p "$WORK"

# ---------- 1. basic build/tag/run ----------
ctx="$WORK/basic"; mkdir -p "$ctx"
cat >"$ctx/Dockerfile" <<'EOF'
FROM alpine:3.22
RUN echo hello > /hello
CMD ["cat", "/hello"]
EOF
if ( cd "$ctx" && docker build -t cider-compat/bk-basic:1 . ) >/dev/null 2>&1 &&
   [[ "$(docker run --rm cider-compat/bk-basic:1)" == "hello" ]]; then
  record "basic build, tag, run" PASS
else
  record "basic build, tag, run" FAIL
fi
docker rmi -f cider-compat/bk-basic:1 >/dev/null 2>&1 || true

# ---------- 2. --build-arg + --target (multi-stage) ----------
ctx="$WORK/target"; mkdir -p "$ctx"
cat >"$ctx/Dockerfile" <<'EOF'
FROM alpine:3.22 AS base
ARG GREETING=unset
RUN echo "$GREETING" > /greeting

FROM alpine:3.22 AS unreachable
RUN false

FROM base AS final
CMD ["cat", "/greeting"]
EOF
if ( cd "$ctx" && docker build --build-arg GREETING=hi-target --target final -t cider-compat/bk-target:1 . ) >/dev/null 2>&1 &&
   [[ "$(docker run --rm cider-compat/bk-target:1)" == "hi-target" ]]; then
  record "--build-arg + --target (multi-stage)" PASS
else
  record "--build-arg + --target (multi-stage)" FAIL
fi
docker rmi -f cider-compat/bk-target:1 >/dev/null 2>&1 || true

# ---------- 3. --secret ----------
ctx="$WORK/secret"; mkdir -p "$ctx"
cat >"$ctx/Dockerfile" <<'EOF'
# syntax=docker/dockerfile:1
FROM alpine:3.22
RUN --mount=type=secret,id=tok cat /run/secrets/tok > /secret-out
CMD ["cat", "/secret-out"]
EOF
printf 's3cr3t' > "$WORK/secret.txt"
if ( cd "$ctx" && docker build --secret id=tok,src="$WORK/secret.txt" -t cider-compat/bk-secret:1 . ) >/dev/null 2>&1 &&
   [[ "$(docker run --rm cider-compat/bk-secret:1)" == "s3cr3t" ]]; then
  record "--secret id=tok,src=file" PASS
else
  record "--secret id=tok,src=file" FAIL
fi
docker rmi -f cider-compat/bk-secret:1 >/dev/null 2>&1 || true

# ---------- 4. cache mount + heredoc RUN ----------
ctx="$WORK/cache-heredoc"; mkdir -p "$ctx"
cat >"$ctx/Dockerfile" <<'EOF'
# syntax=docker/dockerfile:1
FROM alpine:3.22
RUN --mount=type=cache,target=/cache echo cache-note > /cache/note && cp /cache/note /from-cache
RUN <<HEREDOC
echo heredoc-hello > /heredoc
HEREDOC
CMD ["sh", "-c", "cat /from-cache; cat /heredoc"]
EOF
out=$( { cd "$ctx" && docker build -t cider-compat/bk-cache:1 . && docker run --rm cider-compat/bk-cache:1; } 2>&1 )
if grep -q "cache-note" <<<"$out" && grep -q "heredoc-hello" <<<"$out"; then
  record "cache mount + heredoc RUN" PASS
else
  record "cache mount + heredoc RUN" FAIL "$out"
fi
docker rmi -f cider-compat/bk-cache:1 >/dev/null 2>&1 || true

# ---------- 5. --progress plain ----------
ctx="$WORK/progress"; mkdir -p "$ctx"
printf 'FROM alpine:3.22\nRUN echo hi > /hi\n' > "$ctx/Dockerfile"
progress_status=0
out=$( cd "$ctx" && docker build --progress plain -t cider-compat/bk-progress:1 . 2>&1 ) || progress_status=$?
# The step name below is also what a *failed* build's first line looks like (it's the step that
# failed), so the exit code must be checked too -- grep alone would false-PASS on any failure that
# happens to occur at or after that step.
if [[ $progress_status -eq 0 ]] && grep -qF '#1 [internal] load build definition' <<<"$out"; then
  record "--progress plain" PASS
else
  record "--progress plain" FAIL "$out"
fi
docker rmi -f cider-compat/bk-progress:1 >/dev/null 2>&1 || true

# ---------- 6. --iidfile / -q agree with `docker images -q` ----------
ctx="$WORK/iid"; mkdir -p "$ctx"
printf 'FROM alpine:3.22\nRUN echo hi > /hi\n' > "$ctx/Dockerfile"
iid_file="$WORK/iid.txt"
if ( cd "$ctx" && docker build --iidfile "$iid_file" -t cider-compat/bk-iid:1 . ) >/dev/null 2>&1; then
  iid=$(cat "$iid_file" 2>/dev/null || true)
  imgid=$(docker images --no-trunc -q cider-compat/bk-iid:1)
  qid=$( cd "$ctx" && docker build -q -t cider-compat/bk-iid:1 . 2>/dev/null )
  if [[ -n "$imgid" && "$iid" == "$imgid" && "$qid" == "$imgid" ]]; then
    record "--iidfile and -q match \`docker images -q\`" PASS
  else
    record "--iidfile and -q match \`docker images -q\`" FAIL "iid=$iid img=$imgid q=$qid"
  fi
else
  record "--iidfile and -q match \`docker images -q\`" FAIL
fi
docker rmi -f cider-compat/bk-iid:1 >/dev/null 2>&1 || true

# ---------- 7. untagged build is dangling and prunable ----------
ctx="$WORK/untagged"; mkdir -p "$ctx"
printf 'FROM alpine:3.22\nRUN echo hi > /hi\n' > "$ctx/Dockerfile"
built_id=$( cd "$ctx" && docker build -q . 2>/dev/null )
built_short="${built_id#sha256:}"; built_short="${built_short:0:12}"
dangling_before=$(docker images --filter dangling=true -q)
docker image prune -f >/dev/null 2>&1
dangling_after=$(docker images --filter dangling=true -q)
if [[ -n "$built_short" ]] && grep -q "$built_short" <<<"$dangling_before" && ! grep -q "$built_short" <<<"$dangling_after"; then
  record "untagged build is dangling + prunable" PASS
else
  record "untagged build is dangling + prunable" FAIL
fi

# ---------- 8. --no-cache ----------
ctx="$WORK/no-cache"; mkdir -p "$ctx"
printf 'FROM alpine:3.22\nRUN echo hi > /hi\n' > "$ctx/Dockerfile"
if ( cd "$ctx" && docker build -t cider-compat/bk-nc:1 . && docker build --no-cache -t cider-compat/bk-nc:1 . ) >/dev/null 2>&1; then
  record "--no-cache" PASS
else
  record "--no-cache" FAIL
fi
docker rmi -f cider-compat/bk-nc:1 >/dev/null 2>&1 || true

# ---------- 9. --output type=local, type=tar ----------
ctx="$WORK/output"; mkdir -p "$ctx"
cat >"$ctx/Dockerfile" <<'EOF'
FROM alpine:3.22 AS build
RUN echo hello-out > /hello

FROM scratch
COPY --from=build /hello /hello
EOF
local_dest="$WORK/local-out"; mkdir -p "$local_dest"
tar_dest="$WORK/tar-out.tar"
if ( cd "$ctx" && docker build --output type=local,dest="$local_dest" . ) >/dev/null 2>&1 &&
   [[ "$(cat "$local_dest/hello" 2>/dev/null)" == "hello-out" ]] &&
   ( cd "$ctx" && docker build --output type=tar,dest="$tar_dest" . ) >/dev/null 2>&1 &&
   [[ -s "$tar_dest" ]] && tar -tf "$tar_dest" | grep -q hello; then
  record "--output type=local,dest=<dir> and type=tar,dest=<file>" PASS
else
  record "--output type=local,dest=<dir> and type=tar,dest=<file>" FAIL
fi

# ---------- 10. buildx inspect/du/prune, builder prune ----------
inspect_out=$(docker buildx inspect default --bootstrap 2>&1)
if grep -qE "Status:[[:space:]]+running" <<<"$inspect_out" && grep -q "linux/arm64" <<<"$inspect_out" &&
   docker buildx du >/dev/null 2>&1 &&
   docker buildx prune -f >/dev/null 2>&1 &&
   docker builder prune -f >/dev/null 2>&1; then
  record "buildx inspect default --bootstrap, du, prune -f; builder prune -f" PASS
else
  record "buildx inspect default --bootstrap, du, prune -f; builder prune -f" FAIL "$inspect_out"
fi

# ---------- 11. compose build with a shared context (two services) ----------
ctx="$WORK/compose"; mkdir -p "$ctx"
cat >"$ctx/docker-compose.yml" <<'EOF'
services:
  svc-a:
    build:
      context: .
      dockerfile: Dockerfile.a
    command: ["sleep", "300"]
  svc-b:
    build:
      context: .
      dockerfile: Dockerfile.b
    command: ["sleep", "300"]
EOF
echo "shared-context-marker" > "$ctx/shared.txt"
printf 'FROM alpine:3.22\nCOPY shared.txt /shared.txt\nRUN echo svc-a >> /shared.txt\n' > "$ctx/Dockerfile.a"
printf 'FROM alpine:3.22\nCOPY shared.txt /shared.txt\nRUN echo svc-b >> /shared.txt\n' > "$ctx/Dockerfile.b"
compose_project="cider-compat-bk"
if ( cd "$ctx" && docker compose -p "$compose_project" build && docker compose -p "$compose_project" up -d ) >/dev/null 2>&1; then
  ps_out=$( cd "$ctx" && docker compose -p "$compose_project" ps --format '{{.Service}}: {{.State}}' )
  if grep -q "svc-a: running" <<<"$ps_out" && grep -q "svc-b: running" <<<"$ps_out"; then
    record "compose build (two services, one shared context)" PASS
  else
    record "compose build (two services, one shared context)" FAIL "$ps_out"
  fi
else
  record "compose build (two services, one shared context)" FAIL
fi
( cd "$ctx" && docker compose -p "$compose_project" down -v --remove-orphans --rmi local ) >/dev/null 2>&1 || true

# ---------- 12. buildx bake with two targets sharing a context ----------
ctx="$WORK/bake"; mkdir -p "$ctx"
echo "bake-shared-context" > "$ctx/shared.txt"
printf 'FROM alpine:3.22\nCOPY shared.txt /shared.txt\nRUN echo bake-a >> /shared.txt\n' > "$ctx/Dockerfile.a"
printf 'FROM alpine:3.22\nCOPY shared.txt /shared.txt\nRUN echo bake-b >> /shared.txt\n' > "$ctx/Dockerfile.b"
cat >"$ctx/docker-bake.hcl" <<'EOF'
group "default" {
  targets = ["a", "b"]
}
target "a" {
  context    = "."
  dockerfile = "Dockerfile.a"
  tags       = ["cider-compat/bk-bake-a:1"]
}
target "b" {
  context    = "."
  dockerfile = "Dockerfile.b"
  tags       = ["cider-compat/bk-bake-b:1"]
}
EOF
if ( cd "$ctx" && docker buildx bake ) >/dev/null 2>&1; then
  images_out=$(docker images --format '{{.Repository}}:{{.Tag}}')
  if grep -q "cider-compat/bk-bake-a:1" <<<"$images_out" && grep -q "cider-compat/bk-bake-b:1" <<<"$images_out"; then
    record "buildx bake (two targets, shared context)" PASS
  else
    record "buildx bake (two targets, shared context)" FAIL
  fi
else
  record "buildx bake (two targets, shared context)" FAIL
fi
docker rmi -f cider-compat/bk-bake-a:1 cider-compat/bk-bake-b:1 >/dev/null 2>&1 || true

# ---------- 13. large-ish build context within a 180s budget ----------
mb="${CIDER_E2E_CONTEXT_MB:-20}"
ctx="$WORK/large"; mkdir -p "$ctx"
printf 'FROM alpine:3.22\nCOPY . /ctx\nRUN ls -la /ctx > /listing\n' > "$ctx/Dockerfile"
head -c "$((mb * 1024 * 1024))" /dev/urandom > "$ctx/payload.bin"
if run_with_timeout 180 docker build -t cider-compat/bk-large:1 "$ctx" >/dev/null 2>&1; then
  record "${mb} MiB build context within 180s" PASS
else
  record "${mb} MiB build context within 180s" FAIL
fi
docker rmi -f cider-compat/bk-large:1 >/dev/null 2>&1 || true

# ---------- Apple builder VM survival (cider-ger.3/T4b) ----------
builder_state=$(container builder status 2>/dev/null | awk '$1=="buildkit"{print $3}')
builder_ok=1
[[ "$builder_state" == "running" ]] || builder_ok=0

overall_pass=1
for status in "${RESULT_STATUS[@]}"; do
  [[ "$status" == "FAIL" ]] && overall_pass=0
done
[[ $builder_ok -eq 1 ]] || overall_pass=0

{
  echo "# buildkit report"
  echo
  echo "Run: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "| Scenario | Result |"
  echo "|---|---|"
  for i in "${!RESULT_NAMES[@]}"; do
    echo "| ${RESULT_NAMES[$i]} | ${RESULT_STATUS[$i]} |"
  done
  echo
  echo "## Apple builder VM survival (cider-ger.3/T4b)"
  echo
  echo "\`container builder status\` after the run: \`${builder_state:-<none>}\` ($( [[ $builder_ok -eq 1 ]] && echo PASS || echo FAIL ))"
  echo
  for i in "${!RESULT_NAMES[@]}"; do
    if [[ "${RESULT_STATUS[$i]}" == "FAIL" && -n "${RESULT_NOTES[$i]:-}" ]]; then
      echo "### ${RESULT_NAMES[$i]}"
      echo
      echo '```'
      echo "${RESULT_NOTES[$i]}" | tail -60
      echo '```'
      echo
    fi
  done
} > "$REPORT"

echo "==> Report written to $REPORT"

if [[ $overall_pass -eq 1 ]]; then
  echo "==> buildkit: PASS"
  exit 0
else
  echo "==> buildkit: FAIL"
  exit 1
fi
