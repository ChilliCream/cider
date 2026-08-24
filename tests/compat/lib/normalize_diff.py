#!/usr/bin/env python3
"""
tests/compat/lib/normalize_diff.py

Shape-diff two directories of raw JSON captures (OrbStack vs cider)
produced by diff-vs-orbstack.sh, and write a prioritized markdown report of
fields OrbStack's (reference) output has that cider's is missing,
null, empty, or a different JSON type for.

Deliberately does NOT try to normalize/blank "volatile" fields (ids,
timestamps, random ports, MAC addresses, etc.) by enumerating their key
names — that list is never complete and rots. Instead it diffs *shape*:
presence, non-nullness/non-emptiness, and JSON type at each path. A field
that exists with a plausible value on both sides is left alone even if the
literal values differ (that's expected — different container IDs, ports,
timestamps are not bugs).

Usage: normalize_diff.py <orbstack_raw_dir> <cider_raw_dir> <report.md>
Exit code: 0 on success (even if findings are non-empty — this is a report,
not a pass/fail gate), 1 if a required capture file is missing/unparseable.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

_MISSING = object()


def type_name(v: object) -> str:
    if v is _MISSING:
        return "absent"
    if v is None:
        return "null"
    if isinstance(v, bool):
        return "bool"
    if isinstance(v, (int, float)):
        return "number"
    if isinstance(v, str):
        return "string"
    if isinstance(v, list):
        return "array"
    if isinstance(v, dict):
        return "object"
    return type(v).__name__


def summarize(v: object, limit: int = 80) -> str:
    s = json.dumps(v) if not isinstance(v, str) else v
    s = str(s)
    return s if len(s) <= limit else s[: limit - 1] + "…"


def looks_like_id(key: str) -> bool:
    """Container/network/image ids: long lowercase-hex strings. Distinguishes
    dynamic ID-keyed maps (`Containers{<id>: ...}`) from named-key maps
    (`Networks{"bridge": ...}`, `Ports{"80/tcp": ...}`) that should be
    compared key-by-key."""
    return len(key) >= 12 and all(c in "0123456789abcdef" for c in key.lower())


def compare(path: str, ref: object, other: object, findings: list[tuple[str, str, str]]) -> None:
    """Walk `ref` (the reference/OrbStack side); flag what's wrong on `other`."""
    if ref is None or ref == "":
        return  # nothing meaningful required here

    if isinstance(ref, dict):
        if not isinstance(other, dict):
            findings.append((path, "TYPE", f"orbstack=object, cider={type_name(other)}"))
            return
        if not ref:
            return
        # Dicts keyed by randomly-generated ids (docker/cider will
        # never agree on a literal id) get shape-sampled instead of compared
        # key-by-key, which would otherwise report every key as "missing"
        # purely because the ids differ between engines.
        if all(looks_like_id(k) for k in ref.keys()):
            if not other:
                findings.append((path + ".*", "EMPTY", f"orbstack has {len(ref)} entries, cider has 0"))
            else:
                r_sample = next(iter(ref.values()))
                o_sample = next(iter(other.values()))
                compare(f"{path}.<entry>", r_sample, o_sample, findings)
        else:
            for k, rv in ref.items():
                ov = other.get(k, _MISSING)
                if ov is _MISSING:
                    findings.append((f"{path}.{k}", "MISSING", f"orbstack={summarize(rv)}"))
                else:
                    compare(f"{path}.{k}", rv, ov, findings)
        return

    if isinstance(ref, list):
        if not isinstance(other, list):
            findings.append((path, "TYPE", f"orbstack=array, cider={type_name(other)}"))
            return
        if not ref:
            return
        if not other:
            findings.append((path, "EMPTY", f"orbstack has {len(ref)} item(s), cider has 0"))
            return
        compare(f"{path}[0]", ref[0], other[0], findings)
        return

    # scalar leaf
    if other is _MISSING or other is None:
        findings.append((path, "MISSING/NULL", f"orbstack={summarize(ref)!r}"))
    elif other == "" and ref != "":
        findings.append((path, "EMPTY", f"orbstack={summarize(ref)!r}"))
    elif type(ref) is not type(other) and not (
        isinstance(ref, (int, float)) and isinstance(other, (int, float)) and not isinstance(ref, bool) and not isinstance(other, bool)
    ):
        findings.append((path, "TYPE", f"orbstack={type_name(ref)}({summarize(ref)!r}) cider={type_name(other)}({summarize(other)!r})"))


PRIORITY_HINTS = [
    "State", "NetworkSettings", "HostConfig.PortBindings", "Mounts", "Health",
    "Config", "Networks", "IPAM", "Ports", "Labels",
]


def priority(path: str) -> int:
    for i, hint in enumerate(PRIORITY_HINTS):
        if hint in path:
            return i
    return len(PRIORITY_HINTS)


def load(path: Path) -> object:
    return json.loads(path.read_text())


