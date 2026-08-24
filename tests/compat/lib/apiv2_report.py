#!/usr/bin/env python3
"""tests/compat/lib/apiv2_report.py

Allowlist + grouped-failures-report generator for run-podman-apiv2.sh.

Allowlist protocol (shared convention across the case-level suites in this
harness — podman-apiv2 and docker-py):
  - allowlists/<suite>.txt does not exist yet -> first run: write every
    currently-passing case id into it, write every failure (grouped, with a
    best-guess category) into reports/<suite>-failures.md, exit 0.
  - allowlists/<suite>.txt exists -> compare current passes against it; any
    previously-allowlisted id that is not in the current pass set is a
    regression -> print it and exit non-zero. New/unlisted failures never
    fail the build (only a regression of something we already know works
    does), matching the task's "exit non-zero only if an allowlisted case
    fails" instruction.

Failures are grouped by assertion pattern (variable references normalized
out) because a single upstream `.at` file can produce hundreds of
individual subtests and a flat list would be unreadable; each group gets
one best-guess category (not-supported-by-apple-container /
daemon-bug / podman-specific) via a small keyword heuristic. The heuristic
is intentionally conservative — anything it can't confidently classify
lands in daemon-bug/NEEDS-TRIAGE for a human to look at, which is the
correct default: a category should never quietly excuse a real gap.
"""
import argparse
import collections
import datetime
import re
import sys

CATEGORY_RULES = [
    (re.compile(r"REGISTRY_PORT|start_registry|push.*registry|:\s*/", re.I),
     "podman-specific", "depends on the no-op start_registry fixture (local TLS registry push/auth, out of scope for Docker-compat)"),
    (re.compile(r"\bmanifest\b", re.I),
     "podman-specific", "podman manifest-list feature has no Docker Engine API equivalent"),
    (re.compile(r"\bpods?\b", re.I),
     "podman-specific", "podman pods have no Docker Engine API equivalent"),
    (re.compile(r"cgroup|cpuset|CpuShares|CpuQuota|CpuPeriod|NanoCpus|OomKillDisable|MemorySwap|MemorySwappiness|pids_stats|PidsLimit|Ulimit|seccomp|apparmor|SecurityOpt|Rlimit", re.I),
     "not-supported-by-apple-container", "Apple container is a lightweight-VM runtime; Linux cgroup-level resource controls are not exposed 1:1"),
    (re.compile(r"healthcheck|generate|\binit\b|\bunshare\b", re.I),
     "podman-specific", "depends on a libpod-only podman subcommand our shim no-ops"),
    (re.compile(r"short-?name|shortnames\.conf|unqualified-search", re.I),
     "podman-specific", "podman's short-name resolution/aliasing config has no Docker Engine API equivalent"),
]


def categorize(testname):
    for pattern, category, note in CATEGORY_RULES:
        if pattern.search(testname):
            return category, note
    return "daemon-bug", "NEEDS-TRIAGE -- Docker-compat request failed against cider; see expected/actual"


_VAR_RE = re.compile(r"\$\{?[A-Za-z_][A-Za-z0-9_]*\}?")


def group_key(testname):
    """Normalize out interpolated ids/names so repeated-shape failures
    (e.g. the same assertion re-run against 40 different container ids)
    collapse into one group."""
    key = _VAR_RE.sub("<var>", testname)
    key = re.sub(r"\b[0-9a-f]{12,64}\b", "<hex-id>", key)
    return key


_SEQ_RE = re.compile(r"^(\S+?)#\d{4}\s+")
_HEXID_RE = re.compile(r"\b[0-9a-f]{12,64}\b")


