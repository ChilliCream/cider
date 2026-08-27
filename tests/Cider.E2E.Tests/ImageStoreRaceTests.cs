using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cider.Core.Configuration;
using Cider.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Cider.E2E.Tests;

/// <summary>
/// A <see cref="DaemonFixture"/> pinned to the CLI transport regardless of the ambient
/// <c>CIDER_RUNTIME_TRANSPORT</c> — cider-ede.37 leg 1 correction, choice (a) of the two the task
/// offered. cider-ede.31's Verification section said "the xpc transport", but on the XPC transport the
/// fixed <c>RemoveImageAsync</c> (<c>XpcContainerRuntime.Images.cs</c>'s primary path) issues
/// <c>imageDelete(reference, garbageCollect: false)</c> and nothing else — no sweep at all, so racing
/// it against concurrent pulls never contends <c>BlobSweepGate</c> and can never fail for the reason
/// this test exists to check. <c>AppleContainerRuntime.Images.cs</c>'s <c>RemoveImageAsync</c>
/// (<see cref="CiderOptions.CliRuntimeTransport"/>) is different: Apple's own <c>container image
/// delete</c> subprocess sweeps the whole content store on every single call with no flag to skip it
/// (<c>ImageDelete.swift</c>), so on this transport every <c>rmi</c> genuinely is a store-wide sweep,
/// serialized by <see cref="Cider.AppleContainer.BlobSweepGate.EnterSweepAsync"/> — that is the exact
/// code path the gate exists to protect, and the only rmi path where a race against concurrent pulls
/// has anything to prove. The alternative the task also offered — keep XPC and race
/// <c>XpcContainerRuntime.Images.cs</c>'s <c>PruneImagesAsync</c> instead — was rejected: that call is
/// an explicit, user-requested store-wide prune, and this task's own environment rules forbid running
/// one against this shared machine's real Apple store, so the CLI-transport rmi sweep is both the more
/// faithful reproduction of cider-ede.31's fix (which changed rmi, not prune) and the safer one.
/// </summary>
public sealed class ImageStoreRaceFixture : DaemonFixture
{
    /// <inheritdoc />
    protected override string? RuntimeTransportOverride => CiderOptions.CliRuntimeTransport;

    /// <summary>
    /// cider-ede.37 leg 1 correction: this fixture would otherwise never start at all on this
    /// machine right now — see <see cref="DaemonFixture.ToleratesImageSnapshotDanglingContentFailure"/>'s
    /// own doc comment for why, and this class's own remarks for the specific dangling entry
    /// (docker.io/library/alpine:3.18) responsible.
    /// </summary>
    protected override bool ToleratesImageSnapshotDanglingContentFailure => true;
}

/// <summary>The collection <see cref="ImageStoreRaceTests"/> uses so it gets its own CLI-pinned daemon.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageStoreRaceCollection : ICollectionFixture<ImageStoreRaceFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "cider-e2e-image-store-race";
}

