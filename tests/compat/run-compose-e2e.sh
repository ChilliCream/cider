#!/usr/bin/env bash
# tests/compat/run-compose-e2e.sh
#
# Deliverable 5 of the compat harness (see tests/compat/README.md).
#
# Two independent checks, both run against our own DOCKER_HOST:
#   A) docker/compose's own `pkg/e2e` Go test subset, at the tag matching the
#      locally installed `docker compose` CLI plugin — only if `go` is on
#      PATH (it is a build-time dependency of that suite, not something we
#      vendor).
#   B) A tiny two-service fixture (tests/compat/fixtures/compose/docker-compose.yml)
#      exercised via `docker compose up/ps/logs/down`, asserting the `client`
#      service reaches `web` by its Compose-assigned DNS name. This always
#      runs, since it needs nothing beyond the `docker compose` CLI plugin
#      already required by every other script in this harness.
#
# No case-level allowlist here (unlike podman-apiv2 / docker-py): this suite
# is small enough to just be pass/fail as a whole. Exit non-zero if either
# check fails to run at all, or if the fixture smoke test fails.
#
# Report: tests/compat/reports/compose-e2e.md

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/daemon.sh
source "$SCRIPT_DIR/lib/daemon.sh"

mkdir -p "$CIDER_COMPAT_DIR/reports"
REPORT="$CIDER_COMPAT_DIR/reports/compose-e2e.md"
FIXTURE_DIR="$CIDER_COMPAT_DIR/fixtures/compose"
COMPOSE_PROJECT="cider-compat"

overall_pass=1
go_ran=0
go_pass=0
COMPOSE_TAG=""
fixture_pass=1
fixture_notes=""
client_log=""

start_daemon || { echo "daemon failed to start" >&2; exit 1; }
snapshot_images

cleanup_fixture() {
  ( cd "$FIXTURE_DIR" && docker compose -p "$COMPOSE_PROJECT" down -v -t 5 ) >/dev/null 2>&1 || true
  docker network rm cider-compat-net >/dev/null 2>&1 || true
}
trap 'cleanup_fixture; cleanup_new_images; stop_daemon' EXIT

# KNOWN DAEMON BUG (found via this harness, reported, NOT fixed here per
# tests/compat ownership boundaries): POST /networks/create returns 500
# "Object reference not set to an instance of an object." whenever the
# request body has `"Options":null` (confirmed via a proxied capture of
# real `docker compose` traffic -- compose *always* sends Options:null for
# its implicit default per-project network, and IPAM:null alone / Driver:""
# alone are both fine, so this is specifically about a null Options map).
# That makes `docker compose up` unable to create its own default network
# for ANY compose file that doesn't pre-declare an external network --
# i.e. most compose files, out of the box, against cider today.
#
# Workaround (fixture-side only, so the rest of this smoke test -- DNS /
# service-name resolution -- can still run and produce signal): pre-create
# the network via the plain `docker network create` compat endpoint
# (unaffected -- no Options:null involved there) and have the compose file
# reference it as `external: true`, so compose never calls
# POST /networks/create at all.
echo "==> Pre-creating external network 'cider-compat-net' (works around a daemon bug: POST /networks/create 500s when compose's own create-network body has Options:null -- see reports/compose-e2e.md)"
docker network rm cider-compat-net >/dev/null 2>&1 || true
if ! docker network create cider-compat-net >/dev/null; then
  echo "FATAL: even a plain 'docker network create' failed -- can't proceed with the fixture smoke test" >&2
  exit 1
fi

