using Xunit;

namespace Cider.E2E.Tests.Infrastructure;

/// <summary>
/// cider-0o3 finding #2: <see cref="DaemonFixture.FilterOwnedImageIdsAsync"/>'s parsing of a real
/// <c>docker images -a --no-trunc --format "{{.ID}}\t{{.Repository}}:{{.Tag}}"</c> listing is the one
/// genuinely destructive step teardown takes (everything else is a straight <c>docker rmi</c> of ids
/// this already decided to remove), and it had zero coverage that runs without a live daemon and
/// <c>CIDER_E2E=1</c>. These drive the pulled-out parsing seam, <see cref="DaemonFixture.ParseOwnedImageIds"/>,
/// directly — no daemon, no <c>CIDER_E2E</c> gate, every default <c>dotnet test</c> run.
/// </summary>
public sealed class DaemonFixtureImageOwnershipTests
{
    // Captured verbatim (2026-08-26) from `docker images -a --no-trunc --format
    // "{{.ID}}\t{{.Repository}}:{{.Tag}}"` against a real throwaway cider fixture daemon -- pins the
    // one genuine divergence from tests/compat/lib/daemon.sh's shell equivalent, which formats with a
    // plain space instead of this tab. The C# "\t" escape below produces the identical byte the real
    // CLI emits; this is not a re-derivation of the format, it is that captured line.
    private const string RealCapturedLine =
        "sha256:5d3b3e589fcf57f626c7967bff5171924cf9c55068911247a1f7bd2458e726c3\tcoredns/coredns:1.14.7";

    // Captured verbatim (2026-08-26) from the same run, right after `docker build -t
    // e2e/capture-b40417a3 .` against the fixture -- the real shape a BuildKitTests.UniqueTag/
    // BuildTests.Tag build leaves behind. Full command and raw multi-line output this and the two
    // constants below were pulled from are quoted in the cider-0o3 verification notes.
    private const string RealTaggedLine =
        "sha256:5341c44d24253c138f3aa5a8f1c14b9ad2a25110ddb7672e959a3568015990b8\te2e/capture-b40417a3:latest";

    // Captured verbatim (2026-08-26) from the same run, right after a plain `docker build .` (no
    // `-t`) against the fixture -- the real shape an untagged build leaves behind. This is the
    // synthetic-tagged/untagged residual cider-0o3 finding #1/#2 established is never visible through
    // cider's own listing API (ImageManager.VisibleReferences strips the cider-build-* marker before
    // ToSummary derives RepoTags, so it always renders as <none>:<none> here) and so is left alone by
    // FilterOwnedImageIdsAsync as an accepted residual under cider-24v's never-remove-what-we-did-not-
    // create rule, not reclaimed by this filter.
    private const string RealUntaggedLine =
        "sha256:fe34c8603e8a5a6819717f4243c637ce38f29da13d65a930db1c3ce3e7754024\t<none>:<none>";

    [Fact]
    public void Real_captured_line_is_kept_when_not_a_candidate()
    {
        // Not one of our candidate ids (it is a pre-existing base image no snapshot ever flagged as
        // new) -- must never appear in the result, whatever its tag looks like.
        var result = DaemonFixture.ParseOwnedImageIds(RealCapturedLine, candidateIds: ["sha256:deadbeef"]);

        Assert.Empty(result);
    }

    [Fact]
    public void Real_e2e_tagged_new_id_is_removed()
    {
        const string id = "sha256:5341c44d24253c138f3aa5a8f1c14b9ad2a25110ddb7672e959a3568015990b8";
        var listing = RealCapturedLine + "\n" + RealTaggedLine;

        var result = DaemonFixture.ParseOwnedImageIds(listing, candidateIds: [id]);

        Assert.Equal([id], result);
    }

    [Fact]
    public void Multi_tag_id_with_one_tag_outside_the_owned_prefixes_is_kept()
    {
        // One id, two repo:tag lines -- the real CLI emits one line per tag for a multi-tag image.
        // Every tag must be owned for the id to be removed; a single outside tag (a base image someone
        // also tagged as a build artifact, say) disqualifies the whole id.
        const string id = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        var listing = string.Join(
            '\n',
            id + "\te2e/capture-abc123:latest",
            id + "\talpine:3.19");

        var result = DaemonFixture.ParseOwnedImageIds(listing, candidateIds: [id]);

        Assert.Empty(result);
    }

    [Fact]
    public void Real_untagged_new_id_is_kept_as_the_accepted_residual()
    {
        // The real shape an untagged build leaves behind (RealUntaggedLine, above): it renders
        // <none>:<none>, not a cider-build-* tag -- that marker never survives cider's own listing
        // (cider-0o3 finding #2), so it is disqualified here by the EndsWith(":<none>") branch just
        // like any other dangling image, and stays in the store as the known, accepted residual under
        // cider-24v's rule that teardown never removes what this run cannot unambiguously claim.
        const string id = "sha256:fe34c8603e8a5a6819717f4243c637ce38f29da13d65a930db1c3ce3e7754024";

        var result = DaemonFixture.ParseOwnedImageIds(RealUntaggedLine, candidateIds: [id]);

        Assert.Empty(result);
    }

    [Fact]
    public void Pre_existing_id_not_passed_as_a_candidate_is_kept()
    {
        const string preExisting = "sha256:4444444444444444444444444444444444444444444444444444444444444444";
        const string candidate = "sha256:5555555555555555555555555555555555555555555555555555555555555555";
        var listing = string.Join(
            '\n',
            preExisting + "\te2e/capture-abc123:latest",
            candidate + "\te2e/capture-def456:latest");

        // preExisting carries an owned tag too, but it was never a candidate (the snapshot already
        // knew about it), so it must never be returned -- "owned tag" alone is not enough, "new since
        // our snapshot" is the other half of the test.
        var result = DaemonFixture.ParseOwnedImageIds(listing, candidateIds: [candidate]);

        Assert.Equal([candidate], result);
    }

    [Fact]
    public void Id_absent_from_the_listing_is_kept()
    {
        const string vanished = "sha256:6666666666666666666666666666666666666666666666666666666666666666";

        // The id was a candidate (new since the snapshot) but the teardown-side listing no longer
        // mentions it at all -- e.g. it vanished between the two `docker images` calls. Fails closed:
        // never returned, since nothing here can say what it was tagged with.
        var result = DaemonFixture.ParseOwnedImageIds(RealCapturedLine, candidateIds: [vanished]);

        Assert.Empty(result);
    }

    [Fact]
    public void Empty_listing_removes_nothing()
    {
        var result = DaemonFixture.ParseOwnedImageIds(string.Empty, candidateIds: ["sha256:deadbeef"]);

        Assert.Empty(result);
    }
}
