# Cider

Cider is a Docker Engine API daemon, written in C#/.NET, that speaks the Docker HTTP API over a unix
socket and executes everything on [Apple `container`](https://github.com/apple/container). Point the
`docker` CLI, `docker compose`, Testcontainers, .NET Aspire or Docker.DotNet at it and they work
against Apple's native VM-per-container runtime on macOS instead of a Linux `dockerd`.

It is a [ChilliCream](https://chillicream.com) project, and it takes its name from the same shelf as
Hot Chocolate and Strawberry Shake.

**Status: early.** A large surface is proven end to end against the real Apple runtime — Docker CLI
tier-1/2 commands, `docker compose`, published ports, container DNS, Testcontainers (Ryuk included),
.NET Aspire, and `docker build` through both BuildKit (the default builder) and the classic builder.
A number of Docker behaviours are not supported or are emulated — BuildKit here is single-platform
and cannot export to a cache or a registry (see [Limitations](#limitations)). Read
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
cider host-loopback enable|disable  # opt in/out of the 127.0.0.1 pf redirect (experimental, see below)
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
docker build -t myimage .                          # BuildKit, the default builder
DOCKER_BUILDKIT=0 docker build -t myimage .         # classic builder, see Limitations
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
| `runtime.transport` | `CIDER_RUNTIME_TRANSPORT` | `auto` | Which transport talks to Apple `container`: `auto` pings the apiserver over XPC and decides (the default), `xpc` requires XPC and fails fast if the apiserver is unreachable or too old, `cli` always shells out to the CLI instead. XPC needs apiserver ≥ 1.2.0; cider is tested through 1.3.0 — older than the minimum falls back to `cli` with a warning in `auto` (or fails fast in `xpc`), newer than tested is used untested. See [How it works](#how-it-works) |
| `portPublishing` | `CIDER_PORT_PUBLISHING` | `proxy` | `proxy` (the daemon binds host ports and forwards itself) or `apple` (hand `-p` to Apple `container`). Anything other than `apple` means `proxy` |
| `logLevel` | `CIDER_LOG_LEVEL` | `Information` | The daemon's own log verbosity |
| `builder.enabled` | `CIDER_BUILDKIT` | `true` | Whether the daemon offers BuildKit through Apple's builder VM at all. `false` (or `CIDER_BUILDKIT=0`/`=false`) makes `/grpc` and `/session` answer 404 and drops `Builder-Version` on `/_ping` to `1`, which is what makes buildx report the default builder unsupported — see [Building images](#building-images) |
| `builder.cpus` | — | Apple's own default (2) | vCPUs passed as `-c` to `container builder start` |
| `builder.memory` | — | Apple's own default (2 GiB) | Memory (bytes) passed as `-m` to `container builder start` |
| `defaultCpus` | — | `2` | CPUs given to containers that do not request a specific amount |
| `defaultMemoryBytes` | — | `2147483648` (2 GiB) | Memory given to containers that do not request a specific amount |
| `dns.enabled` | — | `true` | Whether the built-in DNS server and the per-network forwarders run |
| `dns.listen` | — | `0.0.0.0:10053` | Where the daemon's own DNS server listens (see [DNS](#container-name-dns)) |
| `dns.upstream` | — | `["1.1.1.1:53", "8.8.8.8:53"]` | Upstream resolvers for names the daemon does not know |
| `dns.searchDomain` | — | `""` | Optional `--dns-search` value passed to containers |
| `dnsForwarderImage` | — | `docker.io/coredns/coredns:1.14.7` | Image used for the per-network DNS forwarder |
| `pollIntervalSeconds` | — | `3` (cli) / `1` (xpc) | How often the state poller reconciles against the engine; transport-aware unless set explicitly (a `containerList` pass is ~19 ms over the CLI vs. ~0.1 ms over XPC) |
| `logMaxBytes` | — | `67108864` (64 MiB) | Cap on one container's captured log file before it is truncated |

<!-- every key, default and env var: src/Cider.Core/Configuration/CiderOptions.cs (properties at
     :48-162, builder.* config.json overlay in ApplyFile() at :405-416, env reads in Load() at
     :189-255 incl. CIDER_BUILDKIT at :239-243; pollIntervalSeconds's transport-aware default is
     resolved by Cider.Core.Services.StatePoller, not here) -->

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
| `docker build` (BuildKit, default builder) | Works — see [Building images](#building-images) |
| `docker build` (classic builder) | Works with `DOCKER_BUILDKIT=0` — see [Building images](#building-images) |
| `docker buildx build` / `du` / `prune` / `inspect` / `bake` | Works against the default builder — see [Building images](#building-images) |
| `docker network` / `docker volume` | Works; network attachment is fixed once a container has started (see Limitations) |
| `docker events` | Works |
| Published ports (`-p`) | Works — the daemon binds the host port and forwards TCP/UDP itself, see [Port publishing](#port-publishing) |
| `docker compose up` / `down` / `logs` / `ps` | Works |
| `docker compose build` | Works — BuildKit by default (same `Builder-Version` signal as `docker build`), classic with `DOCKER_BUILDKIT=0` |
| Testcontainers (.NET) | Works, including the Ryuk reaper: the daemon relays a bind mount targeting `/var/run/docker.sock` to its own socket, and in the default `proxy` mode Ryuk's published port is reachable |
| .NET Aspire | Works — Aspire 13.5.0 AppHost + DCP, exercised end to end with a two-resource fixture (redis and postgres) and a consumer round-tripping through both over published ports |
| Container-name DNS, `host.docker.internal` | Works via a per-network CoreDNS forwarder — see [DNS](#container-name-dns) |

### Building images

`/_ping` advertises `Builder-Version: 2` by default (`builder.enabled: true`), which steers
`docker build`, `docker buildx build`/`bake` and `docker compose build` at the default builder —
BuildKit, run inside Apple's own builder VM — with nothing to opt into:

```bash
docker build -t myimage .
docker buildx build -t myimage .
docker buildx bake
docker compose build
```

`docker buildx du`, `docker buildx prune` and `docker buildx inspect` work against the default
builder too, and `docker buildx create` custom builders are unaffected — Cider only intercepts the
*default* builder's `/grpc` and `/session`. `--push` works the way it always has on the docker
driver: BuildKit's exporter never pushes directly for it, buildx loads the image locally first and
then pushes it with an ordinary, separate `docker push` — which the daemon already serves.

Set `builder.enabled: false` (or `CIDER_BUILDKIT=0`) to turn BuildKit off instead: `/grpc` and
`/session` then answer 404 and `Builder-Version` on `/_ping` drops to `1`, which is what makes
buildx report the default builder "unsupported" and steers `docker compose build` back to the
classic (non-BuildKit) `/build` endpoint on its own — force the classic builder explicitly for a
plain `docker build`:

```bash
DOCKER_BUILDKIT=0 docker build -t myimage .
```

A build without `-t` behaves like Docker's in either builder: the image shows up as `<none>:<none>`,
counts as dangling, and is removed by `docker image prune`. (Apple `container` insists on a repo
name for every build, so the daemon mints a private one and hides it everywhere a client could see
it.)

BuildKit here is single platform (`linux/arm64` only) and cannot export to a cache or a registry —
see [Limitations](#limitations) — because the daemon does not speak to buildkitd's real exporters at
all; every `moby`-typed exporter buildx sends is rewritten in place to a `docker` (tar) exporter,
captured over a daemon-owned buildkit session instead of uploaded anywhere, and loaded the same way
`docker load` loads a tar. See [How it works](#how-it-works) for the mechanism.

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

Apple `container`'s embedded resolver does not resolve container names, and host port 53 is not free
for Cider to bind either way — Apple's own embedded resolver listens on `127.0.0.1:2053`/`:1053`, never
on 53, so it is macOS's own resolver machinery that holds the port, not Apple's container networking.
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
- **Experimental — two known defects (cider-ede.22), found by review and not yet fixed.** `pfctl -E`
  prints its `Token : <n>` line on **stderr**, but the token parser only reads stdout
  (`PfRedirect.ParseEnableToken`/`RecordEnableToken`), so the token is never actually recorded; a
  later `disable` then has nothing to pass to `pfctl -X` and never releases pf's reference count at
  all. Separately, if `pfctl -E` succeeds but a later step in `enable` fails, the token it *did* parse
  (when parsing worked) is dropped without being persisted or released either. Both leak a pf
  reference until it clears itself: `sudo pfctl -a com.chillicream.cider.hostloopback -F all` flushes
  the anchor's rules, but the reference-counted `pfctl -E` enable itself was never matched by a
  `pfctl -X <token>` disable, and there is no token on hand to construct one by hand either — a
  reboot is the reliable way to clear it. Treat `host-loopback enable` as unverified until this is
  fixed.

## Limitations

| Limitation | Detail |
|---|---|
| **BuildKit is single-platform and cannot export to a cache or a registry** | `builder.enabled` defaults to `true` and `docker build`/`buildx build`/`bake`/`compose build` all reach BuildKit through `/grpc` + `/session` (see [Building images](#building-images)) — but only `linux/arm64`: a multi-platform `--platform` request fails at Solve time because the docker (tar) exporter every build is rewritten to cannot produce a manifest list. `--cache-to`/`--cache-from` are not supported (no cache export), and `--output type=docker,dest=<path>`/`type=oci` are rejected — buildx refuses both for the docker driver without a containerd snapshotter, and Apple's worker is made to look like it has none on purpose (see [How it works](#how-it-works)) so buildx does not enable exporters this proxy cannot serve. The build cache lives inside Apple's *own* builder VM, not on the host, and is wiped by `container builder delete`. Set `builder.enabled: false` (or `CIDER_BUILDKIT=0`) to turn BuildKit off entirely and force the classic builder with `DOCKER_BUILDKIT=0` |
| **BuildKit context uploads are paced to 32 MiB/s by default** | Only the bytes the daemon sends *to* buildkitd — build context uploads via FileSync/DiffCopy — pass through a token-bucket pacer (`TokenBucketPacer.DefaultBytesPerSecond`) on the exec pipe (`container exec -i buildkit buildctl dial-stdio`), so a large upload cannot overrun buildkitd's HTTP/2 receive window and wedge it. Bytes buildkitd sends back — image tar exports via FileSend, and the Control/Status streams — are not paced at all; that direction is bounded only by the connection's 256 KiB initial HTTP/2 stream window (`BuilderConnection.InitialStreamWindowBytes`). Retuned (cider-ger.21) from the original 8 MiB/s placeholder shipped before any large-context measurement existed. cider-ger.21's own three-run measurement reported ~6.2s (~32 MiB/s, matching the new steady rate — was ~25s at 8 MiB/s) with zero stalls or link-recovery events; an independent reproduction for cider-ger.23 (three more clean, warm runs against the same 200 MiB context on this machine) instead saw 15.64s/11.80s/14.01s (12.8/16.9/14.3 MiB/s, average ~13.8s / ~14.5 MiB/s) — end-to-end wall time, which includes fixed per-build overhead (context hashing, buildkitd solve/exec-pipe setup) on top of the paced transfer itself, so it runs measurably slower than the pacer's steady rate alone would suggest. Either figure can recur run to run depending on host load; treat "single-digit seconds" as optimistic and "~14s average" as the more representative number. 32 MiB/s, rather than a rate closer to the exec pipe's demonstrated ~120-260 MiB/s single-stream ceiling (a 585 MB `--output type=tar` export — unaffected by the pacer, since exports travel the unpaced direction — moved at ~266 MiB/s, and a 512 MiB/s pacer diagnostic moved the same 200 MiB context in ~1s, both with zero stalls), was chosen to leave margin for buildkitd being busy or a slow consumer on the far side; concurrent builds share one link and so one aggregate token bucket rather than multiplying the rate. Full reasoning and the evidence behind the margin are recorded on `TokenBucketPacer.DefaultBytesPerSecond`'s doc comment |
| Legacy `POST /build?version=2&session=...&remote=client-session` clients are not supported | Old `docker-py` (`images.build(..., version='2')`) and some `Docker.DotNet` build paths post the context over a separate `/session` upload instead of the request body. The daemon ignores `version` and always runs the classic single-shot build, which rejects `remote=client-session` outright with a 501 (`ImageManager.BuildAsync`, `request.Remote`) rather than fetching the context over `/session`. Evaluated and closed as won't-do (cider-ger.14): wiring it through the daemon's BuildKit control-proxy plumbing would mean pulling the `Solve`/session-attach helpers off the real inbound gRPC call context they depend on today, which risks the modern, working `buildx`/`/grpc`+`/session` path for a compatibility shim that the current `docker` CLI (v29.7.2) never even exercises. `docker build`/`buildx build` and everything under [Building images](#building-images) are unaffected |
| No pause/unpause | `POST /containers/{id}/pause` and `/unpause` return 501 — there is no apiserver route for it, and no cgroup-freezer equivalent inside Apple's guest init (`vminitd`); the closest approximation, `SIGSTOP`/`SIGCONT` to just the container's init process, freezes PID 1 but not its whole process tree for most images, so it is not offered as a substitute |
| No `--privileged` | Mapped to `--cap-add ALL` plus empty masked/read-only path lists — `--masked-path NONE --read-only-path NONE` over the CLI fallback, typed empty `maskedPaths`/`readonlyPaths` arrays over XPC, same semantics either way — which is not a true privileged mode; Docker-in-Docker and some device access will not behave |
| No `--network host` / `container:*`; `none` only on the XPC transport | `host` and `container:<id>` are rejected with 400 on both transports. `network_mode: none` works on `runtime.transport: xpc` (the container is created with zero attachments and has no eth0) and is still rejected with 400 on `runtime.transport: cli`, which has no way to ask `container create` for zero attachments. A compose service using `network_mode: host` does not come up. If a `network_mode: none` create on the XPC transport has to fall back to the CLI mid-request (no merged entrypoint, or the apiserver reports itself unavailable) — the CLI has no flag for zero attachments either, so silently attaching the default network is not an option — the create is refused instead, with 501 (`RuntimeErrorKind.NotSupported`, `GuardNoNetworkFallback` in `XpcContainerRuntime.Create.cs`), not the 400 a `none` request gets everywhere else |
| `network connect`/`disconnect` only before the first start | Apple fixes a container's networks at create time, so both work on a container that was created and never started (the daemon deletes and re-creates it with the new network list — over XPC that is a `containerDelete` + `containerCreate` pair with the config already in hand, no CLI arg round-trip) and return 501 with an explanatory message afterwards. `disconnect` will not drop a container to zero networks; use `network_mode: none` at create time on the XPC transport instead |
| Every container is a VM | Each container gets its own lightweight VM. Apple's own default is 4 CPUs / 1 GiB plus one extra `cpuOverhead` core per VM; Cider passes its own, more conservative default of 2 CPUs / 2 GiB unless the container asks for something else (`defaultCpus`/`defaultMemoryBytes`, see [Configuration](#configuration)). Many services at once cost real RAM either way, and it only grows: memory a guest frees internally is never returned to macOS while its VM lives (Apple's own `docs/technical-overview.md`), so a long-lived service's VM accumulates RSS until it restarts |
| Exit codes can be lost | On the XPC transport the daemon captures a container's exit code with `containerWait`, which is not tied to who started the container — if the daemon restarts while the container is still running, it simply re-issues `containerWait` for it and recovers the real exit code once the container actually exits (see [How it works](#how-it-works)). Only a container that exited *during* the daemon's downtime is unrecoverable — Apple stores no post-mortem exit code anywhere once the runtime helper for it has shut down. On `runtime.transport: cli` the older, blunter loss still applies: the exit code comes from a held `container start -a` child, so any daemon restart while that container is running loses it outright |
| Foreign containers' logs are one merged stdout+stderr stream, with no history | `docker logs` now works for containers the daemon did not start too, via the apiserver's own `containerLogs` fd — but that fd is one file Apple's runtime always tees stdout *and* stderr into together, so the streams cannot be told apart, and the file is truncated on every container start, so nothing survives a restart. Cider's own log store still keeps stdout/stderr separated with history, for containers it started itself |
| `docker commit` / `import` flatten the image | Apple has no commit primitive, so both export the container's whole root filesystem and load it back as a **single-layer** OCI image: real and runnable, but sharing no layer with its parent. On a *stopped* container this export no longer boots a VM at all — the daemon (over XPC, `containerExport`) reads the ext4 rootfs image directly, so `commit`/`export` on a stopped container is now cheap; a running container still needs a live snapshot. `--change` accepts `CMD`, `ENTRYPOINT`, `ENV`, `EXPOSE`, `WORKDIR`, `USER`, `LABEL` and `VOLUME`; anything else is a 400. `docker import <url>` (as opposed to `docker import -`) is not supported |
| `docker image prune` / `docker system prune` delete images but never compact the shared store — physical blob reclaim is Apple's own `container image prune`, run by you | By design (cider-ede.41, planner-ruled): cider used to finish every prune with Apple's whole-store orphaned-blob sweep, and that sweep is only safe when the *entire machine* is quiescent — the Apple content store is shared by every cider daemon, Apple's own CLI and any other client at once, and a sweep in one process deletes another process's just-written, not-yet-committed pull blobs. That is exactly the corruption that hit this machine's store three times in one day, and it was reproduced on demand in ~2 seconds by two daemons with cider's in-process sweep/write gate enabled in both: one pulling, the other looping a prune whose filter matched nothing (so the sweep was the only store-touching verb). No client-side lock can close that window, because the non-atomicity is Apple's (a pull writes blobs before committing its index entry, and the sweep treats those blobs as orphans). So cider's prune now only deletes the matched images (a per-image reference drop, never a sweep) and reports `SpaceReclaimed` honestly: on the default XPC transport a delete frees no blob bytes, so it reports 0. To actually reclaim disk, run Apple's own `container image prune` yourself — preferably on a quiet machine (no cider daemons or other `container` clients pulling), because Apple's sweep races any concurrent pull machine-wide the same way, and it also aborts wholesale on the first dangling content entry it meets instead of skipping it with a warning. Residual: on the CLI transport (`runtime.transport: cli`, or the XPC transport's apiserver-unavailable delete fallback) Apple's `container image delete` sweeps inside its own binary with no flag to disable it — cider cannot remove that sweep, only document it |
| Only the `json-file` log driver | Another `HostConfig.LogConfig.Type` is rejected at create — an unknown name with dockerd's own 400, a driver dockerd has but this daemon cannot honour (including `none`) with a 501 — rather than being accepted and silently logged json-file anyway |
| Volumes have only the `local` driver | `POST /volumes/create` with any other `Driver` is a 404, in dockerd's plugin-lookup wording |
| Volumes are single-attach ext4 images | Apple volumes are sparse ext4 disk images (512 GiB virtual size by default), not host directories: you cannot read one from the host, only through a container. Bind mounts behave normally |
| A static container IP cannot be honoured | Apple has no `--ip`, so `EndpointsConfig[net].IPAMConfig.IPv4Address` is not applied and the runtime picks the address. A malformed address, or one outside the network's subnet, is still rejected at create with dockerd's wording |
| No `diff`, best-effort `top` | `changes` is not implemented (Apple exposes no layer view); `top` runs `ps` inside the container as an approximation |
| Restart policies and healthchecks are emulated | The daemon supervises `always`/`unless-stopped`/`on-failure` restarts and runs healthcheck probes itself; Apple `container` has no native support for either. On the XPC transport this got more reliable across a daemon restart: a still-running container's exit is now actually waited for (see the exit-codes row above) and republished as a normal `die` event, so a `restart: always` container that is still running when the daemon comes back and exits afterwards is picked back up correctly, instead of the daemon never finding out. A container that exited *during* the daemon's downtime is still not restarted: reconcile records it as `exited` with `exit code unknown (daemon restarted)` and raises no `die` state change, so the restart supervisor never sees it (same limit as the exit-codes row above) |
| No swarm | Every `/swarm`, `/services`, `/tasks`, `/nodes`, `/secrets`, `/configs`, `/plugins` route answers with a clear "not supported" error instead of a raw 404. Apple `container` 1.3 ships its own local single-node orchestration answer, the `container k8s` plugin, if that shape fits — this daemon does not proxy to it |
| IPv6 management is limited | Container IPv6 addresses are reported: `docker inspect` surfaces `NetworkSettings`/`EndpointSettings` `GlobalIPv6Address` from Apple's own `ipv6Address`, and `docker network create --subnet-v6`/`-6` is honoured and passed straight through as `container network create --subnet-v6`. `IPv6Gateway` is always empty though, and not just unpopulated by an oversight: Apple's XPC `Attachment` type has no per-attachment IPv6 gateway field at all to read (`docs/spikes/xpc/02-apiserver-xpc-protocol.md` §2.2), so there is nothing upstream to plumb through. Also still missing: no `--ip6` (no field to request a static IPv6 address, the same structural gap as the static-IPv4 row above), and IPv6-only or dual-stack publish/connect behaviour beyond what's above is unexercised |
| `host.docker.internal` reaches only `0.0.0.0`-bound services, unless you opt in | A host service bound to `127.0.0.1` only is not reachable from containers by default — and many dev servers bind loopback by default. `cider host-loopback enable` closes the gap with a pf redirect, but treat it as **experimental**: it has two known defects (cider-ede.22) that can leak a pf reference-count token on every `enable`; see [Reaching loopback-only host services](#reaching-loopback-only-host-services) for what that means before turning it on |
| Published ports flow through the daemon (default `proxy`) | Traffic is relayed by the daemon process, not by the kernel or Apple's forwarder, so it stops when the daemon stops and adds one userland hop. `portPublishing: "apple"` avoids the hop but depends on macOS Local Network permission |
| `docker cp` **into** a created container is emulated | Apple `container cp` refuses a container that is not running, so a tar `PUT` into a created container is staged under the data dir and bind-mounted in when the container starts (this is how .NET Aspire injects its dev certificates). Up to 64 files are mounted; beyond that they are copied in immediately after the start instead |
| `docker cp` **out of** a stopped container is emulated | The same refusal, in the other direction: `container cp`/`containerCopyOut` both guard on the container being started. The path is selected out of the container's own rootfs export (over XPC, the same no-VM-boot `containerExport` the commit/import row above uses, so at least no subprocess or VM boot on the way), which still costs O(rootfs) rather than O(path). A path that reaches its target through a symbolic link inside the container is not resolved <!-- wording taken from the cp-out implementation's own note; src/Cider.Core/Services/ContainerManager.Archive.cs --> |
| `HEAD /containers/{id}/archive` of a non-root path is O(image) | The stat is served by copying the path out of the container; `/` is answered synthetically, but a stat of a large subtree copies that subtree |

## How it works

```
docker CLI / compose / Testcontainers / Aspire
          │  HTTP/1.1 over unix socket
          ▼
   cider (Kestrel, in-process)
      │                                     │
      │ control plane                       │ data plane, on the daemon's own sockets:
      │ XPC to the apiserver (primary),     │  · published ports: host bind → <containerIP>:<port>
      │ `container` CLI only as fallback    │  · DNS for guests on 0.0.0.0:10053 + CoreDNS forwarder
      ▼                                     │
   com.apple.container.apiserver            │ straight to the guest VM's IP, no Apple forwarder
   com.apple.container.core.…-images        ▼
      │  mach services, over libxpc
      ▼
   container-apiserver ── one lightweight VM per container
```

The daemon is a single ASP.NET Core (Kestrel) process listening on a unix socket. Its **control
plane** — everything that creates, starts, stops, inspects, execs into or lists containers, images,
networks and volumes, plus `docker cp`, image save/load and (partly) `build` — talks directly to
Apple's own daemons over XPC: `com.apple.container.apiserver` for containers/networks/volumes, and the
separate `com.apple.container.core.container-core-images` mach service for images, both reached
through a .NET P/Invoke `libxpc` client (`src/Cider.AppleContainer/Xpc`) — no Swift bridge, no helper
process, and no CLI subprocess for anything this path covers. This is the primary transport
(`runtime.transport: auto`, the default) whenever a `ping` reports an apiserver version ≥ 1.2.0 —
measured floor from the probe that proved the approach out: `ping` ~25 µs, a container list ~0.1 ms, a
create ~11 ms median (`docs/spikes/xpc/04-dotnet-xpc-probe-report.md`). The `container` CLI is kept as
a fallback only: per call, whenever XPC itself reports the apiserver unavailable; unconditionally for a
handful of members with no reason to move (`docker login`'s credential store, the classic builder,
starting the builder VM — the exact, current list is `FallbackMatrix`, also what `cider status` prints
as `fallback: ...`); and for the whole runtime when the apiserver is unreachable, older than the
version gate, or `runtime.transport` is set to `cli` outright — see [Configuration](#configuration).

Container lifecycle ownership follows the same split. Over XPC the daemon calls `containerBootstrap`
with pipes (or a PTY) it holds itself, then `containerStartProcess`, then `containerWait` for the exit
code — there is no held `container start -a` child process, so a hard-killed daemon leaves nothing
running with no parent and cannot wedge the Apple runtime the way it used to (see
[Troubleshooting](#troubleshooting)). If a container is still running when the daemon comes back up,
`containerWait` is simply re-issued for it and the real exit code is recovered even though the daemon
was not there to see the exit happen live, then republished as an ordinary `die` event so
`always`/`on-failure` restarts and `--rm` behave as if the daemon had never gone away — only a
container that exited *during* the daemon's downtime is genuinely unrecoverable (see
[Limitations](#limitations)). None of that holds under `runtime.transport: cli`, or for a call that
fell back mid-flight: the CLI fallback still shells `container start -a` and still holds its child
process the old way.

The **data plane** deliberately does not go through Apple either way. In the default `proxy` mode the
daemon binds the host side of every `-p` mapping itself and forwards straight to the container's VM
address, and container DNS is answered by the daemon's own DNS server through a per-network CoreDNS
forwarder. Both exist because Apple's own forwarder is refused by macOS Local Network privacy on some
machines, and because port 53 on the host is not free either way — macOS's own resolver machinery
holds it, not Apple's container networking (Apple's own embedded resolver listens on
`127.0.0.1:2053`/`:1053`, never on 53).

Docker's two "hijacked" endpoints (`POST /exec/{id}/start`, which always carries a body Kestrel
refuses to upgrade, and `POST /containers/{id}/attach`) — and BuildKit's two, `POST /grpc` and
`POST /session` — are intercepted at the connection level before Kestrel's HTTP layer sees them, so
the daemon can hold the raw stream open for the lifetime of the process instead of tearing it down
the moment a client half-closes its write side. A request that actually carries the `h2c` upgrade
still gets hijacked when `builder.enabled` is `false`, and is answered the same `404` a disabled
BuildKit gives everywhere else; a plain (non-upgrade) POST to `/grpc` or `/session` is never
recognized as a hijack at all, so it falls through to the ordinary Kestrel route instead, which
answers a `400` diagnostic when BuildKit is enabled and the same `404` when it isn't.

BuildKit's own two endpoints are hijacked the same way, but each becomes a different role once
upgraded. `POST /grpc` is BuildKit's control-plane connection: buildx dials it, the daemon answers
the `101` and hands the raw connection to Kestrel's own HTTP/2 engine as a *server* over an in-process
tunnel, where `ControlProxyService` answers three RPCs itself — `Solve`, `ListWorkers`, `Session` —
and forwards everything else (`Status`, `Info`, `DiskUsage`, `Prune`, build history, `LLBBridge`,
`Content`, trace export — what `buildx du`/`prune`/`inspect` and BuildKit's own frontend rely on)
untouched to buildkitd. `POST /session` runs the opposite direction — BuildKit is the gRPC *server*
there — so the daemon dials out over that same connection instead and parks it in a session registry
that a later `Solve` looks its session id up in (`bake` shares one session across its whole matrix of
targets rather than opening one per target).

Every call actually reaching buildkitd goes over one long-lived link the daemon keeps open into
Apple's builder VM: `container exec -i buildkit buildctl dial-stdio`, with a gRPC channel run over
that exec's own stdio, paced by a token bucket on what the daemon sends and watched by a stall
detector — a call that stops making progress restarts the builder (`container builder start`) before
the next dial, clearing a wedged exec the same way a hung one has to be cleared by hand (see
[Troubleshooting](#troubleshooting)). The `buildkit` container this drives is started on demand and
hidden from `docker ps`/`inspect` the same way the DNS forwarders are.

`Solve` gets one rewrite before it reaches buildkitd: buildx's docker driver always asks for a
`moby`-typed exporter, which stock buildkitd does not implement, so the daemon turns every one into
a `docker` exporter (a tar) in place and arms a daemon-owned buildkit session
(`SessionBridge`/`FileSendCapture`) to capture that export's `FileSend`/`DiffCopy` traffic by
exporter id, rather than letting it go anywhere. Once the real Solve returns, the captured tar is
handed to the same load path `docker load` and the `docker commit` export use, tagged with whatever
name(s) the caller asked for (or a private synthetic tag for an untagged build, exactly like the
classic builder), and a `build` event is published — and `ListWorkers` responses have
`org.mobyproject.buildkit.worker.snapshotter` stripped from every worker record (that label is what
tells buildx cache export, multi-platform and attestations might work, none of which do here) before
they reach buildx.

State (container, network and volume records) is persisted as JSON under `~/.cider/state`; captured
container logs live under `~/.cider/logs`; the daemon's own launchd log is `~/.cider/daemon.log`.

| Project | Purpose |
|---|---|
| `src/Cider.Core` | Docker wire DTOs, the `IContainerRuntime` abstraction, state stores, managers (container/exec/image/network/volume/system), events, logs, health, restart supervision — no ASP.NET dependency |
| `src/Cider.AppleContainer` | The `IContainerRuntime` implementations: `Xpc/` talks to the apiserver directly (the primary transport — client, wire models, transport selection, `FallbackMatrix`) over a libxpc P/Invoke layer, wrapping the CLI-based implementation (process launching, pty handling, JSON parsing, error mapping) as its own fallback |
| `src/Cider.Dns` | Standalone DNS server (UDP + TCP), message codec, resolver interface — no dependency on Core |
| `src/Cider.Daemon` | The `cider` executable: Kestrel hosting, the hijack interceptor, Docker API routes, the BuildKit control proxy (`src/Cider.Daemon/BuildKit`), and the `serve`/`install`/`uninstall`/`status`/`sync` verbs |

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

### Apple's image store has a dangling content reference

Apple's own store can end up naming a content blob in `state.json` that is no longer actually on
disk — usually after an interrupted operation on the Apple side. Symptom: `container image ls`
(and cider) fail with

```
Error: content with digest sha256:<hex>
```

While the store is in that state, `docker images` may list fewer images than are really there, or
none at all — cider logs a Warning naming the digest rather than failing the whole listing, but it
cannot repair another tool's store, so the listing is genuinely short until the entry is cleaned up.
`container image inspect <ref>`/`container ls -a` are unaffected — only the listing route is
poisoned by the one bad entry.

Repair it with Apple's own tooling:

```bash
container image prune
# or, once the offending image is identified:
container image delete <ref>
```

cider never writes to Apple's `state.json` — it does not and will not attempt to repair this itself.

### A hard-killed daemon can wedge the Apple runtime (`runtime.transport: cli` only)

This is a CLI-transport problem. On the default XPC transport the daemon never spawns a
`container start -a` child in the first place (see [How it works](#how-it-works)), so there is no
process left behind for a hard kill to orphan, and the rest of this section does not apply. It still
applies when the daemon is running with `runtime.transport: cli`, when the version gate fell back to
the CLI because the installed Apple `container` is older than 1.2.0, or if you are troubleshooting a
cider from before 0.2 (the CLI-only architecture, before the XPC transport existed) — `cider status`
prints which transport is actually active.

Under the CLI transport, the daemon holds one `container start -a <name>` child process per running
container, to own that container's lifetime and exit code. A clean shutdown disposes them. A **hard
kill** — `pkill`, a crash, a test harness tearing the process down — does not: the children survive
with no parent, keep their containers running, and hold the networks those containers are attached to.
Once that debris accumulates, `container network create` can hang for 300+ seconds *with no Cider in
the path*, `container stop` hangs, and `container network delete` answers `network <n> has a pending
operation`.

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

Orphaned `cider-dns-*` CoreDNS forwarders are reaped automatically: since cider-0o3, every daemon
startup runs `DnsForwarderService.ReapOrphanedForwardersAsync`, which stops and deletes any forwarder
whose owning daemon's data dir no longer exists on disk — so starting or restarting any Cider instance
cleans these up the same way it recovers the held children above, with no manual step required. A
forwarder belonging to a still-live daemon (its data dir still on disk) is never touched, including a
second daemon running against a different `--data-dir`. For a machine where no daemon will be started
again — the same case the recovery list above is for — remove one by hand instead:
`container stop <name> && container delete -f <name>`; the daemon starts a fresh forwarder on demand.

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

### The builder VM won't start, or a build hangs

`container builder status` shows whether Apple's builder VM (`buildkit`) exists and is running; the
daemon runs `container builder start` itself whenever a build needs it and the VM isn't already up,
so a manual `container builder start` is mostly for diagnosing why that failed. A build that hangs
after an earlier one stalled is usually a wedged `container exec` into that VM: the daemon detects a
call that stops making progress and restarts the builder itself before the next build (see
[How it works](#how-it-works)), so this is normally self-healing. A `builder.cpus`/`builder.memory`
change (see [Configuration](#configuration)) only takes effect the next time the VM starts from
stopped — `container builder start` on an already-running VM is a no-op, so apply a resize with
`container builder stop` (Apple's own CLI) and let the next build start it again.

Custom builders created while experimenting with BuildKit before the default builder worked can be
left behind as `buildx_buildkit_*` containers on the host's own container runtime (`docker buildx ls`
shows them); they are unrelated to Cider's `buildkit` VM and are removed the ordinary way:

```bash
docker buildx rm <name>
```

## Testing

```bash
dotnet test Cider.sln
```

Unit and integration tests against a fake in-memory container runtime, plus — when the real `docker`
CLI is on `PATH` — an in-process daemon over a temp socket: **1338 passing and 29 skipped** in
`Cider.Tests` (the 29 need the opt-in below) and **16** in `Cider.Dns.Tests`, on both target
frameworks.

Tests against the real Apple `container` runtime are opt-in and slower:

```bash
CIDER_E2E=1 dotnet test Cider.sln
```

That adds the adapter tests in `tests/Cider.Tests/AppleContainer/` (29) and the
`tests/Cider.E2E.Tests` suite (69, including a handful of always-on fixture-ownership unit tests) —
**98 tests today**, up from 33 the last time this section was checked, driving the real `docker` CLI,
`docker compose`, Testcontainers and a .NET Aspire AppHost against an in-process daemon on the real
runtime. A bare `CIDER_E2E=1` run against the default `auto` transport skips at least four of them:
the `apple`-mode port characterization (`CIDER_PORT_PUBLISHING=apple`), the two XPC-only fast-path
latency characterizations in `PerfSmokeTests` (`CIDER_RUNTIME_TRANSPORT=xpc` set *explicitly* — `auto`
resolving to XPC at runtime does not count, since the attribute cannot see that resolution), and the
large build-context characterization (`CIDER_E2E_LARGE=1`). Exact pass/skip counts and timing depend
on what's already on the machine (see the per-TFM note below), so no fixed "last run" number is
quoted here.

The Testcontainers and Aspire fixtures (`tests/e2e-testcontainers`, `tests/e2e-aspire`) sit outside
`Cider.sln` on purpose and are built by those tests on demand; the Aspire fixture pins SDK 10.0.302
in its own `global.json`, because `Aspire.AppHost.Sdk` 13.5.0 cannot build on the .NET 11 preview SDK
the repository root rolls forward to.

The E2E suite runs against whichever runtime transport the in-process daemon under test picks —
`auto` by default, pinnable with `CIDER_RUNTIME_TRANSPORT=xpc` or `=cli` (`DaemonFixture.Transport`),
which is exactly what CI's `.github/workflows/e2e.yml` sets, running the whole suite twice (once per
transport) against the newest Apple `container` release cider has been exercised against
(`ApiServerVersion.Tested`). The XPC-only `PerfSmokeTests` (`tests/Cider.E2E.Tests/PerfSmokeTests.cs`)
guard that the fast create path stays fast and skip outright under `cli`. To characterize the *older
minimum* supported apiserver (`ApiServerVersion.Minimum`) instead of CI's pinned newest release,
install that older signed `.pkg` from https://github.com/apple/container/releases locally and run
`CIDER_E2E=1 CIDER_RUNTIME_TRANSPORT=xpc dotnet test tests/Cider.E2E.Tests` against it — see
`tests/compat/README.md`'s "Runtime transport" section for the exact commands.

**Run one target framework at a time whenever real Apple `container` state is involved**
(`CIDER_E2E=1`, and everything under `tests/compat`) — pass `-f net10.0`/`-f net11.0` explicitly rather
than letting `dotnet test`'s default multi-targeting run both together, and never invoke two such runs
concurrently either. CI never does: `.github/workflows/e2e.yml` always pins `-f net10.0` for exactly
this reason. It is structural, not a convenience — there is exactly one `apiserver` per user, on one
fixed mach service name, so the content store cannot be partitioned per test run, and two concurrent
runs, whatever their TFM, end up sharing one store with whatever images and containers you actually
have on this machine. A run that only fails when something else is running against the same store at
the same time is not a regression to chase: every such failure observed here passed cleanly on an
immediate, isolated re-run.

## License

MIT — see [`LICENSE`](LICENSE). Copyright ChilliCream Inc.
