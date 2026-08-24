#!/usr/bin/env bash
# tests/compat/run-docker-py.sh
#
# Runs a curated subset of docker-py's `tests/integration` suite (Apache-2.0,
# https://github.com/docker/docker-py) against our socket, and checks the
# result against tests/compat/allowlists/docker-py.txt.
#
# docker-py is fetched at a pinned tag at run time (never vendored) into
# /tmp/cider-compat-docker-py-src. A dedicated venv at /tmp/cider-compat-venv holds
# pinned `docker` and `pytest` versions.
#
# Exit code: non-zero only if a case listed in allowlists/docker-py.txt
# regresses to failing. New/unlisted failures do not fail the script (they
# land in reports/docker-py-failures.md for triage) — see the allowlist
# protocol comment near the bottom of this file.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/daemon.sh
source "${SCRIPT_DIR}/lib/daemon.sh"

DOCKER_PY_TAG="7.1.0"
DOCKER_PY_PIN="docker[ssh]==7.1.0"  # [ssh] extra pulls in paramiko, which tests/helpers.py imports unconditionally
PARAMIKO_PIN="paramiko==5.0.0"
PYTEST_PIN="pytest==7.4.2"
PYTEST_TIMEOUT_PIN="pytest-timeout==2.1.0"

VENV_DIR="/tmp/cider-compat-venv"
SRC_DIR="/tmp/cider-compat-docker-py-src"
JUNIT_XML="${SCRIPT_DIR}/reports/docker-py-junit.xml"
ALLOWLIST="${SCRIPT_DIR}/allowlists/docker-py.txt"
FAILURES_MD="${SCRIPT_DIR}/reports/docker-py-failures.generated.md"

mkdir -p "${SCRIPT_DIR}/reports" "${SCRIPT_DIR}/allowlists"

log() { echo "[run-docker-py] $*" >&2; }

# ---------------------------------------------------------------------------
# 1. venv + pinned install
# ---------------------------------------------------------------------------
if [[ ! -x "${VENV_DIR}/bin/python" ]]; then
  log "creating venv at ${VENV_DIR}"
  python3 -m venv "${VENV_DIR}" || { log "venv creation failed"; exit 1; }
fi

log "installing pinned deps (${DOCKER_PY_PIN}, ${PARAMIKO_PIN}, ${PYTEST_PIN}, ${PYTEST_TIMEOUT_PIN})"
# NOTE: docker-py 7.1.0's own dev pins (pytest==7.4.2 etc, see its
# pyproject.toml [project.optional-dependencies].dev) install cleanly as-is
# on this machine's Python 3.14 runtime — no relaxation needed. The one
# addition beyond the task's literal "docker==7.1.0, pytest" pins:
# tests/helpers.py unconditionally `import paramiko`, which is only pulled
# in via docker-py's optional `[ssh]` extra, so we install that extra and
# pin the paramiko version it resolved to for reproducibility.
if ! "${VENV_DIR}/bin/pip" install -q --upgrade pip >/dev/null; then
  log "pip self-upgrade failed (continuing with existing pip)"
fi
if ! "${VENV_DIR}/bin/pip" install -q "${DOCKER_PY_PIN}" "${PARAMIKO_PIN}" "${PYTEST_PIN}" "${PYTEST_TIMEOUT_PIN}"; then
  log "pip install failed"
  exit 1
fi

# ---------------------------------------------------------------------------
# 2. fetch docker-py at the pinned tag (sparse not needed, repo is small)
# ---------------------------------------------------------------------------
if [[ -d "${SRC_DIR}/.git" ]]; then
  current_tag="$(git -C "${SRC_DIR}" describe --tags --exact-match 2>/dev/null || true)"
  if [[ "${current_tag}" != "${DOCKER_PY_TAG}" ]]; then
    log "existing clone at ${SRC_DIR} is not tag ${DOCKER_PY_TAG} (got '${current_tag}'); re-cloning"
    rm -rf "${SRC_DIR}"
  fi