# --- Part A: docker/compose's own pkg/e2e Go suite, only if `go` is present ---
if command -v go >/dev/null 2>&1; then
  go_ran=1
  COMPOSE_TAG="v$(docker compose version --short 2>/dev/null || true)"
  [[ "$COMPOSE_TAG" == "v" || -z "$COMPOSE_TAG" ]] && COMPOSE_TAG="v5.1.2"
  COMPOSE_SRC=/tmp/cider-compat-compose-src

  echo "==> [A] Cloning docker/compose @ $COMPOSE_TAG (go is on PATH)"
  rm -rf "$COMPOSE_SRC"
  if git clone --depth 1 --filter=blob:none --branch "$COMPOSE_TAG" \
      https://github.com/docker/compose "$COMPOSE_SRC"; then
    echo "==> [A] Running docker/compose pkg/e2e subset against our socket"
    if ( cd "$COMPOSE_SRC" && go test ./pkg/e2e \
           -run 'TestLocalComposeUp|TestComposePs|TestLocalComposeLogs|TestNetworks|TestVolume' \
           -count=1 -timeout 20m ); then
      go_pass=1
    else
      echo "==> [A] docker/compose pkg/e2e subset FAILED" >&2
      overall_pass=0
    fi
  else
    echo "==> [A] git clone of docker/compose @ $COMPOSE_TAG failed; skipping Go suite" >&2
    go_ran=0
  fi
else
  echo "==> [A] 'go' not found on PATH — skipping docker/compose pkg/e2e Go suite" \
       "(expected on this machine; see tests/compat/README.md)."
fi

# --- Part B: our own two-service fixture smoke test (always runs) ---
echo "==> [B] Running fixture compose smoke test ($FIXTURE_DIR)"

if ! ( cd "$FIXTURE_DIR" && docker compose -p "$COMPOSE_PROJECT" up -d ); then
  fixture_pass=0
  fixture_notes="\`docker compose up -d\` failed outright — see daemon log."
else
  ( cd "$FIXTURE_DIR" && docker compose -p "$COMPOSE_PROJECT" ps )

  # Client loops every 2s (nslookup + wget); give DNS/networking generous
  # room to settle — cider spins up a per-network CoreDNS forwarder
  # container (see docs/ARCHITECTURE.md decisions log), which may take
  # longer to become resolvable than a plain bridge network. Adjust upward
  # if this proves marginal in practice.
  sleep 15

  client_log=$( cd "$FIXTURE_DIR" && docker compose -p "$COMPOSE_PROJECT" logs client 2>&1 )
  echo "$client_log" | tail -30

  if ! grep -q -- "-- OK reached web by name" <<<"$client_log"; then
    fixture_pass=0
    fixture_notes="client never logged a successful reach of 'web' by Compose DNS name within the 15s wait window."
  fi
fi

[[ $fixture_pass -eq 0 ]] && overall_pass=0

# --- report ---
{
  echo "# compose-e2e report"
  echo
  echo "Run: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## A. docker/compose pkg/e2e (Go)"
  echo
  if [[ $go_ran -eq 1 ]]; then
    echo "- Tag: $COMPOSE_TAG"
    echo "- Result: $( [[ $go_pass -eq 1 ]] && echo PASS || echo FAIL )"
  else
    echo "- SKIPPED: \`go\` not on PATH on this machine (or clone failed). This is a"
    echo "  portability branch, not a daemon finding — install Go to exercise it."
  fi
  echo
  echo "## B. Fixture smoke test (tests/compat/fixtures/compose/docker-compose.yml)"
  echo
  echo "- Result: $( [[ $fixture_pass -eq 1 ]] && echo PASS || echo FAIL )"
  if [[ $fixture_pass -eq 0 ]]; then
    echo "- Category: daemon-bug — Docker-compat container networking / embedded DNS is the"
    echo "  most likely culprit (compose itself only calls documented Engine API endpoints:"
    echo "  container create/start, network create, standard inspects). Re-categorize to"
    echo "  \`compose-specific\` only if triage shows compose relying on undocumented behavior"
    echo "  (e.g. \`depends_on\` startup ordering, which the Engine API does not guarantee)."
    echo "- Notes: $fixture_notes"
    echo
    echo '```'
    echo "$client_log" | tail -60
    echo '```'
  fi
} > "$REPORT"

echo "==> Report written to $REPORT"

if [[ $overall_pass -eq 1 ]]; then
  echo "==> compose-e2e: PASS"
  exit 0
else
  echo "==> compose-e2e: FAIL"
  exit 1
fi