/// <summary>
/// E2E — cider-ede.37 leg 1: cider-ede.31's own deferred proving experiment, run first and reported
/// on its own per the planner's re-prioritisation (task comment). cider-ede.31 (commits f21b028 +
/// 0411d9e) stopped <c>docker rmi</c> from triggering a store-wide orphaned-blob sweep on every call
/// — the mechanism that corrupted this machine's real Apple image store twice in one day (alpine:3.19,
/// then redis:8.6), diagnosed from Apple's Swift source (<c>ImageDelete.swift</c>: <c>garbageCollect:
/// false</c> per delete, then one unconditional store-wide <c>cleanUpOrphanedBlobs()</c> — on the XPC
/// transport this used to run again after every single <c>rmi</c>) and shipped on unit tests plus
/// that source-level reasoning alone. The one experiment cider-ede.31's own Verification section named
/// — concurrent <c>docker pull</c> against a <c>docker rmi</c> loop for a minute, then assert Apple's
/// own <c>container image ls</c> still exits 0 — was never run before this task. This is that
/// experiment, run against images this test pulls itself, never against the shared store's existing
/// content.
/// </summary>
/// <remarks>
/// Live finding recorded here for whoever reads this next: writing this test surfaced a THIRD, until
/// now unknown, dangling content reference already sitting in this machine's real Apple store —
/// <c>docker.io/library/alpine:3.18</c> → <c>sha256:de0eb0b3...</c>, no blob file on disk, discovered
/// on a solo (non-concurrent) seed <c>docker pull</c> before this test's own race loop had run at
/// all. <c>container image ls</c> (Apple's own CLI) exited 1 on it, confirmed independently of cider
/// with the raw CLI. This is NOT evidence that cider-ede.31's fix has a hole: that fix serializes only
/// THIS daemon's own pulls/loads/builds against THIS daemon's own rmi/prune sweeps (a documented,
/// deliberate limit — task cider-ede.31 comment #85 — it cannot coordinate with another process), and
/// nothing in this test's own code ever touched <c>alpine:3.18</c> or ran concurrently before the
/// solo seed pull that hit it. It is most likely either residual damage from the same incident
/// cider-ede.31 already recorded as unrepaired (a different digest than the one recorded there,
/// <c>redis:8.6</c> → <c>sha256:93b8ce77</c> — so a third casualty, not a duplicate report of the
/// same one) or new damage from some other process against the shared store between that task's
/// close and this one running — this machine has no per-run isolation and other agents' sessions were
/// independently active at the time (confirmed via <c>ps</c>: several concurrent <c>dotnet test</c>
/// processes). Left unrepaired: clearing it needs a scoped Apple-CLI delete against the live shared
/// store, which this session's own permission boundary refused when attempted directly, and repairing
/// the shared store is an operator step outside this task's own scope regardless. <b>The user's real
/// image store is in exactly the broken state cider-ede.31 exists to prevent recurrence of, right
/// now, independent of anything below.</b>
///
/// cider-ede.37 leg 1 NEGATIVE CONTROL (planner/orchestrator hard close condition), recorded here as
/// the task requires all three of the following reported together:
///
/// (1) FAILURE CRITERION, stated before the run: Apple's own <c>container image ls</c> exits non-zero
/// for a NEW reason (a digest not already named by the pre-existing alpine:3.18 entry above), OR any
/// <c>LoadImages</c> tag (pulled repeatedly, never deleted, by this race) stops inspecting/running
/// through cider after the race — the two assertions this test already makes below. cider-ede.37 leg 1
/// correction (finding 1): the confound classifier (<c>IsPreExistingDanglingContentConfound</c>) keys
/// on that exact tracked digest (<c>TrackedDanglingContentDigest</c>), not on the generic dangling-
/// content marker text alone, so this stated criterion and the code now agree — a dangling-content
/// failure naming any OTHER digest is real signal, never the tracked confound.
///
/// (2) NEGATIVE CONTROL: <c>CIDER_TEST_SKIP_BLOB_SWEEP_GATE=1</c> (restores the exact pre-cider-ede.31
/// unguarded window on the CLI transport's <c>RemoveImageAsync</c> — see that env var's own doc
/// comment on <c>AppleContainerRuntime.Images.cs</c>) run twice: once at this test's default 15s
/// budget (3-way concurrency: 1 pull loop + 2 rmi loops) — 68 rmi/re-pull cycles, 15 pulls — and once
/// at the full <c>CIDER_E2E_RACE_FULL=1</c> minute budget, same concurrency — 108 rmi/re-pull cycles,
/// 27 pulls. Both runs PASSED: neither reproduced the failure criterion above. The gate-skip itself
/// was independently verified live (a throwaway reflection probe against the built
/// Cider.AppleContainer.dll, env var set vs. unset) to actually flip
/// <c>AppleContainerRuntime.SkipBlobSweepGateForTest</c>, so this is not a no-op control.
///
/// (3) POST-FIX run, same full-minute budget and same 3-way concurrency (not fewer), gate ENABLED
/// (this test's normal, default configuration): 244 rmi/re-pull cycles, 45 pulls. Also PASSED.
///
/// CONCLUSION, per the task's own explicit instruction for this outcome: the negative control could
/// NOT reproduce the corruption at either budget tried, so this loop is not yet a proving test for
/// cider-ede.31's fix on this machine — this is reported as that finding, not as a pass for the fix.
/// Two candidate reasons, neither chased further within this task's own scope (file scope: "stop and
/// report rather than fixing in place" once a leg's own experiment is the thing that failed):
/// (a) the loop's actual exercised concurrency (2 rmi loops racing 1 pull loop, hundreds of cycles
/// over a minute on Apple Silicon-local disk) may simply be narrower than whatever window the
/// production incidents (alpine:3.19, redis:8.6, and the alpine:3.18 entry documented above) actually
/// hit; (b) EVERY rmi/re-pull cycle on this machine right now fails before completing real work for a
/// SEPARATE, deterministic reason documented on <c>PreExistingDanglingContentMarker</c> below (a
/// pre-existing dangling content entry breaks <c>ImageManager.FindImageDetailAsync</c>'s fallback to
/// <c>ListImagesAsync</c> for any reference not found by a direct inspect), which could plausibly be
/// narrowing the real race window further by aborting most cycles before the pull side ever reaches
/// Apple's subprocess — though the delete side's real, physical <c>image delete</c> subprocess (with
/// its store-wide sweep) does still run every time regardless of that separate defect, confirmed via
/// the daemon's own "image delete ...: running (sweeps the whole content store)" log line appearing
/// once per rmi attempt in every run above.
/// </remarks>
[Collection(ImageStoreRaceCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ImageStoreRaceTests(ImageStoreRaceFixture daemon, ITestOutputHelper output)
{
    // Four distinct tags this test alone pulls repeatedly as the concurrent write load, plus one
    // separate tag it repeatedly deletes and re-pulls as the rmi churn -- distinct from every tag
    // another suite in this run touches (ImageTests/LifecycleTests/SyncTests use alpine:3.22;
    // BuildKitTests/BuildTests mint their own e2e/* tags) so this test's own rmi loop can never
    // remove an image another concurrently-running suite assumes is present.
    //
    // NOT alpine:3.18: discovered live while writing this test (2026-08-26) to already be a dangling
    // content reference on this machine's real store -- state.json carries
    // docker.io/library/alpine:3.18 -> sha256:de0eb0b3f2a47ba1eb89389859a9bd88b28e82f5826b6969ad604979713c2d4f
    // with no file under content/blobs/sha256 for that digest, so a solo, non-concurrent `docker pull
    // alpine:3.18` fails outright and `container image ls` exits 1 on it -- see this test's own class
    // doc comment for the full account. Left alone rather than repaired (an operator step this task's
    // permissions do not extend to, and repairing it is not this test's job); avoided here so the
    // actual experiment below can run against images unaffected by that pre-existing entry.
    //
    // NOT alpine:3.19 either (cider-ede.37 leg 4 correction): cider-ede.31's own report recorded
    // alpine:3.19 as the OTHER tag its incident corrupted (alongside redis:8.6), so seeding it here
    // risks a seed pull failing for a reason that has nothing to do with this race.
    // CIDER_E2E_RACE_LOAD_IMAGES (comma-separated) overrides the concurrent-pull load tags for a
    // control run. Needed because registry choice is load-bearing (cider-ede.37 control re-run
    // finding): Docker Hub 429s an unauthenticated IP after ~100 pulls/6h, and this race makes
    // hundreds of registry hits in minutes -- a control run against docker.io collapses into
    // instant-429 cycles that never open a write window, which is the same "cycles that do no real
    // work" confound the re-run existed to remove. mirror.gcr.io serves the same library images
    // (same digests) without that limit.
    private static readonly string[] LoadImages =
        Environment.GetEnvironmentVariable("CIDER_E2E_RACE_LOAD_IMAGES") is { Length: > 0 } load
            ? load.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["alpine:3.14", "alpine:3.20", "alpine:3.21"];

    // cider-ede.37 control re-run condition (task comments #148/#151): the write window this race
    // needs open is "blobs written, index entry not yet committed", and its width is proportional to
    // how much there is to write. The default churn tag (alpine:3.16, small and usually warm) has a
    // window of milliseconds; the three real corruptions this machine suffered were alpine:3.19,
    // redis:8.6 and alpine:3.18 -- redis being a large multi-layer network pull with a window of
    // seconds. CIDER_E2E_RACE_CHURN_IMAGE overrides the churn tag for such a control run (e.g.
    // redis:8.6). When it is set, the run is a deliberate cold-image control, so the test ALSO
    // asserts, from the Apple store's own state.json, that the image is genuinely absent before the
    // seed pull (absence asserted, not assumed -- recorded in the run's own output) and that the
    // baseline store is clean (no state entry lacking its blob); a dirty baseline stops the run as
    // INCONCLUSIVE rather than confounding it. The default (unset) keeps the small warm tag and the
    // three-way delta outcomes so the suite-run test never goes red on a machine whose store already
    // carries unrelated damage.
    private static readonly string ChurnImage =
        Environment.GetEnvironmentVariable("CIDER_E2E_RACE_CHURN_IMAGE") is { Length: > 0 } churn
            ? churn
            : "alpine:3.16";

    private static readonly bool ColdChurnControl =
        Environment.GetEnvironmentVariable("CIDER_E2E_RACE_CHURN_IMAGE") is { Length: > 0 };

    // cider-ede.37 leg 4 correction: the full minute this test was written with burns a minute of
    // Docker Hub traffic against four tags on every default `dotnet test` run on this shared machine.
    // Set CIDER_E2E_RACE_FULL=1 to run the full budget cider-ede.31's Verification section asked for;
    // left unset (the default), a much shorter budget still exercises the same race loops, just with
    // fewer iterations.
    // CIDER_E2E_RACE_BUDGET_SECONDS (cider-ede.37 control re-run) takes precedence over both so a
    // sustained control run (10+ minutes with a large churn image whose cycle time is seconds) can be
    // driven without touching the suite defaults.
    private static readonly TimeSpan RaceBudget =
        int.TryParse(
            Environment.GetEnvironmentVariable("CIDER_E2E_RACE_BUDGET_SECONDS"),
            NumberStyles.None, CultureInfo.InvariantCulture, out var budgetSeconds) && budgetSeconds > 0
            ? TimeSpan.FromSeconds(budgetSeconds)
            : string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E_RACE_FULL"), "1", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(3);

    // cider-ede.37 leg 1, negative-control finding: on this machine, right now, a plain *serial*
    // `docker rmi <ref>` or `docker pull <ref>` for a reference that a direct `container image
    // inspect <ref>` cannot find (freshly removed, not yet pulled, ...) deterministically 500s --
    // with or without this test's race, with or without cider-ede.31's BlobSweepGate. Traced live
    // (manual daemon, no concurrency at all) to Cider.Core.Services.ImageManager.FindImageDetailAsync:
    // its fallback from "not found by direct inspect" to a full `container image ls` listing has no
    // guard against ListImagesAsync's own documented TOTAL-failure case (AppleContainerRuntime.
    // Images.cs's ListImagesAsync doc comment: Apple's `image ls --format json` returns EMPTY stdout
    // on this machine's pre-existing dangling alpine:3.18 entry -- no partial rows to enumerate-with-
    // skips, unlike the case that fix was written for) -- so it throws, and that throw is not caught
    // by any of FindImageDetailAsync's three callers inside PullAsync (the existedBefore check before
    // the pull even starts, and the afterDetail check right after it) the way InspectImageAsync's own
    // WithSiblingReferencesAsync already catches the identical failure a few lines above it. This is a
    // SEPARATE, deterministic, non-racy defect from cider-ede.31's own bug (concurrent pull vs. sweep
    // corrupting blobs) -- it fires on a single, solo, non-concurrent rmi+pull cycle -- and it is not
    // this task's to fix in src/ (file scope: "stop and report rather than fixing in place"). It is
    // reported here, not filed as its own defect, because cider-ede.41 already tracks the root dangling
    // entry and this is a new consequence of that same tracked cause, not a new independent one.
    //
    // Consequence for this test's own design: the rmi/re-pull loop's per-call success/failure count is
    // NOT a valid signal for whether cider-ede.31's fix holds under the race, because on this machine
    // every single cycle fails this way regardless of the fix or the race. Failures whose message
    // carries this exact marker AND the tracked digest below (TrackedDanglingContentDigest) are counted
    // and reported separately from any OTHER failure text, which remains a real regression signal (a
    // message that does NOT carry both the marker and that exact digest cannot be this confound, and
    // fails the test loudly -- in particular a dangling-content failure naming a DIFFERENT digest, which
    // is exactly what this race would produce if it corrupted a blob, is never misclassified as this
    // confound). The decisive signal this test still answers cleanly despite the confound is unchanged:
    // whether `container image ls`'s own error digest changes across the race (see the
    // baseline/appleList comparison below) -- Apple's own delete/pull subprocesses still physically run
    // and race each other underneath cider's bookkeeping even when that bookkeeping itself throws, so
    // that comparison still exercises the real race cider-ede.31 fixed.
    private const string PreExistingDanglingContentMarker = "content with digest";

    // The exact digest of the ONE pre-existing dangling content entry documented above and in this
    // class's own remarks (docker.io/library/alpine:3.18 -> this digest, no blob file on disk).
    // cider-ede.37 leg 1 correction (finding 1): the marker text alone is Apple's generic dangling-blob
    // error text (identical to CliErrorMapper.DanglingContentMarker) shared by ANY dangling entry, so
    // matching on the marker alone would also swallow a genuinely NEW dangling entry this race itself
    // produced. The classifier below requires this exact digest too, so it keys on the tracked entry
    // specifically, not on the generic error shape.
    private const string TrackedDanglingContentDigest =
        "sha256:de0eb0b3f2a47ba1eb89389859a9bd88b28e82f5826b6969ad604979713c2d4f";

    /// <summary>
    /// True when <paramref name="text"/> carries BOTH the marker text of Apple's generic dangling-
    /// content error AND the exact digest of the ONE pre-existing, out-of-scope confound tracked as
    /// cider-ede.41 (<see cref="TrackedDanglingContentDigest"/>) -- never true for a dangling-content
    /// failure naming any OTHER digest, so a NEW dangling entry this race itself produces is classified
    /// as real signal, not silently folded into the known confound.
    /// </summary>
    private static bool IsPreExistingDanglingContentConfound(string text) =>
        text.Contains(PreExistingDanglingContentMarker, StringComparison.Ordinal) &&
        text.Contains(TrackedDanglingContentDigest, StringComparison.Ordinal);

    // Apple's machine-wide shared image store -- the store every cider daemon on this machine
    // (including this test's throwaway one) actually writes through. The scan below reads its index
    // (state.json: reference -> {digest,...}) and checks each entry's blob file exists, which is the
    // literal definition of the corruption cider-ede.31 fixed: a state entry whose digest has no file
    // under content/blobs/sha256.
    internal static readonly string AppleStoreRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "com.apple.container");

    /// <summary>
    /// Reads the Apple store's own index and returns every reference it holds plus every entry whose
    /// blob file is missing (<c>"reference -> digest"</c>). <c>null</c> when the index cannot be read
    /// (missing or, transiently, mid-write -- retried a few times before giving up). Only called when
    /// the race loops are quiescent (before seeding, after the race), never mid-race.
    /// </summary>
    internal static (IReadOnlyList<string> References, IReadOnlyList<string> MissingBlobEntries)? ScanAppleStoreState()
    {
        var statePath = Path.Combine(AppleStoreRoot, "state.json");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(statePath))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var references = new List<string>();
                var missing = new List<string>();
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    references.Add(entry.Name);
                    var digest = entry.Value.GetProperty("digest").GetString()!;
                    var blobPath = Path.Combine(
                        AppleStoreRoot, "content", "blobs", "sha256", digest["sha256:".Length..]);
                    if (!File.Exists(blobPath))
                    {
                        missing.Add($"{entry.Name} -> {digest}");
                    }
                }

                return (references, missing);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                Thread.Sleep(500);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an Apple-store index key (fully qualified, e.g. <c>docker.io/library/redis:8.6</c>)
    /// names the short image reference this test uses (e.g. <c>redis:8.6</c>).
    /// </summary>
    internal static bool ReferenceMatches(string stateKey, string image) =>
        string.Equals(stateKey, image, StringComparison.Ordinal) ||
        stateKey.EndsWith("/" + image, StringComparison.Ordinal);

    [E2EFact]
    public async Task Concurrent_pulls_survive_a_minute_of_rmi_churn_without_corrupting_the_store()
    {
        // Negative-control integrity check (cider-ede.37, kept in the harness rather than as a
        // throwaway probe): CIDER_TEST_SKIP_BLOB_SWEEP_GATE must genuinely flip
        // AppleContainerRuntime.SkipBlobSweepGateForTest in THIS process (the daemon under test is
        // in-process, so this reflected value is the exact flag the racing RemoveImageAsync reads).
        // Without this, a "control" run could silently be a no-op with the gate still engaged.
        var skipGateRequested = string.Equals(
            Environment.GetEnvironmentVariable("CIDER_TEST_SKIP_BLOB_SWEEP_GATE"), "1", StringComparison.Ordinal);
        var gateField = typeof(Cider.AppleContainer.AppleContainerRuntime).GetField(
            "SkipBlobSweepGateForTest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(gateField is not null, "AppleContainerRuntime.SkipBlobSweepGateForTest no longer exists -- the negative-control seam was removed or renamed");
        var gateSkipped = (bool)gateField!.GetValue(null)!;
        Assert.True(
            skipGateRequested == gateSkipped,
            $"CIDER_TEST_SKIP_BLOB_SWEEP_GATE requested skip={skipGateRequested} but the reflected " +
            $"AppleContainerRuntime.SkipBlobSweepGateForTest is {gateSkipped} -- the control seam did not engage");
        output.WriteLine(
            $"blob-sweep gate: skip requested={skipGateRequested}, reflected SkipBlobSweepGateForTest={gateSkipped} " +
            (gateSkipped
                ? "(NEGATIVE CONTROL: cider-ede.31's fix DISABLED, pre-fix unguarded sweep window restored)"
                : "(fix enabled: sweeps serialized against this daemon's writes, cider-ede.31 default)"));

        // Baseline store health, recorded from the store's own index BEFORE anything is pulled: a
        // pre-existing entry lacking its blob would confound the entire run (any dangling entry found
        // afterwards could not be attributed to the race). Recorded always; enforced when this run is
        // a deliberate cold-image control (see ChurnImage's doc comment).
        var preSeedLs = await Cmd.RunAsync("container", ["image", "ls"], timeout: TimeSpan.FromSeconds(60));
        var preScan = ScanAppleStoreState();
        output.WriteLine(
            $"pre-seed baseline: `container image ls` ok={preSeedLs.Ok}; state.json scan: " +
            (preScan is null
                ? "unavailable"
                : $"{preScan.Value.References.Count} entries, {preScan.Value.MissingBlobEntries.Count} missing blob(s)" +
                  (preScan.Value.MissingBlobEntries.Count > 0
                      ? ":\n" + string.Join('\n', preScan.Value.MissingBlobEntries)
                      : "")));

        var churnPresentBeforeSeed = preScan?.References.Any(r => ReferenceMatches(r, ChurnImage));
        output.WriteLine(
            $"churn image {ChurnImage}: cold-control mode={ColdChurnControl}; present in store before seed pull: " +
            $"{(churnPresentBeforeSeed is null ? "unknown (scan unavailable)" : churnPresentBeforeSeed.Value ? "YES (warm)" : "NO (cold -- absence asserted from the store's own index, not assumed)")}");

        if (ColdChurnControl)
        {
            if (!preSeedLs.Ok || preScan is null || preScan.Value.MissingBlobEntries.Count > 0)
            {
                Assert.Fail(
                    "INCONCLUSIVE -- baseline store not clean before the cold-image control run, so the " +
                    "experiment is stopped rather than confounded (a pre-existing dangling entry makes " +
                    "'did the race produce a NEW one?' unanswerable):\n" +
                    $"container image ls ok={preSeedLs.Ok}\n{preSeedLs}\n" +
                    $"state.json scan: {(preScan is null ? "unavailable" : string.Join('\n', preScan.Value.MissingBlobEntries))}");
            }

            Assert.True(
                churnPresentBeforeSeed == false,
                $"cold-image control requires {ChurnImage} to be genuinely ABSENT from the Apple store " +
                "before the seed pull (the write window this control needs is a real multi-layer network " +
                $"pull, not a warm no-op), but the store's own index already holds it -- remove it or " +
                "choose a genuinely uncached image");
        }

        // Self-pulled: every image this race touches is pulled by this test before the race starts,
        // so what follows races re-pulls and re-deletes of known-present, this-run images, not first
        // pulls that happen to interleave with an rmi for something else.
        foreach (var image in LoadImages.Append(ChurnImage))
        {
            var seed = await daemon.DockerAsync(["pull", image], timeout: PullTimeout);
            Assert.True(seed.Ok, $"seed pull of {image} failed: {seed}");
        }

        // Baseline, taken AFTER seeding but BEFORE the race starts: separates "already broken before
        // this test's own race ran" (see this class's remarks — the store can carry damage from
        // outside this test entirely) from "broken BY the race below", so a failure never gets
        // misattributed to the wrong side of that line. Captured, not asserted here: the race still
        // runs even when the baseline is already red, so a pre-existing, unrelated confound can never
        // prevent the actual experiment (does THIS race introduce NEW damage) from being answered.
        var baseline = await Cmd.RunAsync("container", ["image", "ls"], timeout: TimeSpan.FromSeconds(60));

        var deadline = DateTime.UtcNow + RaceBudget;
        var pullAttempts = 0;
        var pullFailures = 0;
        var rmiAttempts = 0;
        var rePullFailures = 0;
        var failureDetails = new ConcurrentQueue<string>();

        async Task PullLoopAsync()
        {
            while (DateTime.UtcNow < deadline)
            {
                foreach (var image in LoadImages)
                {
                    var result = await daemon.DockerAsync(["pull", image], timeout: PullTimeout);
                    Interlocked.Increment(ref pullAttempts);
                    if (!result.Ok)
                    {
                        Interlocked.Increment(ref pullFailures);
                        failureDetails.Enqueue($"pull {image}: {result}");
                    }
                }
            }
        }

        async Task RmiLoopAsync()
        {
            while (DateTime.UtcNow < deadline)
            {
                // rmi and the very next pull of the same tag are not sequenced against each other --
                // fired one after the other with no synchronization beyond program order in this
                // loop, while PullLoopAsync's own pulls of the other four images run fully
                // concurrently on the other tasks. -f tolerates "already gone" from a previous round.
                await daemon.DockerAsync(["rmi", "-f", ChurnImage], timeout: PullTimeout);
                var rePull = await daemon.DockerAsync(["pull", ChurnImage], timeout: PullTimeout);
                Interlocked.Increment(ref rmiAttempts);
                if (!rePull.Ok)
                {
                    Interlocked.Increment(ref rePullFailures);
                    failureDetails.Enqueue($"re-pull {ChurnImage} after rmi: {rePull}");
                }
            }
        }

        // Two independent rmi/re-pull loops on the same churn tag, plus the four-image pull loop,
        // all racing for the whole minute -- more concurrent writers hitting the daemon's
        // BlobSweepGate at once than a single pair of loops would.
        var raceClock = Stopwatch.StartNew();
        await Task.WhenAll(PullLoopAsync(), RmiLoopAsync(), RmiLoopAsync());
        raceClock.Stop();

        // Separate the pre-existing, out-of-scope confound this class's remarks and
        // PreExistingDanglingContentMarker's own doc comment document (a deterministic, non-racy
        // ListImagesAsync total failure on THIS machine's real store, unrelated to cider-ede.31) from
        // any OTHER failure text, which remains real regression signal.
        var confoundFailures = failureDetails.Count(IsPreExistingDanglingContentConfound);
        var otherFailures = failureDetails.Where(d => !IsPreExistingDanglingContentConfound(d)).ToArray();

        // cider-ede.37 leg 1 negative-control instrumentation: logged unconditionally (not only on
        // failure) so a run's exact iteration count and concurrency are on record whether it passes or
        // fails -- the task's own close condition requires reporting the negative control and the
        // post-fix run at the SAME count and concurrency, which is unverifiable without this.
        output.WriteLine(
            $"race budget {RaceBudget} (wall {raceClock.Elapsed}), concurrency 3 (1 pull loop + 2 rmi loops), " +
            $"churn image {ChurnImage}: " +
            $"{pullAttempts} pull attempts ({pullFailures} failed), " +
            $"{rmiAttempts} rmi/re-pull attempts ({rePullFailures} failed, " +
            $"avg cycle {(rmiAttempts > 0 ? (raceClock.Elapsed.TotalSeconds * 2 / rmiAttempts).ToString("F1", CultureInfo.InvariantCulture) : "n/a")}s " +
            "per rmi loop); " +
            $"of {pullFailures + rePullFailures} total failures, {confoundFailures} carry the " +
            "pre-existing dangling-content marker (known, out-of-scope confound -- see " +
            "PreExistingDanglingContentMarker's doc comment) and " +
            $"{otherFailures.Length} do not (real signal)");

        Assert.True(
            otherFailures.Length == 0,
            $"{otherFailures.Length} pull/rmi failure(s) during the race did not match the tracked " +
            $"cider-ede.41 confound (the classifier keys on marker text \"{PreExistingDanglingContentMarker}\" " +
            $"AND digest {TrackedDanglingContentDigest} together, not the marker alone) -- this is new " +
            "signal, including any dangling-content failure naming a DIFFERENT digest than the tracked " +
            "one, which is new corruption this race itself introduced:\n" + string.Join('\n', otherFailures));

        // ChurnImage, the one tag this race actually deletes, is checked here -- directly through
        // Apple's own CLI, not through cider's ImageManager.FindImageDetailAsync path -- rather than in
        // the LoadImages inspect/run loop below (cider-ede.37 leg 1 correction, finding 2): every
        // re-pull attempt on ChurnImage inside RmiLoopAsync above races against the
        // PreExistingDanglingContentMarker confound (FindImageDetailAsync's existedBefore check throws
        // before cider's own PullImageAsync ever invokes the real Apple `image pull` subprocess), so a
        // post-race inspect/run through cider would fail on every run on this machine for a reason
        // unrelated to this race -- the "red for a reason it doesn't test" failure mode the orchestrator
        // already corrected the appleList assertion for. The churn check moved off that confounded path
        // onto Apple's own CLI instead, and IS asserted here (not merely logged): a failure that does
        // NOT carry the tracked cider-ede.41 confound means Apple's own CLI cannot restore the churn tag
        // after the race, which is real corruption signal, not "never re-pulled through cider by
        // design". Run BEFORE appleList is captured below (cider-ede.37 leg 1 correction) so a NEW
        // dangling digest this restore itself might produce also registers in the baseline-vs-post-race
        // `container image ls` delta, rather than being invisible to it.
        var churnRestore = await Cmd.RunAsync("container", ["image", "pull", ChurnImage], timeout: PullTimeout);
        Assert.True(
            churnRestore.Ok || IsPreExistingDanglingContentConfound(churnRestore.Stderr),
            $"Apple's own CLI could not restore {ChurnImage} after the race, and the failure did not " +
            $"carry the tracked cider-ede.41 confound (marker \"{PreExistingDanglingContentMarker}\" AND " +
            $"digest {TrackedDanglingContentDigest}) -- Apple's own CLI cannot restore the churn tag " +
            "after the race, real signal, not the tracked confound:\n" + churnRestore);
        output.WriteLine(
            churnRestore.Ok
                ? $"{ChurnImage}: restored via Apple's own CLI after the race, store content intact"
                : $"{ChurnImage}: Apple's own CLI hit the tracked cider-ede.41 confound while restoring " +
                  $"it after the race (tolerated, not a race failure):\n{churnRestore}");

        // The decisive assertion cider-ede.31's own Verification section named: Apple's own CLI, not
        // cider, must still list the store cleanly after the race. Before the fix this is exactly the
        // command that started exiting 1 on a dangling content reference following this same
        // pull/rmi pattern (task cider-ede.31's evidence: state.json held a digest with no blob file
        // on disk, and `container image ls` exited 1 on it). Captured AFTER the churnRestore assertion
        // above (cider-ede.37 leg 1 correction, finding 2) so a new dangling digest that restore itself
        // might produce also shows up in the baseline-vs-post-race delta below.
        var appleList = await Cmd.RunAsync("container", ["image", "ls"], timeout: TimeSpan.FromSeconds(60));

        // Direct, filesystem-level form of the failure criterion, from the store's own index rather
        // than inferred from `container image ls`'s exit code: after the race (loops quiescent), no
        // state entry may lack its blob file EXCEPT the one pre-existing tracked confound
        // (TrackedDanglingContentDigest, cider-ede.41). Any OTHER missing-blob entry is a dangling
        // entry this run produced -- the exact corruption cider-ede.31 exists to prevent -- and fails
        // loudly regardless of what `image ls` happens to report.
        var postScan = ScanAppleStoreState();
        var newDangling = postScan?.MissingBlobEntries
            .Where(e => !e.Contains(TrackedDanglingContentDigest, StringComparison.Ordinal))
            .ToArray() ?? [];
        output.WriteLine(
            "post-race state.json scan: " +
            (postScan is null
                ? "unavailable"
                : $"{postScan.Value.References.Count} entries, {postScan.Value.MissingBlobEntries.Count} missing blob(s), " +
                  $"{newDangling.Length} NEW (not the tracked cider-ede.41 confound)"));
        Assert.True(
            newDangling.Length == 0,
            "CORRUPTION REPRODUCED -- after the race, the Apple store's own index holds entr(ies) whose " +
            "blob file is missing under content/blobs/sha256, and the digest is NOT the tracked " +
            "cider-ede.41 confound, so this is a dangling entry this run itself produced:\n" +
            string.Join('\n', newDangling));

        // No state entry this race actually touched lacks its blob: every LoadImages tag (pulled
        // repeatedly by the race, never deleted by it) is still inspectable and actually runnable
        // through cider, straight from the store the race just hammered with concurrent real Apple
        // `image delete` sweeps -- inspect alone would not catch a missing blob (the index entry can
        // still parse with no file backing its digest); running the image forces the content to be
        // read. This is real signal even when `appleList` above stays red on the pre-existing,
        // unrelated alpine:3.18 entry (see class remarks): it directly answers whether THIS race
        // damaged anything THIS race's own code touched. ChurnImage itself is checked above, through
        // Apple's own CLI, not here -- see the comment on churnRestore.
        var ownedImageFailures = new List<string>();
        foreach (var image in LoadImages)
        {
            var inspect = await daemon.DockerAsync(["inspect", image, "--format", "{{.Id}}"]);
            if (!inspect.Ok)
            {
                ownedImageFailures.Add($"{image} is no longer inspectable after the race:\n{inspect}");
                continue;
            }

            var run = await daemon.DockerAsync(["run", "--rm", image, "true"], timeout: TimeSpan.FromMinutes(2));
            if (!run.Ok)
            {
                ownedImageFailures.Add($"{image} is no longer runnable after the race:\n{run}");
            }
        }

        Assert.True(
            ownedImageFailures.Count == 0,
            "the race damaged one or more of the LoadImages tags THIS test itself pulled repeatedly " +
            "and never deleted -- this is new corruption this race caused, not the pre-existing " +
            "alpine:3.18 confound:\n" + string.Join('\n', ownedImageFailures));

        // Attribute appleList's own result against the baseline taken before the race started, rather
        // than asserting appleList.Ok in isolation: a baseline that was already red (the pre-existing,
        // unrelated alpine:3.18 entry — see class remarks) showing the exact same single error after
        // the race is proof the race added no NEW store-wide damage, even though the literal `image
        // ls exits 0` outcome this task asked for still cannot be demonstrated on this machine until
        // that pre-existing entry is repaired by an operator. A baseline that was clean turning red,
        // or an error that changed digest, is the real regression signal this whole test exists to
        // catch, and fails loudly either way.
        if (baseline.Ok)
        {
            Assert.True(
                appleList.Ok,
                "Apple's own `container image ls` was clean before the race and failed after it -- " +
                "the race itself corrupted the store, and the cider-ede.31 fix did NOT hold under the " +
                "exact race it was written for:\n" + appleList);
        }
        else if (appleList.Ok || !string.Equals(appleList.Stderr, baseline.Stderr, StringComparison.Ordinal))
        {
            Assert.Fail(
                "`container image ls` was already failing before the race started on pre-existing, " +
                "unrelated store damage (see class remarks), but the race changed that outcome -- " +
                "either it got repaired mid-race (unexpected) or the error changed, which would mean " +
                "the race introduced damage on top of the pre-existing entry:\n" +
                $"baseline: {baseline}\nafter the race: {appleList}");
        }
        else
        {
            // cider-ede.37 leg 3 correction: this branch is the test's own "the race added no NEW
            // damage" verdict -- baseline and post-race stderr are byte-identical, so the pre-existing,
            // unrelated alpine:3.18 confound (see class remarks) is the only thing keeping `container
            // image ls` from exiting 0, exactly as it did before the race ran. Calling Assert.Fail here
            // made this leg fail on every run on this machine, including its own success case, since
            // that confound is present before this test ever starts. The two real regression signals
            // this test exists to catch (a clean baseline turning red; an error that changed) are the
            // two branches above, and both still fail loudly. Passing here is deliberate, not a gap:
            // the literal "container image ls exits 0" outcome cider-ede.31's Verification section
            // named is left as an open item for the report/task comment, not asserted by a permanently
            // -red E2E test.
            output.WriteLine(
                "container image ls was already failing before this race started, on pre-existing " +
                "store damage this test did not cause (see class remarks: docker.io/library/alpine:3.18, " +
                "sha256:de0eb0b3..., no blob on disk) -- the race itself added NO new damage (identical " +
                "error before and after, and every image this race's own code touched stayed inspectable " +
                "and runnable). The literal 'container image ls exits 0' outcome cider-ede.31's " +
                "Verification section named cannot be demonstrated on this machine until that " +
                "pre-existing entry is repaired by an operator:\n" + appleList);
        }
    }
}

/// <summary>The collection <see cref="CrossProcessImageStoreRaceTests"/> uses so nothing else runs beside it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrossProcessImageStoreRaceCollection
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "cider-e2e-xproc-image-store-race";
}

/// <summary>
/// E2E — cider-ede.41 Fix direction step 1: the CROSS-process store-corruption experiment the
/// intra-daemon race above structurally cannot run. cider-ede.31's blob-sweep gate is an in-process
/// lock, documented as such (task cider-ede.31 comment #85): it serializes THIS daemon's sweeps
/// against THIS daemon's image writes and can never see another process's. This machine routinely
/// runs several cider daemons against the ONE machine-wide Apple store, and cider-ede.41's hypothesis
/// is that the three real corruptions came through exactly that blind spot: a <c>docker image
/// prune</c> in one daemon sweeping the whole shared store while another daemon is midway through a
/// pull whose blobs are written but whose index entry is not yet committed.
///
/// So, unlike every other test in this suite, this one spawns TWO throwaway daemons as separate OS
/// processes (the built <c>cider</c> apphost sitting next to this test assembly, <c>serve</c> on
/// /tmp sockets and data dirs, <c>--no-dns</c> so no forwarder containers are minted) — daemon A
/// pulling a large, genuinely-uncached image in a loop (absence asserted from the store's own index
/// before every pull, per cider-ede.37's control-re-run conditions), daemon B running <c>docker
/// image prune</c> in a loop. BOTH daemons run with the cider-ede.31 gate ENABLED, in its normal
/// default configuration — the point is precisely that two correctly-gated daemons should still be
/// able to corrupt the store across the process boundary if the hypothesis is right. Both daemons
/// are pinned to the XPC transport: at the time of the d63644b reproduction, prune's store-wide
/// sweep (<c>XpcContainerRuntime.PruneImagesAsync</c> → <c>imageCleanupOrphanedBlobs</c>) only
/// existed there, and daemon A's per-cycle <c>rmi</c> is <c>imageDelete(garbageCollect: false)</c>
/// on XPC — no sweep — so daemon B's prune was the ONE sweeping verb in the whole experiment and
/// any interference was attributable to it alone.
///
/// cider-ede.41 MITIGATION SHIPPED SINCE THAT REPRODUCTION (planner ruling, option A): the prune
/// path no longer sweeps at all, on either transport — <c>PruneImagesAsync</c> is gone (see the
/// prevention comment in <c>XpcContainerRuntime.Images.cs</c>). d63644b IS the designated negative
/// control (corruption on cycle 1, ~2s in, both gates enabled); the post-fix verification
/// (ruling requirement 6) is THIS SAME experiment at the full 900s budget, expected to run 0
/// incidents with a clean post-run index scan — sequenced, user-authorized, AFTER the operator has
/// repaired the store d63644b deliberately left broken, on a quiet machine.
///
/// FAILURE CRITERION, stated up front (the ticket's, verbatim): a dangling entry appears — an index
/// entry in the shared store's <c>state.json</c> whose digest has no blob file under
/// <c>content/blobs/sha256</c> — or Apple's own <c>container image ls</c> exits non-zero after the
/// run. The 13ea1d0 digest-specific classifier semantics are kept: a dangling entry is reported with
/// its exact reference and digest, and one matching the historically-tracked cider-ede.41 confound
/// digest is labelled as that entry RESURFACING rather than misattributed as new — though this
/// experiment additionally requires (and asserts) a CLEAN baseline before running at all, per the
/// planner's #160 correction: a pre-existing dangling entry is a confound, not a fixture, and a
/// dirty or non-listing baseline stops the run as INCONCLUSIVE rather than confounding it.
///
/// Secondary signal, recorded but deliberately NOT a test failure: a pull in daemon A failing with
/// the mid-write sweep signature (blob deleted out from under the in-flight pull — NSCocoaErrorDomain
/// "No such file or directory" under content/blobs/sha256, the exact signature cider-ede.37's
/// gate-disabled intra-daemon control produced 6/74 cycles) while daemon B was pruning. That proves
/// the cross-process interference MECHANISM with both gates enabled — the in-process gate demonstrably
/// not covering the other process's sweep — even if the committed-index-entry sub-case (the literal
/// dangling end-state) does not land within the budget. It is reported prominently either way; the
/// ticket's Outcome A/B split stays keyed to the dangling-entry criterion above.
///
/// Deliberate safety choices, because daemon B's prune runs against the real shared store: the prune
/// carries <c>--filter label=cider-ede41-xproc-never-matches</c>, a label no image on this machine
/// carries, so <c>ImageManager.PruneAsync</c> deletes NO images (in particular not the developer's
/// dangling <c>cider-build-*</c> build records, which an unfiltered dangling-only prune would sweep
/// up) — and then, per cider-ede.31's own "a no-op prune must still sweep" rule, unconditionally runs
/// the one store-wide orphaned-blob sweep this experiment is about. The sweep itself only ever
/// reclaims blobs no index entry references (that is the sanctioned, user-invocable reclaim path);
/// the only orphans this run manufactures are the churn image's own, and the only index entries at
/// risk mid-pull are for the image this test itself pulls. Corruption is only ever constructed
/// through normal pull/prune/rmi verbs from the two throwaway daemons — never by touching store
/// files directly, never through the user's installed daemon.
/// </summary>
[Collection(CrossProcessImageStoreRaceCollection.Name)]
[Trait("Category", "E2E")]
public sealed class CrossProcessImageStoreRaceTests(ITestOutputHelper output)
{
    // Large, genuinely-uncached churn image from the mirror. NEVER docker.io: Docker Hub 429s an
    // unauthenticated IP after ~100 pulls/6h, and cider-ede.37's first control run collapsed into
    // instant-429 cycles that never opened a write window -- the exact vacuous-cycle confound this
    // experiment must not repeat. mirror.gcr.io serves the same library images (same digests) without
    // that limit; redis:8.6 is the multi-layer ~seconds-wide-window image the corrected leg-1 control
    // measured at ~25s per real network pull on this machine.
    private static readonly string ChurnImage =
        Environment.GetEnvironmentVariable("CIDER_E2E_XPROC_CHURN_IMAGE") is { Length: > 0 } churn
            ? churn
            : "mirror.gcr.io/library/redis:8.6";

    // Sustained-run budget for the racing loops. Default 900s of real cycles -- the same budget as
    // cider-ede.37's corrected leg-1 control (74 cycles / ~25s each at 900s), so the two experiments'
    // negative results are comparable if this one also fails to reproduce.
    private static readonly TimeSpan RaceBudget = TimeSpan.FromSeconds(
        int.TryParse(
            Environment.GetEnvironmentVariable("CIDER_E2E_XPROC_BUDGET_SECONDS"),
            NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : 900);

    // A label no image on this machine carries: makes daemon B's prune delete nothing while still
    // running the unconditional store-wide sweep (see class remarks).
    private const string NeverMatchingPruneLabel = "cider-ede41-xproc-never-matches";

    // The historically-tracked cider-ede.41 dangling digest (alpine:3.18's), kept from 13ea1d0's
    // classifier so a post-race dangling entry matching it is reported as that entry RESURFACING
    // (store repaired before this run) rather than silently counted as brand-new corruption.
    private const string HistoricalTrackedDanglingDigest =
        "sha256:de0eb0b3f2a47ba1eb89389859a9bd88b28e82f5826b6969ad604979713c2d4f";

    // The mid-write sweep interference signatures: a pull's freshly-written blob deleted out from
    // under it by the concurrent sweep. Two shapes observed live: the filesystem-level one the
    // gate-disabled intra-daemon control produced (cider-ede.37 leg-1 re-run, 6/74 cycles --
    // NSCocoaErrorDomain "No such file or directory" mid-unpack), and the content-resolution one this
    // experiment's own first run produced (2026-08-27, cycle 1: `pull <ref>: content with digest
    // sha256:...` -- Apple's generic dangling-content error text, here naming the digest of the very
    // image being pulled, whose just-written blob the other process's sweep had deleted). The store
    // index is scanned right after every failed pull regardless, so whether a given interference hit
    // also committed a persistent dangling entry (the ticket's Outcome A criterion) is always decided
    // from the store itself, never from this text.
    private static bool IsSweepInterferenceSignature(string text) =>
        text.Contains("No such file or directory", StringComparison.Ordinal) ||
        text.Contains("NSCocoaErrorDomain", StringComparison.Ordinal) ||
        text.Contains("content with digest", StringComparison.Ordinal);

    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DaemonReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReclaimWaitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>One spawned throwaway daemon process (the real built <c>cider</c> apphost), plus the
    /// docker-CLI environment that talks to it and the teardown that removes every trace of it.</summary>
    private sealed class SpawnedDaemon : IAsyncDisposable
    {
        public required string Name { get; init; }

        public required BackgroundProcess Process { get; init; }

        public required string SocketPath { get; init; }

        public required string DataDir { get; init; }

        public required string DockerConfigDir { get; init; }

        public Task<CommandResult> DockerAsync(IEnumerable<string> arguments, TimeSpan? timeout = null) =>
            Cmd.RunAsync(
                "docker",
                arguments,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["DOCKER_HOST"] = "unix://" + SocketPath,
                    ["DOCKER_CONTEXT"] = null,
                    ["DOCKER_CONFIG"] = DockerConfigDir,
                    ["DOCKER_TLS_VERIFY"] = null,
                    ["DOCKER_CERT_PATH"] = null,
                },
                timeout: timeout);

        public async ValueTask DisposeAsync()
        {
            await Process.DisposeAsync();
            try
            {
                if (Directory.Exists(DataDir))
                {
                    Directory.Delete(DataDir, recursive: true);
                }

                if (Directory.Exists(DockerConfigDir))
                {
                    Directory.Delete(DockerConfigDir, recursive: true);
                }

                if (File.Exists(SocketPath))
                {
                    File.Delete(SocketPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<SpawnedDaemon> SpawnDaemonAsync(string name)
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var dataDir = $"/tmp/cider-e2e-xp{name}-{id}";
        var socketPath = $"/tmp/cider-e2e-xp{name}-{id}.sock";
        var dockerConfigDir = dataDir + "-docker-config";
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(dockerConfigDir);

        // The real daemon executable, built alongside this test assembly by the project reference.
        var apphost = Path.Combine(AppContext.BaseDirectory, "cider");
        Assert.True(File.Exists(apphost), $"daemon apphost not found at {apphost}");

        var process = Cmd.Start(
            apphost,
            ["serve", "--socket", socketPath, "--data-dir", dataDir, "--log-level", "Debug", "--no-dns"],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // XPC pinned: prune's store-wide sweep only exists on this transport, and rmi here is
                // imageDelete(garbageCollect: false) -- no sweep -- so daemon B's prune stays the one
                // sweeping verb in the experiment (see class remarks).
                ["CIDER_RUNTIME_TRANSPORT"] = "xpc",

                // The gate stays ENABLED in both daemons -- the ticket's hypothesis is that a normal,
                // correctly-gated daemon still cannot see the other process's sweep. Nulled explicitly
                // so an ambient control-run leftover can never silently disable it here.
                ["CIDER_TEST_SKIP_BLOB_SWEEP_GATE"] = null,
                ["CIDER_DATA_DIR"] = null,
                ["CIDER_SOCKET"] = null,
                ["CIDER_LOG_LEVEL"] = null,
            });

        var daemon = new SpawnedDaemon
        {
            Name = name,
            Process = process,
            SocketPath = socketPath,
            DataDir = dataDir,
            DockerConfigDir = dockerConfigDir,
        };

        var ready = await DaemonFixture.EventuallyAsync(
            async () => !process.HasExited &&
                (await daemon.DockerAsync(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10))).Ok,
            DaemonReadyTimeout);
        if (!ready)
        {
            var tail = process.Stdout + "\n" + process.Stderr;
            await daemon.DisposeAsync();
            Assert.Fail($"throwaway daemon {name} never became ready on {socketPath}:\n{tail}");
        }

        output.WriteLine($"daemon {name}: pid {process.Pid}, socket {socketPath}, data dir {dataDir}, transport xpc, gate ENABLED (default)");
        return daemon;
    }

    /// <summary>Post-race dangling entries from the store's own index, as "reference -> digest" lines.</summary>
    private static string[] DanglingEntries() =>
        ImageStoreRaceTests.ScanAppleStoreState() is { } scan ? [.. scan.MissingBlobEntries] : [];

    [CrossProcessRaceFact]
    public async Task Two_gated_daemons_pull_vs_prune_across_processes_against_the_shared_store()
    {
        // The gate must be ENABLED in this experiment, in every process involved: assert the control
        // env var is not ambiently set (the spawned daemons additionally null it in their own env).
        Assert.True(
            Environment.GetEnvironmentVariable("CIDER_TEST_SKIP_BLOB_SWEEP_GATE") is null or "",
            "CIDER_TEST_SKIP_BLOB_SWEEP_GATE is set in the ambient environment -- this experiment " +
            "requires the cider-ede.31 gate ENABLED (normal config) in both daemons; unset it");

        // CLEAN BASELINE, required and recorded (cider-ede.41 comment #160): `container image ls`
        // exit 0 AND an index scan showing 0 entries missing blobs, before anything else runs. A
        // dirty baseline is a confound ("did we produce a NEW dangling entry?" becomes unanswerable)
        // and stops the run as INCONCLUSIVE.
        var baselineLs = await Cmd.RunAsync("container", ["image", "ls"], timeout: TimeSpan.FromSeconds(60));
        var baselineScan = ImageStoreRaceTests.ScanAppleStoreState();
        output.WriteLine(
            $"CLEAN BASELINE: `container image ls` ok={baselineLs.Ok}; index scan: " +
            (baselineScan is null
                ? "unavailable"
                : $"{baselineScan.Value.References.Count} entries, {baselineScan.Value.MissingBlobEntries.Count} missing blob(s)"));
        if (!baselineLs.Ok || baselineScan is null || baselineScan.Value.MissingBlobEntries.Count > 0)
        {
            Assert.Fail(
                "INCONCLUSIVE -- baseline store not clean before the cross-process race, stopping " +
                "rather than confounding the experiment:\n" +
                $"container image ls ok={baselineLs.Ok}\n{baselineLs}\n" +
                "index scan: " + (baselineScan is null
                    ? "unavailable"
                    : string.Join('\n', baselineScan.Value.MissingBlobEntries)));
        }

        var baselineReferences = new HashSet<string>(baselineScan.Value.References, StringComparer.Ordinal);

        // The churn image must be genuinely uncached before the experiment starts.
        Assert.True(
            !baselineReferences.Any(r => ImageStoreRaceTests.ReferenceMatches(r, ChurnImage)),
            $"churn image {ChurnImage} is already present in the shared store's index -- this " +
            "experiment needs a genuinely uncached image so every pull opens a real seconds-wide " +
            "write window; choose another image or remove it first");
        output.WriteLine($"churn image {ChurnImage}: ABSENT from the store index at baseline (asserted, not assumed)");

        await using var pullDaemon = await SpawnDaemonAsync("a");
        await using var pruneDaemon = await SpawnDaemonAsync("b");
        Assert.True(
            pullDaemon.Process.Pid != pruneDaemon.Process.Pid &&
            pullDaemon.Process.Pid != Environment.ProcessId &&
            pruneDaemon.Process.Pid != Environment.ProcessId,
            "the two daemons and the test runner must be three distinct OS processes");
        output.WriteLine(
            $"cross-process topology: test pid {Environment.ProcessId}, pull daemon A pid " +
            $"{pullDaemon.Process.Pid}, prune daemon B pid {pruneDaemon.Process.Pid} -- the ede.31 " +
            "gate instance in each daemon is process-local and cannot see the other's operations");

        var deadline = DateTime.UtcNow + RaceBudget;
        var stop = false;
        var pullCycles = 0;
        var pullSuccesses = 0;
        var sweepInterferenceFailures = 0;
        var otherPullFailures = new ConcurrentQueue<string>();
        var pruneAttempts = 0;
        var pruneFailures = new ConcurrentQueue<string>();
        var danglingSeen = new ConcurrentQueue<string>();
        var cycleLog = new ConcurrentQueue<string>();
        string? lastChurnDigest = null;

        async Task PruneLoopAsync()
        {
            while (!Volatile.Read(ref stop) && DateTime.UtcNow < deadline)
            {
                var prune = await pruneDaemon.DockerAsync(
                    ["image", "prune", "-f", "--filter", "label=" + NeverMatchingPruneLabel],
                    PruneTimeout);
                Interlocked.Increment(ref pruneAttempts);
                if (!prune.Ok)
                {
                    pruneFailures.Enqueue(prune.ToString());
                }
            }
        }

        async Task PullLoopAsync()
        {
            var clock = Stopwatch.StartNew();
            while (!Volatile.Read(ref stop) && DateTime.UtcNow < deadline)
            {
                var cycle = Interlocked.Increment(ref pullCycles);
                var cycleStart = clock.Elapsed;

                // ABSENCE ASSERTED FROM THE STORE INDEX BEFORE EACH PULL, recorded per cycle. After a
                // previous cycle's rmi (garbageCollect: false) the index entry is gone but the blobs
                // are orphans until daemon B's next sweep reclaims them -- wait for the previous
                // cycle's own index digest blob to actually disappear too, so every pull is a real,
                // full network pull with a real write window (a pull over still-present orphaned
                // content would be the warm-cycle confound avery's #149 warned about). This wait is
                // itself evidence: the blob vanishing here is daemon B's cross-process sweep working.
                var indexEntryPresent = ImageStoreRaceTests.ScanAppleStoreState() is { } s &&
                    s.References.Any(r => ImageStoreRaceTests.ReferenceMatches(r, ChurnImage));
                if (indexEntryPresent)
                {
                    await pullDaemon.DockerAsync(["rmi", "-f", ChurnImage], PullTimeout);
                }

                var previousDigest = lastChurnDigest;
                var blobGone = previousDigest is null || await DaemonFixture.EventuallyAsync(
                    () => Task.FromResult(!File.Exists(Path.Combine(
                        ImageStoreRaceTests.AppleStoreRoot, "content", "blobs", "sha256",
                        previousDigest["sha256:".Length..]))),
                    ReclaimWaitTimeout);
                if (!blobGone)
                {
                    cycleLog.Enqueue(
                        $"cycle {cycle}: previous cycle's index blob {lastChurnDigest} still on disk " +
                        $"after {ReclaimWaitTimeout} of continuous prune sweeps -- daemon B's sweep is " +
                        "not reclaiming, run is going vacuous");
                    Volatile.Write(ref stop, true);
                    break;
                }

                cycleLog.Enqueue(
                    $"cycle {cycle} @ {cycleStart.TotalSeconds:F0}s: {ChurnImage} absent from index" +
                    (lastChurnDigest is null ? "" : $", previous blob {lastChurnDigest[..19]}... reclaimed by daemon B's sweep") +
                    " -- pulling (uncached, real network write window)");

                var pull = await pullDaemon.DockerAsync(["pull", ChurnImage], PullTimeout);
                if (pull.Ok)
                {
                    Interlocked.Increment(ref pullSuccesses);
                    lastChurnDigest = ImageStoreRaceTests.ScanAppleStoreState() is { } post
                        ? post.References
                            .Where(r => ImageStoreRaceTests.ReferenceMatches(r, ChurnImage))
                            .Select(ChurnDigestOf)
                            .FirstOrDefault(d => d is not null)
                        : null;
                    await pullDaemon.DockerAsync(["rmi", "-f", ChurnImage], PullTimeout);
                }
                else
                {
                    var text = pull.ToString();
                    if (IsSweepInterferenceSignature(text))
                    {
                        Interlocked.Increment(ref sweepInterferenceFailures);
                        cycleLog.Enqueue(
                            $"cycle {cycle}: PULL FAILED WITH THE MID-WRITE SWEEP SIGNATURE while " +
                            "daemon B was pruning -- cross-process interference, both gates enabled:\n" + text);
                    }
                    else
                    {
                        otherPullFailures.Enqueue(text);
                        cycleLog.Enqueue($"cycle {cycle}: pull failed WITHOUT the sweep signature (foreign confound?):\n" + text);
                    }

                    lastChurnDigest = null;
                }

                // The ticket's failure criterion, checked from the store's own index after every
                // cycle so a reproduction stops the run at the iteration it happened on.
                foreach (var entry in DanglingEntries())
                {
                    danglingSeen.Enqueue($"after cycle {cycle} ({clock.Elapsed.TotalSeconds:F0}s in): {entry}");
                }

                if (!danglingSeen.IsEmpty)
                {
                    Volatile.Write(ref stop, true);
                }

                if (pullDaemon.Process.HasExited || pruneDaemon.Process.HasExited)
                {
                    cycleLog.Enqueue($"cycle {cycle}: a throwaway daemon EXITED mid-race (A exited={pullDaemon.Process.HasExited}, B exited={pruneDaemon.Process.HasExited})");
                    Volatile.Write(ref stop, true);
                }
            }
        }

        var raceClock = Stopwatch.StartNew();
        await Task.WhenAll(PullLoopAsync(), PruneLoopAsync());
        raceClock.Stop();

        foreach (var line in cycleLog)
        {
            output.WriteLine(line);
        }

        // RESTORE the shared store toward its recorded clean baseline through normal verbs only:
        // remove the one image this run pulled, let one final quiescent prune reclaim its orphans.
        var restoreRmi = await pullDaemon.DockerAsync(["rmi", "-f", ChurnImage], PullTimeout);
        var restorePrune = await pruneDaemon.DockerAsync(
            ["image", "prune", "-f", "--filter", "label=" + NeverMatchingPruneLabel], PruneTimeout);
        var finalScan = ImageStoreRaceTests.ScanAppleStoreState();
        var finalLs = await Cmd.RunAsync("container", ["image", "ls"], timeout: TimeSpan.FromSeconds(60));

        var daemonBReclaims = pruneDaemon.Process.Stdout
            .Split('\n')
            .Count(l => l.Contains("image prune: reclaimed", StringComparison.Ordinal));
        var fallbackWarnings =
            (pullDaemon.Process.Stdout + pullDaemon.Process.Stderr).Split('\n')
                .Count(l => l.Contains("falling back to the CLI", StringComparison.Ordinal)) +
            (pruneDaemon.Process.Stdout + pruneDaemon.Process.Stderr).Split('\n')
                .Count(l => l.Contains("falling back to the CLI", StringComparison.Ordinal));

        output.WriteLine(
            $"race budget {RaceBudget} (wall {raceClock.Elapsed}): {pullCycles} pull cycle(s) " +
            $"({pullSuccesses} completed, {sweepInterferenceFailures} failed with the mid-write sweep " +
            $"signature, {otherPullFailures.Count} failed otherwise) in daemon A (pid {pullDaemon.Process.Pid}); " +
            $"{pruneAttempts} prune sweep(s) ({pruneFailures.Count} failed) in daemon B (pid {pruneDaemon.Process.Pid}), " +
            $"{daemonBReclaims} logged reclaim line(s); {fallbackWarnings} XPC->CLI fallback warning(s) " +
            "(nonzero would mean a verb left the pinned XPC path); " +
            $"restore: rmi ok={restoreRmi.Ok}, final prune ok={restorePrune.Ok}");
        output.WriteLine(
            "final state: index scan " +
            (finalScan is null
                ? "unavailable"
                : $"{finalScan.Value.References.Count} entries, {finalScan.Value.MissingBlobEntries.Count} missing blob(s)") +
            $"; container image ls ok={finalLs.Ok}");

        foreach (var failure in pruneFailures.Take(5))
        {
            output.WriteLine("prune failure (recorded, not itself the criterion):\n" + failure);
        }

        // OUTCOME A -- the ticket's failure criterion met: a dangling entry appeared (or resurfaced).
        // Reported with its exact identity and NOT repaired here: repairing the live shared store is
        // an operator step (cider-ede.41's own rule), and `container image ls` may now be broken
        // machine-wide (the known Apple failure mode).
        var midRaceDangling = danglingSeen.ToArray();
        var finalDangling = finalScan?.MissingBlobEntries ?? [];
        if (midRaceDangling.Length > 0 || finalDangling.Count > 0)
        {
            var annotated = midRaceDangling
                .Concat(finalDangling.Select(e => "at final scan: " + e))
                .Select(e => e.Contains(HistoricalTrackedDanglingDigest, StringComparison.Ordinal)
                    ? e + "  [matches the HISTORICAL tracked cider-ede.41 digest -- resurfaced, not new]"
                    : e + "  [NEW digest]")
                .ToArray();
            Assert.Fail(
                "OUTCOME A -- CROSS-PROCESS CORRUPTION REPRODUCED with the cider-ede.31 gate ENABLED " +
                "in both daemons: the shared store's index holds entr(ies) whose blob file is missing, " +
                "produced by a prune sweep in one process racing a pull mid-write in another -- the " +
                "in-process gate is demonstrably insufficient across processes. DO NOT repair from " +
                "code; record for the operator:\n" + string.Join('\n', annotated) +
                $"\n(after {pullCycles} pull cycle(s) / {pruneAttempts} prune sweep(s) over {raceClock.Elapsed})");
        }

        // Any pull failure WITHOUT the sweep signature is a foreign confound (registry throttling,
        // daemon death, ...) that silently narrows the write windows -- the run must not report a
        // negative result built on cycles that never raced (avery's #149).
        Assert.True(
            otherPullFailures.IsEmpty,
            $"{otherPullFailures.Count} pull failure(s) did not carry the mid-write sweep signature -- " +
            "foreign confound, the sustained run's negative result would be uninterpretable:\n" +
            string.Join('\n', otherPullFailures.Take(3)));

        // OUTCOME B path: the criterion did not fire. The store must be back at its clean baseline.
        Assert.True(
            finalLs.Ok,
            "`container image ls` exited non-zero after the race although the index scan found no " +
            "dangling entry -- still the ticket's failure criterion:\n" + finalLs);
        Assert.True(
            finalScan is not null &&
            baselineReferences.SetEquals(finalScan.Value.References),
            "the shared store's index does not match the recorded clean baseline after restoration:\n" +
            $"baseline ({baselineReferences.Count}): {string.Join(", ", baselineReferences.Order(StringComparer.Ordinal))}\n" +
            $"final ({finalScan?.References.Count ?? 0}): {string.Join(", ", (finalScan?.References ?? []).Order(StringComparer.Ordinal))}");

        output.WriteLine(
            sweepInterferenceFailures > 0
                ? $"OUTCOME B on the ticket's criterion (no dangling entry in {pullCycles} cycles), BUT " +
                  $"the cross-process interference MECHANISM fired {sweepInterferenceFailures} time(s) " +
                  "with both gates enabled -- report both numbers to the ticket verbatim"
                : $"OUTCOME B: no dangling entry and no cross-process interference in {pullCycles} " +
                  $"uncached pull cycle(s) against {pruneAttempts} store-wide prune sweep(s) over " +
                  $"{raceClock.Elapsed} -- per the ticket, the leading explanation shifts to Apple's " +
                  "own non-atomic pull");
    }

    /// <summary>The digest recorded in the store index for <paramref name="reference"/>, or null.</summary>
    private static string? ChurnDigestOf(string reference)
    {
        try
        {
            var statePath = Path.Combine(ImageStoreRaceTests.AppleStoreRoot, "state.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            return doc.RootElement.TryGetProperty(reference, out var entry) &&
                entry.TryGetProperty("digest", out var digest)
                ? digest.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
