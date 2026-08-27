# Fresh-install checklist (cider-5jm)

Preparation for cider-fpt, the user-owned task that merges `mst/v-0-2`, tags a release, and
walks this checklist for real. This document is that preparation: a version decision with its
rationale, a read-only audit of `.github/workflows/release.yml`, a README accuracy check, and —
the actual deliverable — the exact command sequence below with expected output, expected failure
shapes, and what each would mean.

**Read this whole section before running anything.**

- **This path has never been walked end to end.** `cider install`, `uninstall` and `status` are
  covered by unit tests, but nobody has run `brew install` → `cider install` →
  `docker context use cider` → a real container → a real build against a binary built from this
  branch. README.md:100–103 says the same thing about the install flow in general, and
  cider-8ok's close reason records that even the narrower `ProcessType: Interactive` fix inside it
  was never verified live — only unit-tested — because doing so would have restarted this
  machine's real daemon. **You are the first person to run this sequence.** A failure at any step
  below is a finding to file as a task, not evidence you did something wrong.
- **This machine is not actually clean.** It already has Cider 0.1.4 installed via Homebrew
  (`88c4d66`, 2026-08-24), with a launchd agent and a `cider` docker context already in place.
  "Fresh install" in the title is what a new user would experience; on *this* machine the correct
  first command is an upgrade, not `brew install` — see Step 1.
- **Every "expected output" below is either a literal string read out of the current source, or
  explicitly marked UNCONFIRMED.** Nothing here was invented by guessing what a command probably
  prints. Where the exact bytes depend on this host (a version number, a PID, a timing), the
  *shape* is given and the variable part is called out — do not treat a differing PID or timing as
  a failure.

---

## 1. Version decision

**Recommendation: tag `0.2.0`, not a patch release.**

Rationale: every prior tag (0.1.0–0.1.4) shipped while cider only ever spoke to Apple `container`
through the CLI subprocess. This run added a second, now-default transport (XPC to
`com.apple.container.apiserver`, `runtime.transport: auto`) and, riding on it, changed behaviour a
client can observe: image ids are now content-addressed instead of Apple's unstable index digest
(cider-ger.19, cider-ede.29), `docker wait`/exit-code recovery for a container the daemon adopts
after restarting now actually completes instead of being lost (cider-ede.7 and the XPC
`containerWait` path), `network_mode: none` is newly accepted (on the XPC transport — cider-ede.35),
and the classic build path now reports a content-addressed config id instead of a manifest digest
(cider-ger.20). None of that is a bug fix to a released behaviour (semver's patch category) or a
strictly additive capability with no observable change to what was already there (minor, in the
loosest reading) — it is a different answer to "what id does this image have" and "did my container
actually finish exiting" for anyone who scripts against those values, which is exactly what a minor
version bump signals under 0.x semver (patch = fix, minor = feature/behaviour change, both without
an API-breaking promise pre-1.0). This was not decided from nothing: README.md:570, written earlier
in this same run, already refers to "a cider from before 0.2 (the CLI-only architecture, before the
XPC transport existed)" — the codebase already treats 0.2 as the line the XPC transport crossed;
this decision just makes that explicit at the tag. `0.2.0` rather than `0.2.0-rc1` or similar,
because nothing about the release pipeline or the feature set is provisional — see §2 below, the
0.1.x line already exercised the exact same pipeline successfully.

No in-repo file encodes this. `Directory.Build.props` and every `.csproj` under `src/` were checked
(`grep -rn "<Version>" **/*.csproj`) and none of them set `Version`/`VersionPrefix`/
`PackageVersion` — the published binary's version comes entirely from `-p:Version="${VERSION}"` in
`release.yml`, itself taken from the pushed git tag (`GITHUB_REF#refs/tags/`). **There is nothing
to bump in this repository. The version decision is entirely which string to use for the git tag
in cider-fpt** — `0.2.0`.