fi
if [[ ! -d "${SRC_DIR}" ]]; then
  log "cloning docker-py @ ${DOCKER_PY_TAG} into ${SRC_DIR}"
  git clone --depth 1 --branch "${DOCKER_PY_TAG}" https://github.com/docker/docker-py "${SRC_DIR}" \
    || { log "clone failed"; exit 1; }
fi

# ---------------------------------------------------------------------------
# 3. curated subset
# ---------------------------------------------------------------------------
# Picked to cover: containers basic, exec, images pull/inspect/tag/remove,
# networks, volumes, api_* tests that don't need swarm/plugins/secrets.
#
# api_container_test.py is the big one (85 test methods across ~15 unittest
# classes: list/create/start/stop/kill/restart/rename/logs/diff/port/top/
# pause/attach/stats/wait/archive/remove/prune, plus a VolumeBindTest). We
# keep the whole file rather than hand-picking classes, but bound worst-case
# per-test time with --timeout so one hung attach/stats stream can't blow
# the ~10 minute budget for the whole run. api_network_test.py's TestNetworks
# has a `tearDown` that unconditionally calls `leave_swarm(force=True)` after
# *every* test (not just swarm-scoped ones) — if cider doesn't
# implement /swarm/leave, expect that to show up as a teardown error on most
# or all TestNetworks node ids even when the test body itself passed. That's
# a single systemic issue, not N separate bugs; call it out once in triage
# rather than filing each node id separately.
TEST_TARGETS=(
  "tests/integration/api_container_test.py"
  "tests/integration/api_image_test.py"
  "tests/integration/api_network_test.py"
  "tests/integration/api_volume_test.py"
  "tests/integration/api_exec_test.py"
)
KEXPR="not swarm and not plugin and not secret and not service and not config"

log "running: pytest ${TEST_TARGETS[*]} -k \"${KEXPR}\""

# ---------------------------------------------------------------------------
# 4. start daemon, run, stop daemon
# ---------------------------------------------------------------------------
start_daemon || exit 1
trap stop_daemon EXIT

pushd "${SRC_DIR}" >/dev/null

"${VENV_DIR}/bin/python" -m pytest "${TEST_TARGETS[@]}" \
  -k "${KEXPR}" \
  -p no:cacheprovider \
  --timeout=45 \
  --junitxml="${JUNIT_XML}" \
  -v \
  2>&1 | tee "${SCRIPT_DIR}/reports/docker-py-run.log"
PYTEST_STATUS=${PIPESTATUS[0]}

popd >/dev/null

log "pytest exited ${PYTEST_STATUS} (non-zero just means >=1 test failed; junit has the detail)"

# ---------------------------------------------------------------------------
# 5. parse junit xml -> pass/fail sets
# ---------------------------------------------------------------------------
RESULTS_JSON="/tmp/cider-compat-docker-py-results.json"
"${VENV_DIR}/bin/python" - "$JUNIT_XML" "$RESULTS_JSON" <<'PYEOF'
import sys
import json
import xml.etree.ElementTree as ET

junit_path, out_path = sys.argv[1], sys.argv[2]
tree = ET.parse(junit_path)
root = tree.getroot()

suites = [root] if root.tag == "testsuite" else list(root.iter("testsuite"))

passed, failed = [], []
for suite in suites:
    for tc in suite.iter("testcase"):
        classname = tc.get("classname", "")
        name = tc.get("name", "")
        file_attr = tc.get("file")
        # Reconstruct a pytest-style node id: <file>::<Class>::<method>
        if file_attr:
            path = file_attr
        else:
            # classname is usually "tests.integration.api_container_test.ListContainersTest"
            parts = classname.split(".")
            path = "/".join(parts[:-1]) + ".py" if len(parts) > 1 else classname
            classname = parts[-1] if parts else classname
        cls = classname.split(".")[-1] if "." in classname else classname
        node_id = f"{path}::{cls}::{name}" if cls else f"{path}::{name}"

        bad = tc.find("failure")
        err = tc.find("error")
        skipped = tc.find("skipped")
        if skipped is not None:
            continue
        if bad is not None or err is not None:
            reason = (bad.get("message") if bad is not None else err.get("message")) or ""
            failed.append({"id": node_id, "reason": reason[:300]})
        else:
            passed.append(node_id)

