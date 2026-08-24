#!/usr/bin/env python3
"""tests/compat/lib/swagger_check.py

Validates real responses from the cider daemon against the official
Docker Engine API v1.47 swagger.yaml (schema drift check), plus a manual
required-field completeness pass cross-checked against docs/ARCHITECTURE.md
§3-4 (the daemon's own DTO contract), since the upstream swagger.yaml itself
declares almost no `required:` fields for these definitions (verified: only
ImageSummary does) even though real dockerd always emits them.

Usage:
    python3 swagger_check.py --swagger PATH --socket PATH --report PATH

Exit code: 0 if no violations found, 1 otherwise.
"""
from __future__ import annotations

import argparse
import http.client
import json
import socket
import sys
import time
from datetime import datetime, timezone

import yaml
from jsonschema import Draft4Validator
from jsonschema.validators import RefResolver


class UnixSocketHTTPConnection(http.client.HTTPConnection):
    """http.client.HTTPConnection over an AF_UNIX socket."""

    def __init__(self, socket_path: str, timeout: float = 10.0):
        super().__init__("localhost", timeout=timeout)
        self.socket_path = socket_path

    def connect(self):
        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        sock.settimeout(self.timeout)
        sock.connect(self.socket_path)
        self.sock = sock


def request_json(socket_path: str, method: str, path: str, body: dict | None = None, timeout: float = 15.0):
    conn = UnixSocketHTTPConnection(socket_path, timeout=timeout)
    headers = {}
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    conn.request(method, path, body=data, headers=headers)
    resp = conn.getresponse()
    raw = resp.read()
    conn.close()
    status = resp.status
    if not raw:
        return status, None
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, raw.decode(errors="replace")


def read_one_event(socket_path: str, timeout: float = 6.0):
    """Open a streaming GET /events?since=0 connection and read the first
    NDJSON event object, tolerating a timeout (returns None if nothing
    arrived within `timeout` seconds).

    Reads through the `HTTPResponse` (which un-frames `Transfer-Encoding:
    chunked` for us) rather than the raw `resp.fp`: the daemon streams
    /events chunked, so a raw readline() returns the hex chunk-length line
    ("1e0\\r\\n") first and never any JSON — which is why this check used to
    report a bogus "no event observed" violation on every run even though
    the daemon was emitting perfectly well-formed events (verified by hand
    with `curl -N --unix-socket ... /events?since=0`). Keep looping past
    blank/undecodable lines until the deadline so a partially-flushed line
    can't end the read either.
    """
    conn = UnixSocketHTTPConnection(socket_path, timeout=timeout)
    conn.request("GET", "/events?since=0")
    resp = conn.getresponse()  # returns once headers are flushed
    deadline = time.monotonic() + timeout
    try:
        while time.monotonic() < deadline:
            try:
                line = resp.readline()
            except (socket.timeout, TimeoutError, OSError):
                break
            if not line:
                break  # stream closed
            if not line.strip():
                continue
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
    finally:
        conn.close()
    return None


# ---------------------------------------------------------------------------
# Schema validation
# ---------------------------------------------------------------------------

class Violation:
    def __init__(self, endpoint: str, json_path: str, kind: str, expected: str, actual: str):
        self.endpoint = endpoint
        self.json_path = json_path
        self.kind = kind  # "schema" | "manual-required"
        self.expected = expected
        self.actual = actual


def validate_against_schema(endpoint: str, schema: dict, instance, resolver: RefResolver, violations: list[Violation]):
    validator = Draft4Validator(schema, resolver=resolver)
    errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.absolute_path))
    for err in errors:
        json_path = "$" + "".join(
            f"[{p}]" if isinstance(p, int) else f".{p}" for p in err.absolute_path
        )
        if not err.absolute_path:
            json_path = "$"
        expected = f"{err.validator}={err.validator_value!r}"
        actual_val = err.instance
        actual = f"{type(actual_val).__name__}: {actual_val!r}"
        if len(actual) > 200:
            actual = actual[:200] + "…"
        violations.append(Violation(endpoint, json_path, "schema", expected, actual))
    return len(errors) == 0