## 2. `release.yml` wiring — read end to end, not run

Verdict: **sound**, with one known, non-fatal gap. This was not just re-read as YAML; it was cross-checked against real history — `mst/v-0-2`'s copy of `.github/workflows/release.yml` and `.github/actions/` is **byte-identical to `main`'s** (`git diff main mst/v-0-2 -- .github/workflows/release.yml .github/actions/` is empty), and the exact file on `main` published tag `0.1.4` successfully two days ago (run `32773684955`, `2026-08-24T20:29:40Z`): draft release created, binary published/signed/notarized, zip attached, draft published, and the Homebrew tap updated (`ChilliCream/homebrew-tools` commit "🍎 Update cider to 0.1.4", `2026-08-24T20:30:03Z`). Pushing the next tag runs the same file.

What was checked and what it means:

- **Every pinned Action SHA resolves.** All five (`actions/checkout`, `actions/setup-dotnet`,
  `actions/upload-artifact`, `actions/download-artifact`, `actions/create-github-app-token`) were
  looked up against GitHub's API (`gh api repos/<owner>/<repo>/commits/<sha>`) and all five exist.
  Not a rubber-stamp: a dangling pin here is exactly the kind of failure a read-only review is for.
- **Every secret the workflow references was checked against `gh secret list --repo
  ChilliCream/cider`.** Nine of the twelve exist at the repository level:
  `ACTIONS_APP_CLIENT_ID`, `ACTIONS_APP_PRIVATE_KEY`, `APPLE_DEVELOPER_CERTIFICATE_BASE64`,
  `APPLE_DEVELOPER_CERTIFICATE_IDENTITY`, `APPLE_DEVELOPER_CERTIFICATE_PASSWORD`,
  `APPLE_DEVELOPER_CIDER_APP_SPECIFIC_PASSWORD`, `APPLE_DEVELOPER_ID_EMAIL`,
  `APPLE_DEVELOPER_TEAM_ID`, `TEMPORARY_KEYCHAIN_PASSWORD` (`GITHUB_TOKEN` is automatic and always
  present). **`APPLE_DEVELOPER_INSTALLER_CERTIFICATE_BASE64`,
  `APPLE_DEVELOPER_INSTALLER_CERTIFICATE_PASSWORD` and `APPLE_DEVELOPER_INSTALLER_CERTIFICATE_IDENTITY`
  do not appear in that list.** Listing organization-level secrets returned a 403 for this token
  (`admin:org` scope not granted), so an org-level secret supplying these three cannot be ruled out
  from here — but the repo-level list is complete, and it does not have them.
  - **Effect, confirmed by the same historical run, not just inferred from the YAML:**
    `release-context` computes `has_installer_cert` from
    `secrets.APPLE_DEVELOPER_INSTALLER_CERTIFICATE_BASE64 != ''`; `build-pkg`'s
    `if: needs.release-context.outputs.has_installer_cert == 'true'` then evaluates false, so
    `build-pkg` **skips** rather than fails. This is exactly what happened on the real 0.1.4 run
    (job shows `-` / skipped in `gh run view 32773684955`). `publish-release`'s
    "Download .pkg (if built)" step is itself guarded on `needs.build-pkg.result == 'success'`, so
    it also skips cleanly, and the checksum step's `cider-*.pkg` glob (with `nullglob` set) just
    contributes nothing. **Net effect: the release ships the signed, notarized zip but no `.pkg`
    installer, silently — no error, no warning in the run.** This does not block the release: the
    Homebrew formula (`ChilliCream/homebrew-tools`'s `release-cider.yaml`, read directly) only ever
    downloads `cider-osx-arm64.zip`, never a `.pkg`, so `brew install`/`brew upgrade` is unaffected.
    If a signed `.pkg` installer is wanted for some other distribution channel, that needs the three
    installer-cert secrets added at either the repo or org level — otherwise every future tag will
    keep silently skipping it, which is worth knowing before assuming a `.pkg` exists on a release
    page.
- **Two prior tags failed on `main`, and both causes are gone.** `0.1.0` failed because
  `publish-release`'s "Download .pkg (if built)" step had no result guard yet and tried to download
  an artifact that was never uploaded (`build-pkg` was skipped even then) — the guard
  (`if: needs.build-pkg.result == 'success'`) is present in the version read for this task. `0.1.3`
  failed because `ChilliCream/homebrew-tools` did not yet have `release-cider.yaml` on its default
  branch when the dispatch ran (`HTTP 404: workflow release-cider.yaml not found`) — that workflow
  was added minutes later (`homebrew-tools#1`, merged `2026-08-24T20:23:38Z`) and is present now
  (confirmed by fetching it directly from the API), and it is what 0.1.4 dispatched successfully.
  Neither failure mode is live.
- **Paths this run touched still match what `release.yml` references.** `src/Cider.Daemon` (the
  `dotnet publish` project) and `scripts/build-pkg.sh` both still exist at the paths the workflow
  expects — nothing moved under it.
- **The pinned .NET SDK (`10.x` plus `11.0.100-preview.6.26359.118`) is the same pin used in
  `e2e.yml`/`e2e-large.yml` and is what actually built 0.1.4** — not just internally consistent,
  actually exercised.
- **Not independently confirmed:** whether the GitHub App behind `ACTIONS_APP_CLIENT_ID`/
  `ACTIONS_APP_PRIVATE_KEY` is still installed on `ChilliCream/homebrew-tools` with `contents:
  write` — querying the installation endpoint with this session's token returned "A JSON web token
  could not be decoded" (wrong auth shape for that endpoint from a classic PAT, not a real answer).
  This is corroborated rather than proven: the 0.1.4 run's "Update Homebrew tap" job succeeded and
  produced a real commit in that repo two days ago, which only happens if the installation and
  permissions were correct at that time. It could have changed since. Treat as UNCONFIRMED for the
  *next* tag specifically, sound as of the last real run.

## 3. README accuracy check

Audited section by section — Requirements, Install, `cider install`, Configuration table, What
works, Building images, How it works, Troubleshooting — against the current source
(`Program.cs`, `CiderOptions.cs`, `LaunchdInstaller.cs`, `ApiServerVersion.cs`, `FallbackMatrix.cs`)
and specifically against the five behaviour changes named for this task (default transport,
image-id derivation, `docker wait` semantics, `network_mode: none`, the classic build path's
reported id). **No edit was needed.** Specifically checked and found already correct:

- `runtime.transport` default is documented as `auto` in the Configuration table, matching
  `CiderOptions.RuntimeTransport`'s actual default.
- The "before 0.2 (the CLI-only architecture, before the XPC transport existed)" line
  (README.md:570) and the `cider status`-prints-the-active-transport claim both match
  `Program.StatusAsync` as written.
- `network_mode: none` is documented precisely, including that it only works on the XPC transport
  and the exact 501 fallback-refusal behaviour, matching `GuardNoNetworkFallback` in
  `XpcContainerRuntime.Create.cs` (read for context, not edited — out of this task's file scope).
- `docker wait` is listed as working; the "Exit codes can be lost" limitation row correctly scopes
  the *remaining* loss to the CLI transport and to a container that exited during downtime, matching
  the `containerWait`-recovery behaviour added this run.
- The classic build's reported id is not mentioned by name (it was never documented as a manifest
  digest to begin with, so there was no stale claim to correct) — no edit needed there either.
- `cider install`'s launchd/context/system-socket behaviour, the verb list, and the requirements
  (macOS 26, Apple `container` ≥ 1.2.x) all match `Program.cs`/`LaunchdInstaller.cs` and
  `ApiServerVersion.Minimum` (`1.2.0`).
- The Homebrew tap mapping (`chillicream/tools` → `ChilliCream/homebrew-tools`), the `docker`/
  `docker-compose` dependency claim, and the macOS 26 ("Tahoe") + arm64 requirement were all
  cross-checked directly against the tap's actual `release-cider.yaml` (fetched from
  `ChilliCream/homebrew-tools`) and match.