with open(out_path, "w") as f:
    json.dump({"passed": sorted(passed), "failed": failed}, f, indent=2)

print(f"parsed junit: {len(passed)} passed, {len(failed)} failed", file=sys.stderr)
PYEOF

if [[ ! -f "$RESULTS_JSON" ]]; then
  log "failed to parse junit xml at ${JUNIT_XML}; leaving allowlist/report untouched"
  exit 1
fi

# ---------------------------------------------------------------------------
# 6. allowlist protocol (shared across compat suites in this harness):
#    - allowlists/docker-py.txt: one passing node id per line, '#' comments ok
#    - if the allowlist doesn't exist yet: first run. Write every currently
#      passing id into it, write every failing id (+ best-guess category)
#      into reports/docker-py-failures.md, exit 0.
#    - if it exists: any allowlisted id that now FAILS is a regression ->
#      print it and exit non-zero. New passes/failures outside the allowlist
#      are informational only and don't affect the exit code.
# ---------------------------------------------------------------------------
"${VENV_DIR}/bin/python" - "$RESULTS_JSON" "$ALLOWLIST" "$FAILURES_MD" "$DOCKER_PY_TAG" <<'PYEOF'
import sys
import json
import datetime

results_path, allowlist_path, failures_path, tag = sys.argv[1:5]

with open(results_path) as f:
    results = json.load(f)
passed = set(results["passed"])
failed = results["failed"]  # list of {"id", "reason"}

def categorize(node_id: str, reason: str) -> str:
    r = reason.lower()
    nid = node_id.lower()
    if any(k in nid or k in r for k in ("swarm", "plugin", "secret", "service", "config")):
        return "not-supported-by-apple-container"
    if "leave_swarm" in r or "/swarm/leave" in r:
        return "not-supported-by-apple-container"
    return "daemon-bug"  # NEEDS-TRIAGE default; a human should confirm/reclassify

import os
first_run = not os.path.exists(allowlist_path)

if first_run:
    with open(allowlist_path, "w") as f:
        f.write(f"# tests/compat/allowlists/docker-py.txt\n")
        f.write(f"# Generated {datetime.date.today().isoformat()} from docker-py tag {tag} against cider.\n")
        f.write(f"# One pytest node id per line = expected to keep passing.\n")
        for node_id in sorted(passed):
            f.write(node_id + "\n")
    print(f"[run-docker-py] first run: wrote {len(passed)} passing ids to allowlist", file=sys.stderr)
    regressions = []
else:
    with open(allowlist_path) as f:
        allowlisted = {
            line.strip() for line in f
            if line.strip() and not line.strip().startswith("#")
        }
    failed_ids = {item["id"] for item in failed}
    regressions = sorted(allowlisted & failed_ids)
    if regressions:
        print("[run-docker-py] REGRESSIONS (allowlisted but now failing):", file=sys.stderr)
        for r in regressions:
            print(f"  - {r}", file=sys.stderr)

with open(failures_path, "w") as f:
    f.write("# docker-py compat failures\n\n")
    f.write(f"docker-py tag: `{tag}` · generated {datetime.date.today().isoformat()}\n\n")
    f.write("Categories are a first-pass heuristic (`daemon-bug` is the default) — triage and correct by hand.\n\n")
    f.write("| Test ID | Category | Notes |\n")
    f.write("|---|---|---|\n")
    for item in sorted(failed, key=lambda x: x["id"]):
        cat = categorize(item["id"], item["reason"])
        note = item["reason"].replace("|", "\\|").replace("\n", " ")
        f.write(f"| `{item['id']}` | {cat} | {note} |\n")

print(f"docker-py: {len(passed)} passed, {len(failed)} failed, "
      f"{'first run' if first_run else 'regression-checked'}", file=sys.stderr)

sys.exit(1 if regressions else 0)
PYEOF
ALLOWLIST_STATUS=$?

exit $ALLOWLIST_STATUS
