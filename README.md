# Cider

Cider is a Docker Engine API daemon, written in C#/.NET, that speaks the Docker HTTP API over a unix
socket and executes everything on [Apple `container`](https://github.com/apple/container). Point the
`docker` CLI, `docker compose`, Testcontainers, .NET Aspire or Docker.DotNet at it and they work
against Apple's native VM-per-container runtime on macOS instead of a Linux `dockerd`.

It is a [ChilliCream](https://chillicream.com) project, and it takes its name from the same shelf as
Hot Chocolate and Strawberry Shake.

**Status: early.** A large surface is proven end to end against the real Apple runtime — Docker CLI
tier-1/2 commands, `docker compose`, published ports, container DNS, Testcontainers (Ryuk included),
.NET Aspire, and classic (non-BuildKit) `docker build`. A number of Docker behaviours are not
supported or are emulated, and **BuildKit is not one of the things that works**. Read
[Limitations](#limitations) before you rely on this for anything real, especially before making
Cider your default engine for a day or two.

## Requirements

- **macOS 26 on Apple silicon.** Intel Macs cannot run Apple `container` at all.
- **Apple `container` ≥ 1.2.x** installed. Running `container system start` once is a convenience,
  not a prerequisite: the daemon calls `container system start --enable-kernel-install` itself at
  startup if the runtime is not up.
- The **`docker` CLI** and **`docker compose`**, for actually using the daemon. The Homebrew formula
  pulls both in as dependencies (Homebrew's `docker` formula is the CLI only); Docker Desktop is not
  needed.

## Install

To install Cider and make it your default Docker engine:

```bash
brew install chillicream/tools/cider
cider install
docker context use cider
```

Done — `docker`, `docker compose`, Testcontainers and Aspire now talk to Cider. You do **not** need
to touch `/var/run/docker.sock`; that is only for tools that hardcode the path and ignore the
docker context (see [Taking over `/var/run/docker.sock`](#taking-over-varrundockersock)). To go
back: `docker context use default` (or whatever `docker context ls` showed before).

If you had no docker CLI before, `brew` installed Homebrew's `docker` and `docker-compose` alongside
Cider. One catch: Homebrew puts the compose plugin in `$(brew --prefix)/lib/docker/cli-plugins`,
which the docker CLI does not search by default, so `docker compose` says "not a docker command"
until you add that directory to `~/.docker/config.json` once:

```json
{ "cliPluginsExtraDirs": ["/opt/homebrew/lib/docker/cli-plugins"] }
```

(Merge it into the file if one already exists.) If you already had `docker compose` working, nothing
changes.

The rest of this section explains what those three lines do, because Cider separates three things
on purpose, and this is the part people get wrong:

1. **Getting the binary** — starts nothing.
2. **`cider install`** — the opt-in step that makes a background daemon exist (a launchd agent) and
   creates a `cider` docker context. Nothing installs a daemon behind your back.
3. **Pointing your tooling at it** — a docker context, `DOCKER_HOST`, or taking over
   `/var/run/docker.sock`. Also opt-in, and the last of the three is the only one that touches the
   rest of your machine.

So if you install Cider and then type `docker ps`, you are still talking to whatever engine you had
before. That is intentional, not a broken install.

### `cider install` — the opt-in daemon

```bash
cider install [--system-socket] [--force-system-socket] [--no-context] [--host-loopback] [--socket PATH] [--data-dir DIR]
```

This writes a per-user launchd agent at
`~/Library/LaunchAgents/com.chillicream.cider.daemon.plist` (`RunAtLoad`, and a `KeepAlive` that
restarts it after a non-zero exit), bootstraps and kickstarts it under `gui/<uid>`, waits up to 10 s
for the socket to appear, and — unless `--no-context` — creates a `cider` docker context. It does
**not** switch your current context, and it does **not** touch `/var/run/docker.sock` or `pf`
unless you ask (`--system-socket`, `--host-loopback`).

The plist sets `ProcessType` to `Interactive`, not the launchd default. Every container operation
cider runs is a child `container` CLI process, and macOS applies background CPU/IO throttling to a
job's whole process tree, not just the job itself — an interactive-class job is the only one launchd
never throttles. Re-running `cider install` rewrites the plist and restarts the job, so it also fixes
an older install that was still on the throttled class.
<!-- LaunchdInstaller.cs:36 plist path, :68-75 RunAtLoad/KeepAlive, :172 setCurrent:false; Models.cs:11 Label "com.chillicream.cider.daemon", :12 ContextName "cider" -->

The other verbs:

```bash
cider serve [--socket PATH] [--data-dir DIR] [--log-level LEVEL] [--no-dns]   # the default verb
cider status [--socket PATH]        # socket / launchd / Apple container status
cider sync [--socket PATH] [--data-dir DIR] [--json]   # resync cider's state with Apple container
cider uninstall [--data-dir DIR]    # unload the agent, drop the context, restore the system socket
cider host-loopback enable|disable  # opt in/out of the 127.0.0.1 pf redirect, see below
cider version
```

> `install`, `uninstall` and `status` are implemented and covered by unit tests (plist generation,
> the backup record, the restore decision table), but the whole flow has **never been run end to end
> on a real machine** — it touches launchd and `/var/run/docker.sock`, which is the machine owner's
> call to make. Treat your first run as untested and check `cider status` afterwards.

### Taking over `/var/run/docker.sock`

Some tools hardcode `/var/run/docker.sock` instead of honouring `DOCKER_HOST` or the docker context.
That path is root-owned, so Cider cannot replace it without elevated privileges. `install` **prints**
the command, and only runs it when you pass `--system-socket` and non-interactive `sudo` works:

```bash
sudo ln -sf ~/.cider/docker.sock /var/run/docker.sock
```

On most machines that path already belongs to another engine (OrbStack, Docker Desktop, Colima).
`--system-socket` therefore saves whatever was there first, into
`<data-dir>/system-socket.backup.json`, and `cider uninstall` puts the previous target back — but
only while `/var/run/docker.sock` still points at Cider's socket; if another engine has since claimed
it, uninstall leaves it alone and says so. The record lives in the data dir, so **pass the same
`--data-dir` you installed with**, otherwise uninstall cannot find it (it then prints the retry and
the manual restore command and exits non-zero rather than leaving the path dangling in silence).

Neither command ever prompts for a password: they use `sudo -n`, and if that would need one they
print the exact `sudo ln -sf …` to run by hand and exit non-zero. If `/var/run/docker.sock` is a
**real socket file** rather than a symlink it cannot be recreated once replaced, so `--system-socket`
refuses to touch it; stop the engine that owns it, or pass `--force-system-socket` and accept that
uninstall can then only remove Cider's link and warn.

By hand, note the current target first so you can put it back:

```bash
readlink /var/run/docker.sock                        # e.g. /Users/you/.orbstack/run/docker.sock
sudo ln -sf ~/.cider/docker.sock /var/run/docker.sock
sudo ln -sf /Users/you/.orbstack/run/docker.sock /var/run/docker.sock   # restore
```

If `readlink` printed nothing there was no previous link — remove Cider's with `sudo rm -f` instead.
Note that macOS clears `/private/var/run` at boot, so this symlink does not survive a restart, while
your docker context still says `cider`.

### Uninstalling

`cider uninstall --data-dir <the one you installed with>` unloads and deletes the launchd agent,
removes the docker context and restores the system socket. It does not delete your state: remove
`~/.cider` yourself if you want the container, network and volume records, and the captured logs,
gone too. When the `.pkg` route ships it will need one extra step — macOS packages have no built-in
uninstaller, so removing one means `pkgutil --pkgs`, `pkgutil --forget <id>`, and deleting the
installed files by hand.

## Quick start

Start the daemon in the foreground (or `cider install` it as a background agent, above):

```bash
cider serve                                  # from a checkout: dotnet run --project src/Cider.Daemon -- serve
```

By default it listens on `~/.cider/docker.sock` and keeps its state under `~/.cider`. Point Docker
tooling at it, either per shell:

```bash
export DOCKER_HOST=unix://$HOME/.cider/docker.sock
docker version
```

or through a docker context:

```bash
docker context create cider --docker "host=unix://$HOME/.cider/docker.sock"
docker context use cider
```

Then it is just Docker:

```bash
docker run --rm alpine:3.22 echo hello
docker run -d --name web -p 8080:80 nginx:alpine && curl localhost:8080
docker compose up -d && docker compose ps && docker compose down -v
DOCKER_BUILDKIT=0 docker build -t myimage .        # BuildKit is not supported, see Limitations
```

Testcontainers and .NET Aspire need no configuration beyond `DOCKER_HOST` (or the context) pointing
at Cider — Testcontainers' Ryuk reaper and Aspire's DCP both work as they are.

### What to expect on the first day

- **The two engines share nothing.** Cider keeps its own state under `~/.cider` and runs on Apple
  `container`, which has its own image and volume stores. Right after you switch, `docker ps` and
  `docker images` are empty, every image re-pulls, and named volumes from your previous engine do
  not exist here. There is no migration path.
- **Every container is a full VM.** Apple `container` gives each container its own lightweight VM,
  and Cider passes explicit defaults of **2 CPUs and 2 GiB** per container unless the container asks
  for something else. Compose files rarely set `mem_limit`/`cpus`, so in practice every service gets
  the default: an eight-service stack asks for roughly 16 GiB / 16 vCPU, plus a small per-network DNS
  forwarder (1 CPU / 256 MiB). That is comfortable on a Mac with plenty of RAM and not survivable at
  default sizing on 16–32 GiB; set `defaultMemoryBytes` down or size services individually there.
- **A reboot drops the `/var/run/docker.sock` symlink** (see above), while your docker context
  happily still says `cider`.
- **`docker run -it` carries Apple's own boot spinner** — a `Starting container [0s]` progress line
  plus ANSI cursor codes ahead of the container's first byte. Cosmetic, but they are bytes real
  Docker never sends.

## Configuration

`~/.cider/config.json` is read if it exists — the daemon never writes it, so create it yourself when
you want to override a default. Environment variables win over the file, and explicit
`serve`/`install` flags win over both. A broken or missing `config.json` is ignored, never fatal.

| Key (`config.json`) | Env var | Default | Meaning |
|---|---|---|---|
| `dataDir` | `CIDER_DATA_DIR` | `~/.cider` | Root of all daemon state (`state/`, `logs/`, `volumes/`, `tmp/`) |
| `socketPath` | `CIDER_SOCKET` | `<dataDir>/docker.sock` | Unix socket the daemon listens on (max 104 characters) |
| `containerCliPath` | `CIDER_CONTAINER_CLI` | `container` | Path to, or `PATH`-resolved name of, the Apple `container` CLI |
| `portPublishing` | `CIDER_PORT_PUBLISHING` | `proxy` | `proxy` (the daemon binds host ports and forwards itself) or `apple` (hand `-p` to Apple `container`). Anything other than `apple` means `proxy` |
| `logLevel` | `CIDER_LOG_LEVEL` | `Information` | The daemon's own log verbosity |
| `defaultCpus` | — | `2` | CPUs given to containers that do not request a specific amount |
| `defaultMemoryBytes` | — | `2147483648` (2 GiB) | Memory given to containers that do not request a specific amount |
| `dns.enabled` | — | `true` | Whether the built-in DNS server and the per-network forwarders run |
| `dns.listen` | — | `0.0.0.0:10053` | Where the daemon's own DNS server listens (see [DNS](#container-name-dns)) |
| `dns.upstream` | — | `["1.1.1.1:53", "8.8.8.8:53"]` | Upstream resolvers for names the daemon does not know |
| `dns.searchDomain` | — | `""` | Optional `--dns-search` value passed to containers |
| `dnsForwarderImage` | — | `docker.io/coredns/coredns:1.14.7` | Image used for the per-network DNS forwarder |
| `pollIntervalSeconds` | — | `3` | How often the state poller reconciles against `container ls` |
| `logMaxBytes` | — | `67108864` (64 MiB) | Cap on one container's captured log file before it is truncated |

<!-- every key, default and env var: src/Cider.Core/Configuration/CiderOptions.cs (properties at :39-98, env reads in Load() at :122-176) -->

Not configurable: the daemon advertises Docker API version `1.47` (accepting clients down to `1.24`)
and reports engine version `29.0.0`.

## What works

| Area | Status |
|---|---|
| `docker run` / `create` / `start` / `stop` / `kill` / `restart` / `rm` | Works |
| `docker ps` / `inspect` / `logs` / `top` (best effort) / `stats` / `rename` | Works |
| `docker exec`, including `-it` with a real pty | Works |
| `docker attach`, `docker cp`, `docker wait`, `docker export` | Works |
| `docker pull` / `push` / `tag` / `images` / `rmi` / `save` / `load` | Works |
| `docker commit` / `docker import` | Works, flattened to a single layer (see Limitations) |
| `docker build` (classic builder) | Works with `DOCKER_BUILDKIT=0` — see [Building images](#building-images) |
| `docker network` / `docker volume` | Works; network attachment is fixed once a container has started (see Limitations) |
| `docker events` | Works |
| Published ports (`-p`) | Works — the daemon binds the host port and forwards TCP/UDP itself, see [Port publishing](#port-publishing) |
| `docker compose up` / `down` / `logs` / `ps` | Works (compose picks the classic builder automatically) |
| Testcontainers (.NET) | Works, including the Ryuk reaper: the daemon relays a bind mount targeting `/var/run/docker.sock` to its own socket, and in the default `proxy` mode Ryuk's published port is reachable |
| .NET Aspire | Works — Aspire 13.5.0 AppHost + DCP, exercised end to end with a two-resource fixture (redis and postgres) and a consumer round-tripping through both over published ports |
| Container-name DNS, `host.docker.internal` | Works via a per-network CoreDNS forwarder — see [DNS](#container-name-dns) |

### Building images

`/_ping` advertises `Builder-Version: 1`, which steers `docker compose build` and modern compose
tooling to the classic (non-BuildKit) `/build` endpoint automatically. A plain `docker build` from a
recent docker CLI defaults to BuildKit, which Cider does not speak, so force the classic builder:

```bash
DOCKER_BUILDKIT=0 docker build -t myimage .
```

A build without `-t` behaves like Docker's: the image shows up as `<none>:<none>`, counts as
dangling, and is removed by `docker image prune`. (Apple `container` insists on a repo name for every
build, so the daemon mints a private one and hides it everywhere a client could see it.)

### Port publishing

In the default `proxy` mode the daemon carries `-p host:container` itself: when a container starts it
binds the host side and forwards to the container's VM address, for both TCP and UDP. `HostIp` is
honoured (`-p 127.0.0.1:8080:80` binds loopback only; an empty or `0.0.0.0` host IP binds both
address families), `-p 0:80` and `--publish-all` allocate an ephemeral host port and report it back
through `docker ps`/`inspect`, and TCP half-closes propagate the way dockerd's userland proxy does.
Publications are set up after start, refreshed by the state poller, and torn down on stop, die and
remove.

Setting `portPublishing` to `apple` hands `-p` to Apple `container` instead and keeps the daemon out
of the data path. That mode depends on Apple's own forwarder, which on some Macs is refused by macOS
Local Network privacy (`backend - connect failed: No route to host`) until the process running
Apple's services is allowed under System Settings → Privacy & Security → Local Network. That is why
`proxy` is the default.

### Container-name DNS

Apple `container`'s embedded resolver does not resolve container names, and once Apple's network
stack is up, host port 53 is already bound by macOS/vmnet — so Cider cannot simply serve DNS there.
Instead the daemon runs its own DNS server on `0.0.0.0:10053` and lazily starts one small CoreDNS
forwarder container per Docker network (`cider-dns-<network>-<hash>`, hidden from `docker ps`) that
listens on the network's normal port 53 and relays to `<gateway-ip>:10053`. Containers on that
network are started with `--dns <forwarder-ip>`, so container names, aliases, compose service names
and `host.docker.internal` resolve normally, while everything else falls through to the configured
upstream resolvers. The `<hash>` is derived from the daemon's data dir, so several daemons on one
machine never share or restart each other's forwarders; if `dns.listen`'s port is taken, the next 20
are tried and the forwarders relay to the one actually bound.

Forwarders shut down with their network (`docker network rm`, `compose down`) and on a clean daemon
shutdown. A hard-killed daemon can leave one behind — see [Troubleshooting](#troubleshooting).

### Reaching loopback-only host services

By default `host.docker.internal` (answered with the gateway address, above) only reaches a host
service bound to `0.0.0.0` — a service bound to `127.0.0.1` only, which is the default for many dev
servers, is not reachable from a container. This is an opt-in, not something Cider tries at DNS
resolution time: it copies the same recipe Apple's own `container system dns create --localhost`
uses for the identical problem — one `pf` anchor rule redirecting the container subnet's gateway
address to `127.0.0.1`, all ports, one rule — under Cider's own anchor name
(`com.chillicream.cider.hostloopback`), so it never touches or races Apple's `com.apple.container`
anchor. Writing the anchor's rule file is not enough on its own for pf to ever evaluate it, so
`enable` also registers `rdr-anchor`/`anchor`/`load anchor` lines for it in `/etc/pf.conf` (right
alongside Apple's own `com.apple` anchor stanza, in the order pf.conf(5) requires) and reloads the
main ruleset — the same wiring Apple's `PacketFilter.swift` does for `com.apple.container`.

```bash
cider install --host-loopback     # opt in at install time, or:
cider host-loopback enable        # opt in later, against an already-running daemon
cider host-loopback disable       # opt back out
```

`enable` asks the running daemon for the default network's gateway/subnet, then writes and loads the
anchor via non-interactive `sudo`; if that would need a password it prints the exact commands to run
by hand instead (the same pattern as [`--system-socket`](#taking-over-varrundockersock)) — either
way it is recorded as opted in, so `disable` (or the manual `pfctl`/`rm` it prints) is the only way to
turn it back off, and:

- **Needs admin (root).** Non-interactive only — Cider never prompts for a password itself.
- **Disables Private Relay while the rule is loaded**, per Apple's own documented caveat for the
  identical trick (`docs/host-integration.md` in the `container` repo).
- **Does not survive a reboot** — pf state resets, so `cider serve` reinstalls the rule itself (best
  effort, silently, no prompt) each time it starts while enabled. There is still a gap between boot
  and the daemon coming back up.
- **Covers only the default `bridge` network's subnet.** A container on a different, user-created
  network is unaffected.
- `disable` only flushes Cider's own anchor (`pfctl -a com.chillicream.cider.hostloopback -F all`),
  removes its three lines from `/etc/pf.conf` and releases pf via reference-counted
  `pfctl -X <token>`, using the token `enable`'s `pfctl -E` printed and recorded — it never runs
  `pfctl -d` and never touches any other anchor, Apple's included.

## Limitations

| Limitation | Detail |
|---|---|
| **No BuildKit / buildx** | `/grpc` and `/session` return 404; use `DOCKER_BUILDKIT=0`. Dockerfile features that need BuildKit — cache mounts, secret mounts, heredocs — therefore do not build at all. A spike settled the route: reaching Apple's *own* builder VM is a **no-go**, because its BuildKit is only reachable over Apple's private, unversioned, Swift-only XPC protocol. The intended path is instead to run our own BuildKit container under Apple `container` with `--publish-socket` and point `docker buildx create --driver remote unix://<path>` at it. That primitive is verified to work on this runtime; the recipe is not built or shipped yet |
| No pause/unpause | `POST /containers/{id}/pause` and `/unpause` return 501 — Apple `container` has no equivalent |
| No `--privileged` | Mapped to `--cap-add ALL --masked-path NONE --read-only-path NONE`, which is not a true privileged mode; Docker-in-Docker and some device access will not behave |
| No `--network host` / `none` / `container:*` | Rejected with 400. A compose service using `network_mode: host` does not come up |
| `network connect`/`disconnect` only before the first start | Apple fixes a container's networks at create time, so both work on a container that was created and never started (the daemon re-creates it with the new network list) and return 501 with an explanatory message afterwards. A container always keeps at least one network |
| Every container is a VM | Each container gets its own lightweight VM; defaults are 2 CPUs / 2 GiB unless overridden. Many services at once cost real RAM |
| Exit codes can be lost | The daemon captures a container's exit code live from the held `container start -a` process; if the daemon restarts while the container keeps running, that exit code is unrecoverable from Apple `container` afterwards |
| Logs merged for containers the daemon did not start | Containers created outside the daemon are surfaced read-only; their historical logs are not in the daemon's log store |
| `docker commit` / `import` flatten the image | Apple has no commit primitive, so both export the container's whole root filesystem and load it back as a **single-layer** OCI image: real and runnable, but sharing no layer with its parent. `--change` accepts `CMD`, `ENTRYPOINT`, `ENV`, `EXPOSE`, `WORKDIR`, `USER`, `LABEL` and `VOLUME`; anything else is a 400. `docker import <url>` (as opposed to `docker import -`) is not supported |
| `docker history` reports no per-layer size | Instruction text, comment and timestamp of every row are real; `Size` is always `0`, because Apple reports one total size per platform and no per-blob sizes at all |
| `ExposedPorts` are invisible on images | Apple's image inspect omits `ExposedPorts` (and `Volumes`) from every image config, so `docker inspect <image>` shows none and `docker run -P` publishes nothing. Publish explicitly with `-p` |
| Only the `json-file` log driver | Another `HostConfig.LogConfig.Type` is rejected at create — an unknown name with dockerd's own 400, a driver dockerd has but this daemon cannot honour (including `none`) with a 501 — rather than being accepted and silently logged json-file anyway |
| Volumes have only the `local` driver | `POST /volumes/create` with any other `Driver` is a 404, in dockerd's plugin-lookup wording |
| Volumes are single-attach ext4 images | Apple volumes are sparse ext4 disk images (512 GiB virtual size by default), not host directories: you cannot read one from the host, only through a container. Bind mounts behave normally |
| A static container IP cannot be honoured | Apple has no `--ip`, so `EndpointsConfig[net].IPAMConfig.IPv4Address` is not applied and the runtime picks the address. A malformed address, or one outside the network's subnet, is still rejected at create with dockerd's wording |
| No `diff`, best-effort `top` | `changes` is not implemented (Apple exposes no layer view); `top` runs `ps` inside the container as an approximation |
| Restart policies and healthchecks are emulated | The daemon supervises `always`/`unless-stopped`/`on-failure` restarts and runs healthcheck probes itself; Apple `container` has no native support for either |
| No swarm | Every `/swarm`, `/services`, `/tasks`, `/nodes`, `/secrets`, `/configs`, `/plugins` route answers with a clear "not supported" error instead of a raw 404 |
| IPv6 not handled | Container IPv6 addresses exist on the wire but are not managed or exposed |
| `host.docker.internal` reaches only `0.0.0.0`-bound services, unless you opt in | A host service bound to `127.0.0.1` only is not reachable from containers by default — and many dev servers bind loopback by default. `cider host-loopback enable` closes the gap with a pf redirect; see [Reaching loopback-only host services](#reaching-loopback-only-host-services) |
| Published ports flow through the daemon (default `proxy`) | Traffic is relayed by the daemon process, not by the kernel or Apple's forwarder, so it stops when the daemon stops and adds one userland hop. `portPublishing: "apple"` avoids the hop but depends on macOS Local Network permission |
| `docker cp` **into** a created container is emulated | Apple `container cp` refuses a container that is not running, so a tar `PUT` into a created container is staged under the data dir and bind-mounted in when the container starts (this is how .NET Aspire injects its dev certificates). Up to 64 files are mounted; beyond that they are copied in immediately after the start instead |
| `docker cp` **out of** a stopped container is emulated | The same refusal, in the other direction: the path is selected out of the container's own rootfs export, which costs O(rootfs) rather than O(path). A path that reaches its target through a symbolic link inside the container is not resolved <!-- wording taken from the cp-out implementation's own note; src/Cider.Core/Services/ContainerManager.Archive.cs --> |
| `HEAD /containers/{id}/archive` of a non-root path is O(image) | The stat is served by copying the path out of the container; `/` is answered synthetically, but a stat of a large subtree copies that subtree |

## How it works

```
docker CLI / compose / Testcontainers / Aspire
          │  HTTP/1.1 over unix socket
          ▼
   cider (Kestrel, in-process)
      │                                 │
      │ control plane                   │ data plane, on the daemon's own sockets:
      │ drives the `container` CLI      │  · published ports: host bind → <containerIP>:<port>
      ▼                                 │  · DNS for guests on 0.0.0.0:10053 + CoreDNS forwarder
   container CLI                        │
      │ talks to container-apiserver    │ straight to the guest VM's IP, no Apple forwarder
      ▼                                 ▼
   container-apiserver ── one lightweight VM per container
```

The daemon is a single ASP.NET Core (Kestrel) process listening on a unix socket. Its **control
plane** — everything that creates, starts, stops, inspects, execs into or lists containers, images,
networks and volumes, plus `docker cp` and `build` — goes through exactly one path: invoking the
`container` CLI as a subprocess and parsing its output. There is no XPC and no Swift bridge, and that
path is the single implementation behind `IContainerRuntime`.

The **data plane** deliberately does not go through Apple. In the default `proxy` mode the daemon
binds the host side of every `-p` mapping itself and forwards straight to the container's VM address,
and container DNS is answered by the daemon's own DNS server through a per-network CoreDNS forwarder.
Both exist because Apple's own forwarder is refused by macOS Local Network privacy on some machines,
and because host port 53 is already taken once Apple's network stack is up.

Docker's two "hijacked" endpoints (`POST /exec/{id}/start`, which always carries a body Kestrel
refuses to upgrade, and `POST /containers/{id}/attach`) are intercepted at the connection level
before Kestrel's HTTP layer sees them, so the daemon can hold the raw stream open for the lifetime of
the process instead of tearing it down the moment a client half-closes its write side.

State (container, network and volume records) is persisted as JSON under `~/.cider/state`; captured
container logs live under `~/.cider/logs`; the daemon's own launchd log is `~/.cider/daemon.log`.

| Project | Purpose |
|---|---|
| `src/Cider.Core` | Docker wire DTOs, the `IContainerRuntime` abstraction, state stores, managers (container/exec/image/network/volume/system), events, logs, health, restart supervision — no ASP.NET dependency |
| `src/Cider.AppleContainer` | The `IContainerRuntime` implementation that drives the `container` CLI: process launching, pty handling, JSON parsing, error mapping |
| `src/Cider.Dns` | Standalone DNS server (UDP + TCP), message codec, resolver interface — no dependency on Core |
| `src/Cider.Daemon` | The `cider` executable: Kestrel hosting, the hijack interceptor, Docker API routes, and the `serve`/`install`/`uninstall`/`status`/`sync` verbs |

## Troubleshooting

### cider's records disagree with Apple `container`

A container/network/volume deleted with the Apple CLI directly, Apple services restarted, or a
hard-killed daemon (below) can all leave cider's own records out of sync with what Apple `container`
actually has — `docker ps`/`docker network ls` keep showing something that is gone, or don't show
something you created with the Apple CLI. Resync on demand:

```bash
cider sync
```

This only ever fixes cider's own records and cider-owned side processes (DNS forwarders); it never
deletes anything on the Apple side. Pass `--json` for the raw report.

### A hard-killed daemon can wedge the Apple runtime

The daemon holds one `container start -a <name>` child process per running container, to own that
container's lifetime and exit code. A clean shutdown disposes them. A **hard kill** — `pkill`, a
crash, a test harness tearing the process down — does not: the children survive with no parent, keep
their containers running, and hold the networks those containers are attached to. Once that debris
accumulates, `container network create` can hang for 300+ seconds *with no Cider in the path*,
`container stop` hangs, and `container network delete` answers `network <n> has a pending operation`.

The daemon sweeps this itself: at startup it kills any `container start -a` child whose parent is
gone *and* that carries the `CIDER_HELD=1` marker it stamps on every child it spawns — so a held
child with a live parent, or a `container start -a` you ran yourself, is never touched. Killing the
child does not stop its container, which the startup reconcile then adopts as running. Starting or
restarting any Cider instance therefore performs the first recovery step below automatically.

For a machine where no daemon will be started again, or for containers and networks leaked before
that sweep existed, recover by hand in this order:

```bash
pkill -9 -f "^container start -a"        # release the orphaned child processes
container ls -a                          # find the leaked containers…
container stop <name> && container delete <name>
container network ls                     # …then the networks they were holding
container network delete <name>
cider sync                               # then bring cider's own records back in line
```

Orphaned `cider-dns-*` CoreDNS forwarders come from the same mechanism and are removed the same way
(`container rm -f <name>`); the daemon starts a fresh forwarder on demand.

### `docker` talks to the wrong engine

Check both switches: `docker context ls`, and `readlink /var/run/docker.sock`. The docker context and
the system socket are independent and can disagree — most often after a reboot, which clears the
symlink but not the context.

### Container creates take seconds / Testcontainers fails with "Initialization has been cancelled"

Check `ProcessType` in the plist — installs made with 0.1.4 or earlier used `Background`, which
throttles CPU/IO for every child `container` process the daemon spawns, so container operations can
take seconds instead of tens of milliseconds; under load that is slow enough for Testcontainers'
Ryuk to hit its 60 s init budget and throw. Re-run `cider install`.

```bash
grep -A1 ProcessType ~/Library/LaunchAgents/com.chillicream.cider.daemon.plist
```

## Testing

```bash
dotnet test Cider.sln
```

Unit and integration tests against a fake in-memory container runtime, plus — when the real `docker`
CLI is on `PATH` — an in-process daemon over a temp socket: **766 passing and 3 skipped** in
`Cider.Tests` (the 3 need the opt-in below) and **16** in `Cider.Dns.Tests`, on both target
frameworks.

Tests against the real Apple `container` runtime are opt-in and slower:

```bash
CIDER_E2E=1 dotnet test Cider.sln
```

That adds the adapter tests in `tests/Cider.Tests/AppleContainer/` and the `tests/Cider.E2E.Tests`
suite — **33 tests, last run 32 passed / 1 skipped / 0 failed** (roughly 4 minutes), driving the real
`docker` CLI, `docker compose`, Testcontainers and a .NET Aspire AppHost against an in-process daemon
on the real runtime. The single skip is the `apple`-mode port characterization, which only runs with
`CIDER_PORT_PUBLISHING=apple`.

The Testcontainers and Aspire fixtures (`tests/e2e-testcontainers`, `tests/e2e-aspire`) sit outside
`Cider.sln` on purpose and are built by those tests on demand; the Aspire fixture pins SDK 10.0.302
in its own `global.json`, because `Aspire.AppHost.Sdk` 13.5.0 cannot build on the .NET 11 preview SDK
the repository root rolls forward to.

## License

MIT — see [`LICENSE`](LICENSE). Copyright ChilliCream Inc.
