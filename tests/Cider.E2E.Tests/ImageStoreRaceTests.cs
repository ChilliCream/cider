using System.Collections.Concurrent;
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
    private static readonly string[] LoadImages = ["alpine:3.14", "alpine:3.20", "alpine:3.21"];
    private const string ChurnImage = "alpine:3.16";

    // cider-ede.37 leg 4 correction: the full minute this test was written with burns a minute of
    // Docker Hub traffic against four tags on every default `dotnet test` run on this shared machine.
    // Set CIDER_E2E_RACE_FULL=1 to run the full budget cider-ede.31's Verification section asked for;
    // left unset (the default), a much shorter budget still exercises the same race loops, just with
    // fewer iterations.
    private static readonly TimeSpan RaceBudget =
        string.Equals(Environment.GetEnvironmentVariable("CIDER_E2E_RACE_FULL"), "1", StringComparison.Ordinal)
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

    [E2EFact]
    public async Task Concurrent_pulls_survive_a_minute_of_rmi_churn_without_corrupting_the_store()
    {
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
        await Task.WhenAll(PullLoopAsync(), RmiLoopAsync(), RmiLoopAsync());

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
            $"race budget {RaceBudget}, concurrency 3 (1 pull loop + 2 rmi loops): " +
            $"{pullAttempts} pull attempts ({pullFailures} failed), " +
            $"{rmiAttempts} rmi/re-pull attempts ({rePullFailures} failed); " +
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