def unwrap_single(obj: object) -> object:
    """`docker inspect` returns a JSON array with one element."""
    if isinstance(obj, list) and len(obj) == 1:
        return obj[0]
    return obj


def pick_ps_entry(ps_array: list, name: str) -> dict:
    for entry in ps_array:
        if entry.get("Names") == name or (isinstance(entry.get("Names"), list) and name in entry.get("Names", [])):
            return entry
    return {}


SCENARIOS: list[tuple[str, str, str, bool | str]] = [
    ("inspect", "container inspect (`docker inspect compat-diff`)", "inspect.json", True),
    ("version", "`docker version --format '{{json .}}'`", "version.json", False),
    ("info", "`docker info --format '{{json .}}'`", "info.json", False),
    ("ps", "`docker ps -a` entry for compat-diff", "ps.json", "ps"),
    ("network", "`docker network inspect bridge`", "network-bridge.json", True),
    ("volume", "`docker volume inspect compat-v`", "volume-inspect.json", True),
]


def main() -> int:
    if len(sys.argv) != 4:
        print(f"usage: {sys.argv[0]} <orbstack_raw_dir> <cider_raw_dir> <report.md>", file=sys.stderr)
        return 2

    orb_dir, ad_dir, report_path = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])

    sections: list[tuple[str, list[tuple[str, str, str]], list[tuple[str, str, str]]]] = []
    total_missing = 0
    errors: list[str] = []

    for key, label, filename, mode in SCENARIOS:
        orb_file, ad_file = orb_dir / filename, ad_dir / filename
        if not orb_file.exists() or not ad_file.exists():
            errors.append(f"{label}: missing capture file(s) ({filename})")
            continue
        try:
            orb_raw, ad_raw = load(orb_file), load(ad_file)
        except json.JSONDecodeError as e:
            errors.append(f"{label}: JSON parse error ({e})")
            continue

        if mode == "ps":
            orb_obj = pick_ps_entry(orb_raw, "compat-diff")
            ad_obj = pick_ps_entry(ad_raw, "compat-diff")
            if not orb_obj:
                errors.append(f"{label}: OrbStack `ps -a` did not list compat-diff")
                continue
        elif mode:
            orb_obj, ad_obj = unwrap_single(orb_raw), unwrap_single(ad_raw)
        else:
            orb_obj, ad_obj = orb_raw, ad_raw

        missing_findings: list[tuple[str, str, str]] = []
        compare(key, orb_obj, ad_obj, missing_findings)
        missing_findings.sort(key=lambda f: (priority(f[0]), f[0]))

        extra_findings: list[tuple[str, str, str]] = []
        compare(key, ad_obj, orb_obj, extra_findings)
        extra_findings = [f for f in extra_findings if f[1] == "MISSING/NULL"]
        extra_findings.sort(key=lambda f: (priority(f[0]), f[0]))

        total_missing += len(missing_findings)
        sections.append((label, missing_findings, extra_findings))

    lines = [
        "<!-- title: OrbStack Diff -->",
        "# cider vs OrbStack — differential compatibility report",
        "",
        "Reference: a real dockerd (OrbStack, `docker context orbstack`), NOT vendored — used only as a",
        "live comparison target. Scenario: `alpine:3.22` container `compat-diff` (`-p 0:80 -e A=1 -l x=y`),",
        "volume `compat-v`, network `bridge`, run identically against both engines.",
        "",
        "This is a **shape diff**, not a value diff: ids, timestamps, ports, MAC addresses etc. are expected",
        "to differ and are not flagged. A finding means a field OrbStack populated is **missing, null, empty,",
        "or a different JSON type** on cider's side — each is a concrete, actionable gap.",
        "",
        f"**{total_missing} field(s) differ from the OrbStack reference across {len(sections)} scenario(s).**",
        "",
    ]

    if errors:
        lines += ["## Capture errors", ""]
        lines += [f"- {e}" for e in errors]
        lines += [""]

    for label, missing, extra in sections:
        lines.append(f"## {label}")
        lines.append("")
        if not missing:
            lines.append("No shape differences found. ✅")
        else:
            lines.append("| Path | Issue | Detail |")
            lines.append("|---|---|---|")
            for path, kind, detail in missing:
                detail_md = detail.replace("|", "\\|")
                lines.append(f"| `{path}` | {kind} | {detail_md} |")
        lines.append("")
        if extra:
            lines.append("<details><summary>Informational: fields cider has that OrbStack doesn't</summary>")
            lines.append("")
            for path, _kind, detail in extra:
                detail_md = detail.replace("|", "\\|")
                lines.append(f"- `{path}` — {detail_md}")
            lines.append("")
            lines.append("</details>")
            lines.append("")

    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text("\n".join(lines) + "\n")

    print(f"{total_missing} field(s) differ from the OrbStack reference across {len(sections)} scenario(s).")
    if errors:
        print(f"{len(errors)} capture error(s); see report.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