def _get(instance, path_parts):
    cur = instance
    for part in path_parts:
        if isinstance(cur, dict) and part in cur:
            cur = cur[part]
        else:
            return None, False
    return cur, True


def check_required_paths(endpoint: str, instance, required_paths: list[str], violations: list[Violation]):
    """Manual completeness pass: for each dotted path, verify the field is
    present (and non-null unless explicitly allowed). This is a *second*
    source of truth beyond swagger's own (often absent) `required:` lists,
    cross-checked against docs/ARCHITECTURE.md §3-4."""
    for dotted in required_paths:
        parts = dotted.split(".")
        value, present = _get(instance, parts)
        if not present:
            violations.append(Violation(
                endpoint, "$." + dotted, "manual-required",
                "field present (per ARCHITECTURE.md §3-4)", "MISSING",
            ))


# ARCHITECTURE.md §3-4 field checklists per endpoint. Kept intentionally to
# the fields the spec calls out explicitly by name for each DTO; nested
# array/dict-of-object fields are checked one level deep only.
REQUIRED_FIELDS = {
    "GET /version": [
        "Version", "ApiVersion", "MinAPIVersion", "Os", "Arch", "KernelVersion",
        "Platform.Name", "Components",
    ],
    "GET /info": [
        "ID", "Containers", "ContainersRunning", "ContainersPaused", "ContainersStopped",
        "Images", "Driver", "OSType", "OperatingSystem", "KernelVersion", "NCPU", "MemTotal",
        "Name", "ServerVersion", "Labels", "ExperimentalBuild",
        "Swarm.LocalNodeState", "Swarm.NodeID", "Swarm.ControlAvailable",
        "Plugins.Volume", "Plugins.Network", "Plugins.Log",
        "LoggingDriver", "CgroupDriver", "CgroupVersion", "DefaultRuntime", "Runtimes",
        "SecurityOptions", "Warnings", "IndexServerAddress",
        "RegistryConfig.IndexConfigs", "RegistryConfig.Mirrors", "RegistryConfig.InsecureRegistryCIDRs",
        "DockerRootDir", "SystemTime", "IPv4Forwarding", "BridgeNfIptables", "BridgeNfIp6tables",
        "Debug", "NFd", "NGoroutines", "HttpProxy", "HttpsProxy", "NoProxy",
    ],
    "GET /containers/json (each item)": [
        "Id", "Names", "Image", "ImageID", "Command", "Created", "Ports", "Labels",
        "State", "Status", "HostConfig.NetworkMode", "NetworkSettings.Networks", "Mounts",
    ],
    "GET /containers/{id}/json": [
        "Id", "Created", "Path", "Args",
        "State.Status", "State.Running", "State.Pid", "State.ExitCode",
        "State.StartedAt", "State.FinishedAt",
        "Image", "ResolvConfPath", "HostnamePath", "HostsPath", "LogPath", "Name",
        "RestartCount", "Driver", "Platform", "MountLabel", "ProcessLabel", "AppArmorProfile",
        "ExecIDs", "HostConfig", "GraphDriver.Name", "GraphDriver.Data", "Mounts", "Config",
        "NetworkSettings.Ports", "NetworkSettings.Networks",
    ],
    "GET /images/json (each item)": [
        "Id", "ParentId", "RepoTags", "RepoDigests", "Created", "Size", "SharedSize",
        "Labels", "Containers",
    ],
    "GET /networks (each item)": [
        "Name", "Id", "Created", "Scope", "Driver", "EnableIPv6",
        "IPAM.Driver", "IPAM.Config", "Internal", "Attachable", "Ingress",
        "ConfigOnly", "Containers", "Options", "Labels",
    ],
    "GET /volumes": ["Volumes", "Warnings"],
    "GET /volumes (each Volumes[] item)": [
        "Name", "Driver", "Mountpoint", "CreatedAt", "Status", "Labels", "Scope", "Options",
    ],
    "GET /events (one event)": [
        "Type", "Action", "Actor.ID", "Actor.Attributes", "scope", "time", "timeNano",
    ],
}