No product-change follow-up task is needed from this audit.

---

## 4. The checklist

Run these in order, on the machine that owns cider-fpt. Each step: the command, what a *success*
looks like, and what a *failure* looks like and would mean. Anything not literally sourced from
code is marked UNCONFIRMED.

### Step 0 — before any of this: cider-fpt's own prerequisites

Not part of this checklist's steps (they are cider-fpt's, not cider-5jm's), but nothing below makes
sense before they've happened: `mst/v-0-2` merged to `main` and pushed, tag `0.2.0` created and
pushed, and the `Release` workflow run for that tag finished (`gh run list --repo ChilliCream/cider
--workflow=release.yml` shows it `completed`/`success`, per §2 above with the `.pkg` job expected to
show as skipped, not failed).

### Step 1 — install or upgrade the binary

This machine already has `cider` 0.1.4 from Homebrew, so use the upgrade path, not a plain install:

```bash
brew update
brew upgrade cider
```

(On an actually clean machine with no prior install, the equivalent is `brew install
chillicream/tools/cider` — README's documented command, still correct for that case.)

- **Success looks like:** brew reports upgrading `chillicream/tools/cider` from `0.1.4` to `0.2.0`
  (or whatever tag was pushed), downloads `cider-osx-arm64.zip`, and installs it under
  `$(brew --prefix)/bin/cider`. `docker`/`docker-compose` are formula dependencies, so if either is
  missing brew installs them too — if they're already present (they are, on this machine) brew
  leaves them alone.
- **Verify:** `cider version` — first line is `cider <version>+<git sha>` per
  `Program.InformationalVersion()`/`ResolveGitCommit()`; UNCONFIRMED what the exact sha suffix will
  be until the tagged commit exists, but the `<version>+<40-hex-char-sha>` *shape* is fixed by the
  code, not a guess. Also run `brew list --versions cider` and confirm it says the new version, and
  `which -a cider` to confirm the resolved binary is brew's, not a leftover copy elsewhere on
  `PATH`.
- **Failure looks like, and means:**
  - `brew upgrade` says `cider` is already up to date, but `cider version` still prints `0.1.4`:
    the tap wasn't refreshed (`brew update` didn't run, or the tap needs re-adding) or brew resolved
    a different `cider` on `PATH` than the one it just built — check `which -a cider` and `brew
    --prefix`.
  - A checksum/SHA mismatch on the downloaded zip: the asset is corrupted or was tampered with in
    transit — stop, do not `--force` past it, and treat it as a release-pipeline finding, not a
    local retry.
  - `Error: chillicream/tools/cider: no bottle available!` or similar formula-resolution error:
    the tap itself is broken (check `ChilliCream/homebrew-tools`'s `Formula/cider.rb` directly) —
    this would mean the `update-homebrew` job in §2 did not actually leave a working formula behind,
    which is itself the finding to file.

### Step 2 — `cider install`

```bash
cider install
```

Prints each step live as it happens (from `LaunchdInstaller.InstallAsync`/`DockerContextInstaller`,
quoted verbatim from source — exit codes/paths/timings will match this host, the literal wording
will not vary):

```
Ensured data directory: /Users/<you>/.cider
Wrote plist: /Users/<you>/Library/LaunchAgents/com.chillicream.cider.daemon.plist
ProcessType changed: Background -> Interactive (restarting the daemon so the new resource class takes effect)
launchctl bootout gui/<uid>/com.chillicream.cider.daemon (exit 0)
launchctl bootstrap gui/<uid> /Users/<you>/Library/LaunchAgents/com.chillicream.cider.daemon.plist (exit 0)
launchctl kickstart -k gui/<uid>/com.chillicream.cider.daemon (exit 0)
Socket ready: /Users/<you>/.cider/docker.sock
cider daemon installed and running.
docker context update cider --docker host=unix:///Users/<you>/.cider/docker.sock (exit 0)
Docker context 'cider' ready. Run `docker context use cider` to select it.

Point Docker tooling at cider with either of:
  docker context use cider
  export DOCKER_HOST=unix:///Users/<you>/.cider/docker.sock
```

- **The `ProcessType changed: Background -> Interactive` line is expected and is itself a proof
  point** — this machine's existing 0.1.4 install used `ProcessType: Background` (cider-8ok), so
  this line confirms that fix is present in the installed binary. Its absence (no such line at all)
  would mean either the fix regressed or this run somehow re-installed with the old plist template.
- **`docker context update` (not `create`)** is expected here specifically because this machine
  already has a `cider` context from the 0.1.4 install; a genuinely clean machine would instead see
  `docker context create cider --docker ... (exit 0)`. Either is correct for its situation — seeing
  `create` here, on this machine, would be the surprising one (it would mean the old context was
  somehow lost).
- **Failure looks like, and means:**
  - `launchctl bootstrap ... (exit <nonzero>)` followed by `launchctl bootstrap failed: ...`: the
    plist itself is malformed, or something else (SIP, a stale service definition) is refusing the
    load — the stderr text captured after "failed:" names the real reason, read it rather than
    retrying blind.
  - `Timed out waiting for socket: ...` instead of `Socket ready: ...`: the daemon process started
    under launchd but never opened its socket within 10s — check
    `~/.cider/daemon.log` for what it did on startup; this is a real daemon-startup defect if the
    log shows a crash, not a timing fluke to just retry past.
  - `docker CLI not found; skipped context`: `docker` isn't on `PATH` even though brew was supposed
    to install it as a dependency in Step 1 — a formula dependency problem, not a `cider install`
    problem.

### Step 3 — point Docker at cider

```bash
docker context use cider
docker context ls
```

- **Success:** `docker context ls` lists `cider` with a `*` in the `CURRENT` column, `DOCKER
  ENDPOINT` reading `unix:///Users/<you>/.cider/docker.sock`.
- **Failure looks like, and means:** `docker context use cider` errors that the context doesn't
  exist — Step 2 did not actually create/update it; re-check Step 2's output for the docker-context
  lines rather than assuming this step is where the problem is.

### Step 4 — run a container (baseline proof the runtime itself works)

```bash
docker run --rm alpine:3.22 echo hello
```

- **Success:** the daemon pulls `alpine:3.22` (first run only), the VM boots, and `hello` prints to
  stdout before the process exits 0. Apple's own boot-spinner text ("Starting container [0s]" plus
  ANSI cursor codes) may appear ahead of the container's own output — README already documents this
  as expected, not a bug.