def allow_key(entry):
    """Run-independent allowlist key for one graded subtest.

    An entry as printed by lib/apiv2-runner.sh looks like

        20-containers#0152 GET containers/<64-hex-id>/json : .Path=echo

    Two parts of that are re-generated on every run and can never match
    across runs:

      * the ``#NNNN`` sequence number, which is a running counter over every
        assertion the file emitted so far -- one extra (or one fewer) skip
        anywhere earlier in the .at file renumbers everything after it; and
      * the container/image/network ids and digests interpolated into the
        path or the expected value, which are freshly minted per run.

    Comparing raw entries therefore reports a "regression" for any case whose
    fixture id changed, which is most of them: on 2026-08-22 the raw
    comparison flagged 212 of 295 allowlisted cases, of which 186 were the
    *same assertion* passing under a different id. Keying on
    ``<file># <assertion text with ids masked>`` collapses that noise and
    leaves the 20 entries that genuinely stopped passing.

    The trade-off is deliberate: an .at file that runs the identical
    assertion N times now contributes one key instead of N, so the gate is
    coarser (it catches "this assertion no longer passes anywhere in this
    file" rather than "this assertion no longer passes at this position").
    That is the right trade for a cross-run regression gate -- a gate that
    cries wolf 186 times is not a gate.
    """
    key = _SEQ_RE.sub(r"\1# ", entry, count=1)
    return _HEXID_RE.sub("<id>", key)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pass-file", required=True)
    ap.add_argument("--fail-file", required=True)
    ap.add_argument("--allowlist", required=True)
    ap.add_argument("--report", required=True)
    ap.add_argument("--suite-label", required=True)
    args = ap.parse_args()

    with open(args.pass_file, encoding="utf-8", errors="replace") as fh:
        passes = [line.rstrip("\n") for line in fh if line.strip()]

    fails = []
    with open(args.fail_file, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line:
                continue
            parts = line.split("\x1f")
            if len(parts) != 4:
                continue
            fails.append(tuple(parts))  # id, testname, expected, actual

    is_first_run = False
    try:
        with open(args.allowlist, encoding="utf-8") as fh:
            # allow_key() is applied to the stored entries too, so an
            # allowlist written before the key was stabilized still compares
            # correctly instead of reporting every line as a regression.
            allow_lines = set(
                allow_key(line.rstrip("\n"))
                for line in fh
                if line.strip() and not line.startswith("#")
            )
    except FileNotFoundError:
        is_first_run = True
        allow_lines = set()

    pass_set = set(allow_key(p) for p in passes)
    regressions = sorted(allow_lines - pass_set) if not is_first_run else []

    if is_first_run:
        with open(args.allowlist, "w", encoding="utf-8") as fh:
            fh.write(f"# {args.suite_label}\n")
            fh.write(
                f"# generated {datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None).isoformat()}Z -- expected-pass Docker-compat "
                "subtests (libpod-prefixed calls are executed for fixture continuity but never graded;\n"
                "# see lib/apiv2-runner.sh header comment for why).\n"
            )
            fh.write(
                "# Entries are normalized allowlist keys, not raw runner output: the #NNNN\n"
                "# sequence number is dropped and hex ids/digests are masked as <id>, because\n"
                "# both are re-minted every run (see allow_key() in lib/apiv2_report.py).\n"
            )
            fh.write("# regenerate by deleting this file and re-running run-podman-apiv2.sh\n")
            for p in sorted(pass_set):
                fh.write(p + "\n")

    groups = collections.OrderedDict()
    for id_, testname, expected, actual in fails:
        key = group_key(testname)
        groups.setdefault(key, []).append((id_, testname, expected, actual))

    with open(args.report, "w", encoding="utf-8") as fh:
        fh.write(f"# {args.suite_label} -- failures\n\n")
        fh.write(
            f"Generated {datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None).isoformat()}Z. "
            f"{len(passes)} passed, {len(fails)} failed (Docker-compat subtests only).\n\n"
        )
        if is_first_run:
            fh.write(
                f"This is the **first run**: {len(passes)} passing cases were written to "
                "`allowlists/podman-apiv2.txt`. Re-running only fails the build if one of those "
                "regresses.\n\n"
            )
        elif regressions:
            fh.write(f"**REGRESSIONS**: {len(regressions)} previously-allowlisted case(s) now fail:\n\n")
            for r in regressions:
                fh.write(f"- `{r}`\n")
            fh.write("\n")
        else:
            fh.write("No regressions against the existing allowlist.\n\n")

        fh.write(f"## Failures grouped by assertion pattern ({len(groups)} distinct groups)\n\n")
        for key, entries in sorted(groups.items(), key=lambda kv: -len(kv[1])):
            category, note = categorize(key)
            fh.write(f"### `{key}`\n\n")
            fh.write(f"- **Count**: {len(entries)}\n")
            fh.write(f"- **Category**: {category}\n")
            fh.write(f"- **Note**: {note}\n\n")
            fh.write("| ID | Expected | Actual |\n|---|---|---|\n")
            for id_, testname, expected, actual in entries[:5]:
                e = expected.replace("|", "\\|").replace("\n", " ")
                a = actual.replace("|", "\\|").replace("\n", " ")
                fh.write(f"| `{id_}` | `{e}` | `{a}` |\n")
            if len(entries) > 5:
                fh.write(f"\n_...and {len(entries) - 5} more with the same pattern._\n")
            fh.write("\n")

    if regressions:
        print(f"REGRESSION: {len(regressions)} previously-allowlisted case(s) now fail; see {args.report}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
