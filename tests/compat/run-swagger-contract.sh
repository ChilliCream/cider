#!/usr/bin/env bash
# tests/compat/run-swagger-contract.sh
#
# Deliverable 6: validate real cider responses against the official
# Docker Engine API v1.47 swagger.yaml (schema drift), plus a manual
# required-field completeness pass cross-checked against
# docs/ARCHITECTURE.md §3-4. This is the most valuable report the harness
# produces — see tests/compat/reports/swagger-contract.md after running.
#
# Prereqs: python3, network access (to fetch swagger.yaml once, cached
# under /tmp). No `jq` or `go` needed. Uses its own venv (separate from
# run-docker-py.sh's /tmp/cider-compat-venv) to keep dependency sets isolated.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

source lib/daemon.sh

VENV=/tmp/cider-compat-venv-swagger
SWAGGER_CACHE=/tmp/cider-compat-swagger-v1.47.yaml
REPORT="$SCRIPT_DIR/reports/swagger-contract.md"

mkdir -p "$SCRIPT_DIR/reports"

echo "==> Ensuring venv + pyyaml/jsonschema at $VENV"
if [[ ! -x "$VENV/bin/python3" ]]; then
  python3 -m venv "$VENV" || { echo "failed to create venv"; exit 1; }
fi
"$VENV/bin/pip" install --quiet 'PyYAML==6.0.2' 'jsonschema==4.23.0' \
  || { echo "pip install failed"; exit 1; }

echo "==> Fetching Docker Engine API v1.47 swagger.yaml"
if [[ ! -s "$SWAGGER_CACHE" ]]; then
  # Verified working (2026-08-21): docs.docker.com serves the exact v1.47 spec directly.
  if ! curl -fsSL --max-time 20 \
      https://docs.docker.com/reference/api/engine/version/v1.47.yaml \
      -o "$SWAGGER_CACHE"; then
    echo "primary URL failed, falling back to moby raw (note: tag content may not say '1.47' but shapes are equivalent)"
    curl -fsSL --max-time 20 \
      https://raw.githubusercontent.com/moby/moby/v27.5.1/api/swagger.yaml \
      -o "$SWAGGER_CACHE" || { echo "both swagger.yaml sources failed"; exit 1; }
  fi
fi
if ! grep -q "^swagger:" "$SWAGGER_CACHE"; then
  echo "downloaded file at $SWAGGER_CACHE does not look like a swagger spec"; exit 1
fi

echo "==> Starting daemon"
start_daemon || exit 1
snapshot_images
trap 'cleanup_new_images; stop_daemon' EXIT

echo "==> Running schema validation"
"$VENV/bin/python3" lib/swagger_check.py \
  --swagger "$SWAGGER_CACHE" \
  --socket "$CIDER_COMPAT_SOCKET" \
  --report "$REPORT"
rc=$?

echo "==> Report: $REPORT"
exit $rc