- **Failure looks like, and means:**
  - Hangs indefinitely at the pull or at container start: `container system start` may not have
    completed — `cider status`'s `apple:` line (Step 6) says whether Apple `container` itself is
    `running`; if it says `stopped`, that's the layer to debug, not cider.
  - `docker: Error response from daemon: ...` naming a specific Docker API error: read it literally,
    it is the daemon's own error text, not a connectivity problem.
  - No output at all and a nonzero exit with no error text: check `~/.cider/daemon.log` for a crash
    — this is the first real container the newly-installed daemon has run, so a crash here is a
    legitimate finding, not something to explain away.

### Step 5 — build an image (proves the BuildKit/`/grpc`+`/session` path — cider-ger)

```bash
mkdir -p /tmp/cider-smoke && cd /tmp/cider-smoke
printf 'FROM alpine:3.22\nRUN echo built-by-cider > /proof.txt\n' > Dockerfile
docker build -t cider-smoke-test .
docker run --rm cider-smoke-test cat /proof.txt
```

`Builder-Version: 2` is the default (`builder.enabled: true`), so this plain `docker build` already
goes through BuildKit and Cider's `/grpc`+`/session` proxy — no flag needed. This is the one named
check for the `cider-ger` epic.

- **Success:** `docker build` exits 0 and reports an image id/tag; `docker images` lists
  `cider-smoke-test`; the second command prints `built-by-cider`. The exact BuildKit progress-output
  formatting (`[+] Building ...` lines, step timing) is buildx-version-dependent — do not treat its
  literal text as a pass/fail signal, only the exit code and the printed proof file content.