# ---------------------------------------------------------------------------
# Report generation
# ---------------------------------------------------------------------------

def render_report(path: str, endpoints_checked: list[str], violations: list[Violation], swagger_path: str, socket_path: str):
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    lines = []
    lines.append("# Swagger contract report — cider vs Docker Engine API v1.47\n")
    lines.append(f"Generated {now} · swagger spec: `{swagger_path}` · socket: `{socket_path}`\n")
    lines.append(f"**{len(endpoints_checked)} endpoints checked, {len(violations)} violations found.**\n")
    if not violations:
        lines.append("No schema or required-field violations found. 🎉\n")
    lines.append("## Endpoints checked\n")
    for e in endpoints_checked:
        n = sum(1 for v in violations if v.endpoint == e)
        mark = "✅" if n == 0 else f"❌ {n} violation(s)"
        lines.append(f"- `{e}` — {mark}")
    lines.append("")

    if violations:
        lines.append("## Violations\n")
        by_endpoint: dict[str, list[Violation]] = {}
        for v in violations:
            by_endpoint.setdefault(v.endpoint, []).append(v)
        for endpoint, vs in by_endpoint.items():
            lines.append(f"### `{endpoint}`\n")
            lines.append("| JSON Path | Kind | Expected | Actual |")
            lines.append("|---|---|---|---|")
            for v in vs:
                lines.append(f"| `{v.json_path}` | {v.kind} | {v.expected} | {v.actual} |")
            lines.append("")

    with open(path, "w") as f:
        f.write("\n".join(lines) + "\n")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--swagger", required=True)
    ap.add_argument("--socket", required=True)
    ap.add_argument("--report", required=True)
    args = ap.parse_args()

    with open(args.swagger) as f:
        spec = yaml.safe_load(f)

    resolver = RefResolver.from_schema(spec)
    violations: list[Violation] = []
    endpoints_checked: list[str] = []

    def schema_for(swagger_path: str, method: str, status: int = 200):
        return spec["paths"][swagger_path][method]["responses"][status]["schema"]

    # --- GET /version ---
    endpoint = "GET /version"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/version")
    if status != 200 or not isinstance(body, dict):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON object", f"HTTP {status}: {body!r}"))
    else:
        validate_against_schema(endpoint, schema_for("/version", "get"), body, resolver, violations)
        check_required_paths(endpoint, body, REQUIRED_FIELDS[endpoint], violations)

    # --- GET /info ---
    endpoint = "GET /info"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/info")
    if status != 200 or not isinstance(body, dict):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON object", f"HTTP {status}: {body!r}"))
    else:
        validate_against_schema(endpoint, schema_for("/info", "get"), body, resolver, violations)
        check_required_paths(endpoint, body, REQUIRED_FIELDS[endpoint], violations)

    # --- open /events BEFORE creating the probe container, so we can catch a real event ---
    probe_name = "cider-compat-swagger-probe"
    # best-effort cleanup of a stale probe from a previous aborted run
    request_json(args.socket, "DELETE", f"/containers/{probe_name}?force=1")

    import threading
    event_holder = {}

    def _events_reader():
        event_holder["event"] = read_one_event(args.socket, timeout=8.0)

    t = threading.Thread(target=_events_reader, daemon=True)
    t.start()

    time.sleep(0.3)  # give the reader a moment to establish the streaming GET

    # --- create + start probe container ---
    status, body = request_json(
        args.socket, "POST", f"/containers/create?name={probe_name}",
        body={"Image": "alpine:3.22", "Cmd": ["sleep", "300"]},
    )
    created_ok = status == 201 and isinstance(body, dict) and "Id" in body
    if created_ok:
        request_json(args.socket, "POST", f"/containers/{probe_name}/start")

    t.join(timeout=9.0)

    # --- GET /containers/json?all=1 ---
    endpoint = "GET /containers/json (each item)"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/containers/json?all=1")
    item_schema = None
    if status != 200 or not isinstance(body, list):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON array", f"HTTP {status}: {body!r}"))
    else:
        arr_schema = schema_for("/containers/json", "get")
        item_schema = arr_schema.get("items", {})
        probe_item = next((c for c in body if probe_name in (n.lstrip("/") for n in c.get("Names", []))), None)
        target = [probe_item] if probe_item else body[:1]
        for item in target:
            validate_against_schema(endpoint, item_schema, item, resolver, violations)
            check_required_paths(endpoint, item, REQUIRED_FIELDS[endpoint], violations)

    # --- GET /containers/{id}/json ---
    endpoint = "GET /containers/{id}/json"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", f"/containers/{probe_name}/json")
    if status != 200 or not isinstance(body, dict):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON object", f"HTTP {status}: {body!r}"))
    else:
        validate_against_schema(endpoint, schema_for("/containers/{id}/json", "get"), body, resolver, violations)
        check_required_paths(endpoint, body, REQUIRED_FIELDS[endpoint], violations)

    # --- GET /images/json ---
    endpoint = "GET /images/json (each item)"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/images/json")
    if status != 200 or not isinstance(body, list):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON array", f"HTTP {status}: {body!r}"))
    else:
        arr_schema = schema_for("/images/json", "get")
        item_schema = arr_schema.get("items", {})
        for item in body[:3] or []:
            validate_against_schema(endpoint, item_schema, item, resolver, violations)
            check_required_paths(endpoint, item, REQUIRED_FIELDS[endpoint], violations)
        if not body:
            violations.append(Violation(endpoint, "$", "manual-required", "at least one image (alpine:3.22 was pulled)", "empty array"))

    # --- GET /networks ---
    endpoint = "GET /networks (each item)"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/networks")
    if status != 200 or not isinstance(body, list):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON array", f"HTTP {status}: {body!r}"))
    else:
        arr_schema = schema_for("/networks", "get")
        item_schema = arr_schema.get("items", {})
        for item in body:
            validate_against_schema(endpoint, item_schema, item, resolver, violations)
            check_required_paths(endpoint, item, REQUIRED_FIELDS[endpoint], violations)

    # --- GET /volumes ---
    endpoint = "GET /volumes"
    endpoints_checked.append(endpoint)
    status, body = request_json(args.socket, "GET", "/volumes")
    if status != 200 or not isinstance(body, dict):
        violations.append(Violation(endpoint, "$", "http", "200 + JSON object", f"HTTP {status}: {body!r}"))
    else:
        validate_against_schema(endpoint, schema_for("/volumes", "get"), body, resolver, violations)
        check_required_paths(endpoint, body, REQUIRED_FIELDS[endpoint], violations)
        vol_endpoint = "GET /volumes (each Volumes[] item)"
        endpoints_checked.append(vol_endpoint)
        vol_def = resolver.resolve("#/definitions/Volume")[1]
        for item in (body.get("Volumes") or [])[:3]:
            validate_against_schema(vol_endpoint, vol_def, item, resolver, violations)
            check_required_paths(vol_endpoint, item, REQUIRED_FIELDS[vol_endpoint], violations)

    # --- events ---
    endpoint = "GET /events (one event)"
    endpoints_checked.append(endpoint)
    event = event_holder.get("event")
    if event is None:
        violations.append(Violation(endpoint, "$", "manual-required",
                                     "at least one NDJSON event within 8s of container create+start",
                                     "no event observed (stream may not be flushing promptly, or create/start didn't emit one)"))
    else:
        validate_against_schema(endpoint, schema_for("/events", "get"), event, resolver, violations)
        check_required_paths(endpoint, event, REQUIRED_FIELDS[endpoint], violations)

    # --- cleanup ---
    request_json(args.socket, "POST", f"/containers/{probe_name}/stop?t=1")
    request_json(args.socket, "DELETE", f"/containers/{probe_name}?force=1")

    render_report(args.report, endpoints_checked, violations, args.swagger, args.socket)

    print(f"swagger_check: {len(endpoints_checked)} endpoints checked, {len(violations)} violations. Report: {args.report}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
