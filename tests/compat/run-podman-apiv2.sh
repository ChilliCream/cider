#!/usr/bin/env bash
# tests/compat/run-podman-apiv2.sh
#
# Runs Podman's test/apiv2 `.at` files (Apache-2.0, fetched at a pinned tag
# — never vendored) against cider's socket, using our own from-scratch
# `t`/`is`/`like`/`podman()` runner (lib/apiv2-runner.sh) in place of
# upstream's test-apiv2 (which hard-launches `podman system service` on a
# TCP port and needs the real `podman` binary throughout). See
# lib/apiv2-runner.sh's header comment for the libpod-vs-Docker-compat
# grading design.
#
# Only the Docker-compat surface is graded. Files chosen to match the task
# scope:
#   10-images.at, 12-imagesMore.at, 20-containers.at, 25-containersMore.at,
#   30-volumes.at, 35-networks.at, 44-mounts.at, 70-short-names.at
# Excluded on purpose:
#   40-pods.at (podman pods: no Docker Engine API equivalent)
#   45-system.at, 50-secrets.at, 60-auth.at (system/secrets/registry-auth:
#     out of scope per task)
#   75-compose*.at does not exist in podman's test/apiv2 tree at this tag.
#
# Exit code: non-zero only if a previously-allowlisted case regresses (see
# lib/apiv2_report.py for the exact allowlist protocol). First run always
# exits 0 and creates allowlists/podman-apiv2.txt from whatever passed.
#
# Deliberately NOT `set -u`: this script sources upstream Podman .at files
# verbatim (never patched), and upstream's own bash idioms (uninitialized
# `local -a curl_args`, `$path`/`$output` left as plain globals across
# functions, etc.) are not nounset-safe. Enforcing -u here would fight the
# very upstream code this script exists to run unmodified.
set -o pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$SCRIPT_DIR/reports" "$SCRIPT_DIR/allowlists"

# shellcheck source=lib/daemon.sh
source "$SCRIPT_DIR/lib/daemon.sh"

PODMAN_TAG="${PODMAN_APIV2_TAG:-v6.1.0}"
PODMAN_SRC="${CIDER_COMPAT_PODMAN_SRC:-/tmp/cider-compat-podman}"
ALLOWLIST="$SCRIPT_DIR/allowlists/podman-apiv2.txt"
REPORT="$SCRIPT_DIR/reports/podman-apiv2-failures.generated.md"
RUN_LOG="$SCRIPT_DIR/reports/podman-apiv2-run.log"

TARGET_FILES=(10-images 12-imagesMore 20-containers 25-containersMore 30-volumes 35-networks 44-mounts 70-short-names)

command -v jq >/dev/null 2>&1 || {
  echo "ERROR: jq is required by lib/apiv2-runner.sh's field assertions and was not found on PATH." >&2
  echo "       (Installing it is outside this script's scope; see tests/compat/README.md.)" >&2
  exit 1
}
command -v git >/dev/null 2>&1 || { echo "ERROR: git is required to fetch the upstream suite." >&2; exit 1; }

echo "== run-podman-apiv2.sh: containers/podman test/apiv2 @ ${PODMAN_TAG} against cider =="

if [[ ! -d "$PODMAN_SRC/.git" ]]; then
  echo "Cloning containers/podman @ ${PODMAN_TAG} (sparse checkout: test/apiv2 only) into $PODMAN_SRC ..."
  rm -rf "$PODMAN_SRC"
  git clone --depth 1 --filter=blob:none --sparse --branch "$PODMAN_TAG" https://github.com/containers/podman "$PODMAN_SRC" \
    || { echo "ERROR: clone failed"; exit 1; }
  git -C "$PODMAN_SRC" sparse-checkout set test/apiv2 || { echo "ERROR: sparse-checkout failed"; exit 1; }
else
  echo "Reusing existing clone at $PODMAN_SRC (delete it to re-fetch)"
fi

AT_DIR="$PODMAN_SRC/test/apiv2"
[[ -d "$AT_DIR" ]] || { echo "ERROR: $AT_DIR not found after clone -- tag layout may have changed" >&2; exit 1; }

start_daemon || exit 1
snapshot_images
trap 'cleanup_new_images; stop_daemon' EXIT

# shellcheck source=lib/apiv2-runner.sh
source "$SCRIPT_DIR/lib/apiv2-runner.sh"

: >"$RUN_LOG"
exec > >(tee -a "$RUN_LOG") 2>&1

for f in "${TARGET_FILES[@]}"; do
  at="$AT_DIR/$f.at"
  if [[ ! -f "$at" ]]; then
    echo "WARNING: $at not found at tag ${PODMAN_TAG}, skipping"
    continue
  fi
  # Pull the fixture image once per file's expectations (files themselves
  # also do this via the podman() shim, but priming it here keeps the first
  # graded assertion in each file from eating a pull's worth of latency).
  run_at_file "$at"
done

apiv2_summary

echo
echo "Cleaning up test fixtures created against cider (best-effort) ..."
docker rm -f compat_mount_subpath_test hostconfig_test >/dev/null 2>&1 || true

RESULTS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/cider-compat-apiv2-results.XXXXXX")"
{ [[ ${#PASS_RESULTS[@]} -gt 0 ]] && printf '%s\n' "${PASS_RESULTS[@]}"; true; } > "$RESULTS_DIR/pass.txt"
{ [[ ${#FAIL_RESULTS[@]} -gt 0 ]] && printf '%s\n' "${FAIL_RESULTS[@]}"; true; } > "$RESULTS_DIR/fail.txt"

python3 "$SCRIPT_DIR/lib/apiv2_report.py" \
  --pass-file "$RESULTS_DIR/pass.txt" \
  --fail-file "$RESULTS_DIR/fail.txt" \
  --allowlist "$ALLOWLIST" \
  --report "$REPORT" \
  --suite-label "podman test/apiv2 @ ${PODMAN_TAG}"
rc=$?

rm -rf "$RESULTS_DIR"
echo "Full run transcript: $RUN_LOG"
echo "Report: $REPORT"
echo "Allowlist: $ALLOWLIST"
exit $rc