- **Failure looks like, and means:**
  - `buildx failed with: ... the default builder is unsupported`, or buildx silently uses a
    different (non-default) builder: `Builder-Version` on `/_ping` isn't reporting `2` — check
    `builder.enabled`/`CIDER_BUILDKIT` in the running config, this would mean BuildKit did not
    actually ship enabled in this release.
  - The build hangs: per README's Troubleshooting, this usually means Apple's builder VM
    (`buildkit`) isn't starting — `container builder status` (Apple's own CLI) says whether it
    exists/is running.
  - Build succeeds but `docker run --rm cider-smoke-test cat /proof.txt` fails or prints nothing:
    the image was built but not loaded/tagged correctly — a defect in the BuildKit→`docker load`
    handoff, worth filing directly since it would mean the epic's core mechanism has a real gap.

### Step 6 — `cider status` (proves the XPC transport — cider-ede)

```bash
cider status
```

Expected shape, quoted from `Program.StatusAsync`:

```
socket:    /Users/<you>/.cider/docker.sock
data dir:  /Users/<you>/.cider
daemon:    responding
launchd:   installed, running (pid <pid>)
transport: xpc, apiserver <semver>, fallback: BuildImageAsync, LoginAsync, StartBuilderAsync
apple:     container <version>, running, kernel <kernel-version>
```

- **The `transport:` line is the one named proof point for the `cider-ede` epic.** It must say
  `transport: xpc, apiserver ...`, not `transport: cli`, for this release to actually contain the
  XPC-transport work — that is the whole point of checking it here rather than trusting the version
  number alone. The exact `apiserver <semver>` value is UNCONFIRMED until read from this machine
  (Apple `container`'s own version, not cider's) — it should be `≥ 1.2.0` per
  `ApiServerVersion.Minimum`; a value `> 1.3.0` (`ApiServerVersion.Tested`) is still expected to work
  but is running ahead of what this project has actually exercised. On a macOS 26 host the
  `fallback:` list should read exactly `BuildImageAsync, LoginAsync, StartBuilderAsync` (three
  members, that order) — a fourth entry, `CreateNetworkAsync`, would only appear on a **pre**-26
  host, which this machine (macOS 26 required, per README) should not be.
- **Failure looks like, and means:**
  - `transport: cli (<reason>)`: the daemon fell back — the parenthesized reason names why
    (apiserver unreachable, below the version floor, or `runtime.transport` set to `cli` somewhere
    in config/env). If nothing explains the fallback, this is the finding: the XPC transport that
    is the point of this release did not actually activate.
  - `daemon:    not responding`: Step 2 claimed success but the daemon isn't actually answering
    now — re-check `launchctl print gui/<uid>/com.chillicream.cider.daemon` and
    `~/.cider/daemon.log`.
  - `apple:     unavailable (<error>)`: Apple `container` itself isn't running or reachable —
    `container system start` (Apple's own CLI) and retry.

### Step 7 — clean up the smoke-test artifacts (optional)

```bash
docker rmi cider-smoke-test
rm -rf /tmp/cider-smoke
```

---

## If something fails

Every failure mode above says what it would mean. File it as a new task with the exact command,
exact output, `cider status` output, and `~/.cider/daemon.log` attached — do not work around it by
touching the daemon, the plist, or the docker context further by hand first, since that would erase
the evidence of what actually happened on a path nobody has walked before.
