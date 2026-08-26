using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Services;

public sealed class ImageManagerTests : IDisposable
{
    private readonly string _tmpDir = Directory.CreateTempSubdirectory("ad-image-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (ImageManager Manager, FakeContainerRuntime Runtime) CreateManager()
    {
        var (manager, runtime, _) = CreateManagerWithEvents();
        return (manager, runtime);
    }

    private (ImageManager Manager, FakeContainerRuntime Runtime, EventBus Events) CreateManagerWithEvents(TimeProvider? timeProvider = null)
    {
        var runtime = new FakeContainerRuntime();
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir };
        var manager = timeProvider is null
            ? new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance)
            : new ImageManager(runtime, events, options, NullLogger<ImageManager>.Instance, timeProvider);
        return (manager, runtime, events);
    }

    /// <summary>Builds a manager over a <see cref="ManualTimeProvider"/> the test can advance, for the image
    /// detail cache's TTL-based drift protection (cider-ede.17).</summary>
    private (ImageManager Manager, FakeContainerRuntime Runtime, ManualTimeProvider Clock) CreateManagerWithClock()
    {
        var clock = new ManualTimeProvider();
        var (manager, runtime, _) = CreateManagerWithEvents(clock);
        return (manager, runtime, clock);
    }

    /// <summary>Replays everything published since <paramref name="since"/>; an <c>until</c> in the past ends the stream.</summary>
    private static async Task<List<EventMessage>> DrainEventsAsync(EventBus events, DateTimeOffset since)
    {
        var messages = new List<EventMessage>();
        await foreach (var message in events.Subscribe(Filters.Empty, since, DateTimeOffset.UtcNow, CancellationToken.None))
        {
            messages.Add(message);
        }

        return messages;
    }

    [Fact]
    public async Task ListAsync_ReturnsSeededImagesInDockerShape()
    {
        var (manager, _) = CreateManager();

        var images = await manager.ListAsync(true, Filters.Empty, false, CancellationToken.None);

        var alpine = Assert.Single(images, i => i.RepoTags.Contains("alpine:latest"));
        Assert.StartsWith("sha256:", alpine.Id, StringComparison.Ordinal);
        Assert.True(alpine.Size > 0);
        Assert.Contains(images, i => i.RepoTags.Contains("nginx:latest"));
        Assert.Contains(images, i => i.RepoTags.Contains("busybox:latest"));
        Assert.Contains(images, i => i.RepoTags.Contains("hello-world:latest"));
    }

    [Fact]
    public async Task InspectAsync_And_ListAsync_RepoDigests_NameTheIndexDigest_NotTheContentAddressedId()
    {
        // cider-ger.19 orchestrator follow-up (item 3): Docker's RepoDigests names the manifest
        // digest a reference actually resolves to -- Apple's raw index digest -- not the image id.
        // Since this task, image.Id is the content-addressed config digest instead, which is stable
        // across reloads but is not a digest a registry would ever hand back for a pull/push of this
        // reference, so RepoDigests must keep preferring IndexDigests over Id.
        var (manager, runtime) = CreateManager();
        var configDigest = "sha256:" + new string('c', 64);
        var indexDigest = "sha256:" + new string('i', 64);
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = configDigest,
            IndexDigests = [indexDigest],
            References = ["repodigest-check:v1"],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

        var inspect = await manager.InspectAsync("repodigest-check:v1", CancellationToken.None);
        Assert.Single(inspect.RepoDigests, d => d.EndsWith($"@{indexDigest}", StringComparison.Ordinal));
        Assert.DoesNotContain(inspect.RepoDigests, d => d.Contains(configDigest, StringComparison.Ordinal));

        var listed = await manager.ListAsync(true, Filters.Empty, false, CancellationToken.None);
        var summary = Assert.Single(listed, i => i.Id == configDigest);
        Assert.Single(summary.RepoDigests, d => d.EndsWith($"@{indexDigest}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectAsync_ByFamiliarName_ReturnsImageConfig()
    {
        var (manager, _) = CreateManager();

        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);

        Assert.Equal(["/bin/sh"], inspect.Config.Cmd);
        Assert.Contains("alpine:latest", inspect.RepoTags);
    }

    [Fact]
    public async Task InspectAsync_ByIdPrefix_ResolvesUniqueImage()
    {
        var (manager, _) = CreateManager();
        var full = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        var prefix = full.Id["sha256:".Length..][..10];

        var byPrefix = await manager.InspectAsync(prefix, CancellationToken.None);

        Assert.Equal(full.Id, byPrefix.Id);
    }

    [Fact]
    public async Task InspectAsync_UnknownReference_ThrowsNoSuchImage()
    {
        var (manager, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync("does-not-exist:latest", CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task PullAsync_NewImage_EmitsExpectedMessageSequenceEndingWithDownloaded()
    {
        var (manager, _) = CreateManager();
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.PullAsync("busybox2", null, null, null, progress, CancellationToken.None);

        Assert.Equal("Pulling from library/busybox2", messages[0].Status);
        Assert.Equal("latest", messages[0].Id);
        Assert.Equal($"Status: Downloaded newer image for busybox2:latest", messages[^1].Status);
    }

    [Fact]
    public async Task PullAsync_ExistingImage_EmitsUpToDateStatus()
    {
        var (manager, runtime) = CreateManager();
        // Ensure the image exists before pulling instead of relying on the fake's built-in seed
        // data (which happens to include alpine today but is not this test's concern).
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = "sha256:" + new string('a', 64),
            References = ["docker.io/library/alpine:latest"],
            Size = 7_800_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig { Cmd = ["/bin/sh"] },
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-alpine-1"],
        });
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.PullAsync("alpine", null, null, null, progress, CancellationToken.None);

        Assert.Equal("Status: Image is up to date for alpine:latest", messages[^1].Status);
    }

    [Fact]
    public async Task PullAsync_MissingManifest_Throws404WithoutWritingAnyProgress()
    {
        var (manager, runtime) = CreateManager();
        runtime.PullFailure = RuntimeException.NotFound("pull alpine:doesnotexist99: manifest unknown");
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PullAsync("alpine", "doesnotexist99", null, null, progress, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("manifest for alpine:doesnotexist99 not found: manifest unknown", ex.Message);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task PullAsync_MissingManifest_ReportedAsErrorEventFirst_StillThrows404WithoutWritingAnyProgress()
    {
        var (manager, runtime) = CreateManager();
        runtime.PullFailure = RuntimeException.NotFound("pull alpine:doesnotexist99: manifest unknown");

        // A runtime adapter may announce the failure it is about to throw as a terminal error-only
        // event; that is not progress and must not start the response.
        runtime.PullFailureProgress.Add(new ProgressEvent { Error = "pull alpine:doesnotexist99: manifest unknown" });
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PullAsync("alpine", "doesnotexist99", null, null, progress, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Equal("manifest for alpine:doesnotexist99 not found: manifest unknown", ex.Message);
        Assert.Empty(messages);
    }

    // ---- image detail cache (cider-ede.17) ---------------------------------------------------

    [Fact]
    public async Task EnsureImageAsync_SecondResolutionOfTheSameReference_MakesNoRuntimeCalls()
    {
        var (manager, runtime) = CreateManager();

        var first = await manager.EnsureImageAsync("alpine:latest", null, null, pullIfMissing: false, null, CancellationToken.None);
        var callsAfterFirst = runtime.Calls.Count;
        Assert.True(callsAfterFirst > 0, "the first resolution should have hit the runtime at least once");

        var second = await manager.EnsureImageAsync("alpine:latest", null, null, pullIfMissing: false, null, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(callsAfterFirst, runtime.Calls.Count);
    }

    [Fact]
    public async Task EnsureImageAsync_ResolutionsByDifferentEquivalentReferences_ShareTheCache()
    {
        var (manager, runtime) = CreateManager();

        // Familiar name, fully qualified name and image id all name the same image; once any one
        // of them has been resolved, the other spellings should be cache hits too.
        var byName = await manager.EnsureImageAsync("alpine:latest", null, null, pullIfMissing: false, null, CancellationToken.None);
        var callsAfterFirst = runtime.Calls.Count;

        var byQualifiedName = await manager.EnsureImageAsync(
            "docker.io/library/alpine:latest", null, null, pullIfMissing: false, null, CancellationToken.None);
        var byId = await manager.EnsureImageAsync(byName.Id, null, null, pullIfMissing: false, null, CancellationToken.None);

        Assert.Equal(byName.Id, byQualifiedName.Id);
        Assert.Equal(byName.Id, byId.Id);
        Assert.Equal(callsAfterFirst, runtime.Calls.Count);
    }

    [Fact]
    public async Task PullAsync_InvalidatesTheCache_SoASubsequentLookupSeesTheNewDigest()
    {
        var (manager, runtime, clock) = CreateManagerWithClock();
        var oldDigest = "sha256:" + new string('1', 64);
        var newDigest = "sha256:" + new string('2', 64);
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = oldDigest,
            References = ["docker.io/library/refreshed:latest"],
            Size = 100,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

        var before = await manager.InspectAsync("refreshed:latest", CancellationToken.None);
        Assert.Equal(oldDigest, before.Id);

        // A registry re-push landing a new digest under the same tag while cider wasn't looking —
        // simulated by reseeding the fake directly (bypassing ImageManager) the way a real pull
        // would land new content on the runtime side.
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = newDigest,
            References = ["docker.io/library/refreshed:latest"],
            Size = 200,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

        // Proves the cache, not coincidence, is what is being exercised: the runtime already moved
        // on, but a read through the manager should still be serving the id it cached before — and
        // making no runtime call to do it — as long as the entry is still within its TTL.
        var callsBeforeStillCached = runtime.Calls.Count;
        var stillCached = await manager.InspectAsync("refreshed:latest", CancellationToken.None);
        Assert.Equal(oldDigest, stillCached.Id);
        Assert.Equal(callsBeforeStillCached, runtime.Calls.Count);

        // Drift protection: once the entry outlives the cache's TTL, it is no longer served even
        // without any in-process mutation to bump the version — covering an out-of-band digest
        // change (a second `cider` process, or `container image pull` run directly) that this
        // manager never saw and so never had a reason to invalidate for.
        clock.Advance(TimeSpan.FromSeconds(31));
        var callsBeforeDrifted = runtime.Calls.Count;
        var drifted = await manager.InspectAsync("refreshed:latest", CancellationToken.None);
        Assert.Equal(newDigest, drifted.Id);
        Assert.True(runtime.Calls.Count > callsBeforeDrifted, "an expired entry should be a cache miss, making a fresh runtime call");

        // The explicit invalidation path (a pull through the manager itself) still works too, and
        // still needs no TTL expiry to see the new digest.
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);
        await manager.PullAsync("refreshed", null, null, null, progress, CancellationToken.None);

        var after = await manager.InspectAsync("refreshed:latest", CancellationToken.None);
        Assert.Equal(newDigest, after.Id);
    }

    [Fact]
    public async Task TagAsync_InvalidatesTheCache_SoARemoveSeesTheNewReference()
    {
        var (manager, _) = CreateManager();

        // Primes the cache with alpine's detail from before the tag exists.
        await manager.InspectAsync("alpine:latest", CancellationToken.None);

        await manager.TagAsync("alpine:latest", "alpine-renamed", null, CancellationToken.None);

        // If TagAsync had not invalidated the cache, RemoveAsync would resolve "alpine:latest" back
        // to the pre-tag detail (a single reference), conclude it is the last one and delete the
        // whole image outright instead of just untagging it — losing "alpine-renamed" along the way.
        var items = await manager.RemoveAsync("alpine:latest", force: false, noPrune: false, CancellationToken.None);

        var untagged = Assert.Single(items);
        Assert.Equal("alpine:latest", untagged.Untagged);
        Assert.Null(untagged.Deleted);

        var stillThere = await manager.InspectAsync("alpine-renamed:latest", CancellationToken.None);
        Assert.Contains("alpine-renamed:latest", stillThere.RepoTags);
    }

    [Fact]
    public async Task FindImageDetailAsync_CacheInvalidatedWhileARuntimeCallIsInFlight_DoesNotResurrectTheStaleResult()
    {
        // Reproduces the write/invalidate race directly: a resolution's InspectImageAsync call is
        // held open while a concurrent PullAsync bumps the cache version, then released. If the
        // cache write stamped a version read *after* the runtime call (rather than the one captured
        // before it started), it would win the race and wrongly cache the pre-invalidation detail as
        // current — so a later lookup would (wrongly) stay a cache hit instead of going back to the
        // runtime.
        var inner = new FakeContainerRuntime();
        var digest = "sha256:" + new string('3', 64);
        inner.SeedImage(new RuntimeImageDetail
        {
            Id = digest,
            References = ["docker.io/library/racer:latest"],
            Size = 100,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

        var gate = new GatedRuntime(inner);
        var events = new EventBus();
        var options = new CiderOptions { DataDir = _tmpDir };
        var manager = new ImageManager(gate, events, options, NullLogger<ImageManager>.Instance);

        gate.ArmInspectGate();
        var resolveTask = manager.InspectAsync("racer:latest", CancellationToken.None);
        await gate.WaitUntilInspectBlockedAsync();

        // A concurrent mutation (a pull of an unrelated image is enough — invalidation is
        // whole-cache) invalidates the cache while the resolution above is still awaiting its
        // InspectImageAsync call.
        var progress = new SyncProgress<JsonMessage>(_ => { });
        await manager.PullAsync("alpine", null, null, null, progress, CancellationToken.None);

        gate.ReleaseInspect();
        var first = await resolveTask;
        Assert.Equal(digest, first.Id);

        var callsAfterFirst = inner.Calls.Count;
        var second = await manager.InspectAsync("racer:latest", CancellationToken.None);
        Assert.Equal(digest, second.Id);
        Assert.True(
            inner.Calls.Count > callsAfterFirst,
            "the write raced by a concurrent invalidation must have been dropped, so this lookup should hit the runtime again instead of serving a stale cache entry");
    }

    [Fact]
    public async Task PushAsync_MissingImage_Throws404WithoutWritingAnyProgress()
    {
        var (manager, _) = CreateManager();
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PushAsync("no-such-image:latest", null, null, progress, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task RemoveAsync_ImageUsedByRunningContainer_WithoutForce_Throws409()
    {
        var (manager, runtime) = CreateManager();
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            State = RuntimeContainerState.Running,
            ImageReference = "alpine:latest",
        });

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.RemoveAsync("alpine:latest", force: false, noPrune: false, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("must force", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveAsync_TwoTagsOfSameRepository_WhileAContainerRuns_Throws409()
    {
        // Docker's classic store counts distinct *repository names*, not references, when deciding
        // whether a removal has to answer the running-container conflict (`isSingleReference`). Two
        // tags of the SAME repository (alpine:latest, alpine:v2) are a single repository name, so
        // removing one while a container runs off the image still 409s — unlike
        // two tags of two DIFFERENT repositories, which just untags.
        var (manager, runtime) = CreateManager();
        await manager.TagAsync("alpine:latest", "alpine", "v2", CancellationToken.None);
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            State = RuntimeContainerState.Running,
            ImageReference = "alpine:latest",
        });

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.RemoveAsync("alpine:v2", force: false, noPrune: false, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("must force", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web1", ex.Message, StringComparison.Ordinal);

        // Nothing was untagged or deleted — the conflict was raised before any removal happened.
        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(new[] { "alpine:latest", "alpine:v2" }, inspect.RepoTags);
    }

    [Fact]
    public async Task RemoveAsync_TwoTagsOfSameRepository_NoContainer_OnlyUntags()
    {
        // The repository-name count gates the running-container conflict ONLY. Untag vs. delete is
        // still the plain reference count, so with no container in the way `docker rmi alpine:v2`
        // drops that one tag and leaves both the image and alpine:latest alone.
        var (manager, _) = CreateManager();
        await manager.TagAsync("alpine:latest", "alpine", "v2", CancellationToken.None);

        var items = await manager.RemoveAsync("alpine:v2", force: false, noPrune: false, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("alpine:v2", item.Untagged);
        Assert.Null(item.Deleted);

        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(new[] { "alpine:latest" }, inspect.RepoTags);
    }

    [Fact]
    public async Task RemoveAsync_OneOfSeveralTags_WhileAContainerRuns_UntagsInsteadOf409()
    {
        // Docker refuses only the removal that would really delete the image; dropping one of several
        // tags leaves the running container's image in place, so it just untags.
        var (manager, runtime) = CreateManager();
        await manager.TagAsync("alpine:latest", "ad/alias", "1", CancellationToken.None);
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            State = RuntimeContainerState.Running,
            ImageReference = "alpine:latest",
        });

        var items = await manager.RemoveAsync("ad/alias:1", force: false, noPrune: false, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("ad/alias:1", item.Untagged);
        Assert.Null(item.Deleted);

        // The image and the tag the container runs off both survive.
        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(new[] { "alpine:latest" }, inspect.RepoTags);
    }

    [Fact]
    public async Task RemoveAsync_UnusedImage_DeletesAndUntags()
    {
        var (manager, _) = CreateManager();

        var items = await manager.RemoveAsync("busybox:latest", force: false, noPrune: false, CancellationToken.None);

        Assert.Contains(items, i => i.Untagged == "busybox:latest");
        Assert.Contains(items, i => i.Deleted is not null);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync("busybox:latest", CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAsync_ByTag_WithSiblingTags_OnlyUntags(bool force)
    {
        // Docker's `force` overrides the running-container conflict, never "untag" vs "delete".
        var (manager, _, events) = CreateManagerWithEvents();
        await manager.TagAsync("alpine:latest", "ad/alias", "1", CancellationToken.None);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var items = await manager.RemoveAsync("ad/alias:1", force, noPrune: false, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("ad/alias:1", item.Untagged);
        Assert.Null(item.Deleted);

        var published = await DrainEventsAsync(events, since);
        Assert.Contains(published, e => e.Type == "image" && e.Action == "untag" && e.Actor.ID == "ad/alias:1");
        Assert.DoesNotContain(published, e => e.Type == "image" && e.Action == "delete");

        // The image survives under its remaining tag and no longer carries the removed one.
        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(new[] { "alpine:latest" }, inspect.RepoTags);
    }

    [Fact]
    public async Task RemoveAsync_ById_UntagsEveryTagThenDeletes()
    {
        var (manager, _, events) = CreateManagerWithEvents();
        await manager.TagAsync("alpine:latest", "ad/alias", "1", CancellationToken.None);
        var imageId = (await manager.InspectAsync("alpine:latest", CancellationToken.None)).Id;
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var items = await manager.RemoveAsync(imageId, force: false, noPrune: false, CancellationToken.None);

        Assert.Contains(items, i => i.Untagged == "alpine:latest");
        Assert.Contains(items, i => i.Untagged == "ad/alias:1");
        Assert.Contains(items, i => i.Deleted == imageId);

        var published = await DrainEventsAsync(events, since);
        Assert.Contains(published, e => e.Type == "image" && e.Action == "delete" && e.Actor.ID == imageId);

        foreach (var reference in new[] { "alpine:latest", "ad/alias:1" })
        {
            var ex = await Assert.ThrowsAsync<DockerApiException>(
                () => manager.InspectAsync(reference, CancellationToken.None));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
        }
    }

    [Fact]
    public async Task RemoveAsync_LastRemainingTag_UntagsAndDeletes()
    {
        var (manager, _, events) = CreateManagerWithEvents();
        await manager.TagAsync("alpine:latest", "ad/alias", "1", CancellationToken.None);
        var imageId = (await manager.InspectAsync("alpine:latest", CancellationToken.None)).Id;
        await manager.RemoveAsync("ad/alias:1", force: false, noPrune: false, CancellationToken.None);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var items = await manager.RemoveAsync("alpine:latest", force: false, noPrune: false, CancellationToken.None);

        Assert.Contains(items, i => i.Untagged == "alpine:latest");
        Assert.Contains(items, i => i.Deleted == imageId);

        var published = await DrainEventsAsync(events, since);
        Assert.Contains(published, e => e.Type == "image" && e.Action == "delete");
    }

    [Fact]
    public async Task ListAsync_TaggedTwice_IsOneRowWithBothTags()
    {
        var (manager, _) = CreateManager();
        await manager.TagAsync("alpine:latest", "ad/alias", "1", CancellationToken.None);

        var images = await manager.ListAsync(true, Filters.Empty, false, CancellationToken.None);

        var alpine = Assert.Single(images, i => i.RepoTags.Contains("alpine:latest"));
        Assert.Contains("ad/alias:1", alpine.RepoTags);
    }

    [Fact]
    public async Task BuildAsync_WithRealTarContext_EmitsStreamAuxAndSuccessMessages()
    {
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var request = new BuildRequest { Tags = ["myapp:1.0"] };
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.BuildAsync(request, tar, progress, CancellationToken.None);

        Assert.Contains(messages, m => m.Stream is not null && m.Stream.Contains("Step 1/1", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Aux is BuildResultAux);
        Assert.Contains(messages, m => m.Stream is not null && m.Stream.StartsWith("Successfully built", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Stream == "Successfully tagged myapp:1.0\n");
    }

    /// <summary>
    /// Regression: a build context tar produced by macOS `tar` embeds xattrs
    /// (notably the automatic `com.apple.provenance` every file on a modern macOS carries) as pax
    /// extended-header records with raw binary values, plus AppleDouble `._name` sidecar entries.
    /// Before the fix, extracting such a context threw
    /// `System.IO.InvalidDataException: The extended header contains invalid records.` straight out
    /// of <see cref="TarFile.ExtractToDirectoryAsync(Stream, string, bool, CancellationToken)"/> —
    /// caught only as a generic exception by <c>BuildRoutes</c> and surfaced to the client as an
    /// opaque ndjson error instead of a successful build. The fixture below is a real archive
    /// captured with macOS `tar cf` from a directory containing a Dockerfile and a file carrying a
    /// custom xattr and a resource fork.
    /// </summary>
    [Fact]
    public async Task BuildAsync_WithMacOsTarContext_ToleratesAppleDoubleAndPaxXattrEntries()
    {
        var (manager, _) = CreateManager();
        await using var tar = new MemoryStream(LoadMacOsTarFixture());

        var request = new BuildRequest { Tags = ["myapp:1.0"] };
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.BuildAsync(request, tar, progress, CancellationToken.None);

        Assert.DoesNotContain(messages, m => m.Error is not null);
        Assert.Contains(messages, m => m.Aux is BuildResultAux);
        Assert.Contains(messages, m => m.Stream is not null && m.Stream.StartsWith("Successfully built", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Stream == "Successfully tagged myapp:1.0\n");
    }

    /// <summary>Loads the checked-in macOS-tar fixture, resolved relative to this source file so no
    /// project-file changes are needed to ship it alongside the test.</summary>
    private static byte[] LoadMacOsTarFixture([CallerFilePath] string sourcePath = "")
    {
        var fixturePath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "Fixtures", "macos-tar-build-context.tar");
        return File.ReadAllBytes(fixturePath);
    }

    [Fact]
    public async Task BuildAsync_NoTag_EmitsSuccessfullyBuiltOnceAndNoTaggedLine()
    {
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var request = new BuildRequest();
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.BuildAsync(request, tar, progress, CancellationToken.None);

        Assert.Single(messages, m => m.Stream is not null && m.Stream.StartsWith("Successfully built", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Stream is not null && m.Stream.StartsWith("Successfully tagged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_NoTag_ProducesDanglingImage_HiddenFromRepoTagsAndDigests()
    {
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);
        await manager.BuildAsync(new BuildRequest(), tar, progress, CancellationToken.None);

        var imageId = Assert.IsType<BuildResultAux>(messages.Single(m => m.Aux is not null).Aux).ID;
        var inspect = await manager.InspectAsync(imageId, CancellationToken.None);

        Assert.Empty(inspect.RepoTags);
        Assert.Empty(inspect.RepoDigests);

        var listed = await manager.ListAsync(true, Filters.Empty, false, CancellationToken.None);
        var summary = Assert.Single(listed, i => i.Id == inspect.Id);
        Assert.Empty(summary.RepoTags);
        Assert.Empty(summary.RepoDigests);
    }

    [Fact]
    public async Task BuildAsync_NoTag_MatchesDanglingFilter_AndHistoryHidesSyntheticTag()
    {
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);
        await manager.BuildAsync(new BuildRequest(), tar, progress, CancellationToken.None);
        var imageId = Assert.IsType<BuildResultAux>(messages.Single(m => m.Aux is not null).Aux).ID;

        var dangling = Filters.Parse("""{"dangling":{"true":true}}""");
        var danglingList = await manager.ListAsync(true, dangling, false, CancellationToken.None);
        Assert.Contains(danglingList, i => i.Id == imageId);

        var notDangling = Filters.Parse("""{"dangling":{"false":true}}""");
        var notDanglingList = await manager.ListAsync(true, notDangling, false, CancellationToken.None);
        Assert.DoesNotContain(notDanglingList, i => i.Id == imageId);

        var history = await manager.HistoryAsync(imageId, CancellationToken.None);
        Assert.All(history, h => Assert.True(h.Tags is null || h.Tags.Count == 0));
    }

    [Fact]
    public async Task HistoryAsync_ReportsTheRealBuildInstructions_NewestFirst()
    {
        var (manager, _) = CreateManager();

        // Docker builds `docker history` from the image config's history array, one row per build
        // instruction including the ones that produced no layer. Apple carries that array through
        // verbatim, so the daemon used to be throwing away values it already had: every row came
        // back with CreatedBy "" because it counted rootfs layers instead.
        var history = await manager.HistoryAsync("alpine", CancellationToken.None);

        Assert.Equal(5, history.Count);
        Assert.Equal("CMD [\"/bin/sh\"]", history[0].CreatedBy);
        Assert.Equal("RUN adduser -D app", history[1].CreatedBy);
        Assert.Equal("ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", history[2].CreatedBy);
        Assert.Equal("RUN apk add --no-cache curl", history[3].CreatedBy);
        Assert.Equal("ADD alpine-minirootfs.tar.gz / # buildkit", history[4].CreatedBy);
        Assert.Equal("buildkit.dockerfile.v0", history[0].Comment);

        // Only the newest row carries the image id and the tags; the rest are <missing>, exactly as
        // dockerd reports for layers whose intermediate ids it cannot recover.
        Assert.Contains("alpine:latest", history[0].Tags!);
        Assert.Equal("<missing>", history[1].Id);
        Assert.Null(history[1].Tags);
        Assert.True(history[0].Created >= history[4].Created, "rows must come back newest first");
    }

    [Fact]
    public async Task HistoryAsync_AssignsRealPerLayerSizes_WalkingHistoryNewestFirstAgainstTheManifestLayers()
    {
        var (manager, _) = CreateManager();

        // dockerd's own algorithm: walk `history[]` newest-first, and for every entry that is not
        // `empty_layer`, consume the manifest's `layers[]` from the end; `empty_layer` entries report
        // 0. The alpine fixture has 3 layers (2_000_000 / 3_000_000 / 2_800_000, oldest first) and 5
        // history rows with 2 empty ones, so exactly 3 of the 5 rows must consume a layer, newest
        // non-empty row first.
        var history = await manager.HistoryAsync("alpine", CancellationToken.None);

        Assert.Equal(5, history.Count);
        Assert.Equal("CMD [\"/bin/sh\"]", history[0].CreatedBy);
        Assert.Equal(0, history[0].Size); // EmptyLayer
        Assert.Equal("RUN adduser -D app", history[1].CreatedBy);
        Assert.Equal(2_800_000, history[1].Size); // newest real layer
        Assert.Equal("ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", history[2].CreatedBy);
        Assert.Equal(0, history[2].Size); // EmptyLayer
        Assert.Equal("RUN apk add --no-cache curl", history[3].CreatedBy);
        Assert.Equal(3_000_000, history[3].Size);
        Assert.Equal("ADD alpine-minirootfs.tar.gz / # buildkit", history[4].CreatedBy);
        Assert.Equal(2_000_000, history[4].Size); // oldest real layer

        // Total must equal the sum of the manifest's layer sizes, which is also the image's own Size.
        var inspect = await manager.InspectAsync("alpine", CancellationToken.None);
        Assert.Equal(inspect.Size, history.Sum(h => h.Size));
    }

    [Fact]
    public async Task InspectAsync_ReportsTheNewestHistoryEntrysComment()
    {
        var (manager, _) = CreateManager();

        // `docker commit --message` writes exactly this, and Apple keeps it; it used to be hardcoded
        // to "" on the way out.
        var inspect = await manager.InspectAsync("alpine", CancellationToken.None);

        Assert.Equal("buildkit.dockerfile.v0", inspect.Comment);
    }

    [Fact]
    public async Task HistoryAsync_WithoutAConfigHistory_FallsBackToOneRowPerLayer()
    {
        var (manager, _) = CreateManager();

        // hello-world has no history in the fake, like a minimal `container image load` would.
        var history = await manager.HistoryAsync("hello-world", CancellationToken.None);

        var only = Assert.Single(history);
        Assert.Equal("", only.CreatedBy);
        Assert.Contains("hello-world:latest", only.Tags!);
    }

    [Fact]
    public async Task PruneAsync_UnknownFilterKey_Is400()
    {
        var (manager, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"bogus":["x"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'bogus'", ex.Message);
    }

    [Fact]
    public async Task PruneAsync_RemovesDanglingSyntheticBuildImage()
    {
        var (manager, runtime) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);
        await manager.BuildAsync(new BuildRequest(), tar, progress, CancellationToken.None);
        var imageId = Assert.IsType<BuildResultAux>(messages.Single(m => m.Aux is not null).Aux).ID;

        var response = await manager.PruneAsync(Filters.Empty, CancellationToken.None);

        Assert.Contains(response.ImagesDeleted, i => i.Deleted == imageId);
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync(imageId, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);

        // …and it got there through the image's *reference*. Apple's `container image delete` cannot
        // resolve a bare sha256 id, so a prune that hands it one deletes nothing at all — the actual
        // reason `docker image prune -f` was a no-op before that was fixed.
        var delete = Assert.Single(runtime.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));
        Assert.DoesNotContain("sha256:", delete, StringComparison.Ordinal);
        Assert.Contains("cider-build-", delete, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruneAsync_DuplicateSyntheticBuildTagsSharingOneDigest_AllGoInOneCall()
    {
        // cider-ger.17: building the exact same untagged Dockerfile content twice in one daemon
        // instance leaves one digest carrying two `cider-build-*` synthetic tags (Apple lists one
        // row per reference, merged into a single RuntimeImage with two References here). Real
        // Docker's `image prune -f` clears every dangling reference for that digest in one call;
        // before this fix ImageManager only ever removed the *first* reference
        // (RuntimeReferenceFor's FirstOrDefault), so the digest survived under the second tag and
        // kept reappearing as dangling until a second `prune -f`.
        var (manager, runtime) = CreateManager();
        var imageId = "sha256:" + new string('d', 64);
        var tagA = SyntheticBuildTag.New();
        var tagB = SyntheticBuildTag.New();
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = imageId,
            References = [tagA, tagB],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-dup-1"],
        });

        var response = await manager.PruneAsync(Filters.Empty, CancellationToken.None);

        // One prune call reports the digest deleted exactly once…
        Assert.Single(response.ImagesDeleted, i => i.Deleted == imageId);

        // …and both underlying tags actually went, so the digest is gone for good — not merely
        // down to one leftover reference that would still be dangling.
        var removeCalls = runtime.Calls.Where(c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, removeCalls.Count);
        Assert.Contains(removeCalls, c => c.Contains(tagA, StringComparison.Ordinal));
        Assert.Contains(removeCalls, c => c.Contains(tagB, StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync(imageId, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);

        // A second prune call must find nothing left to do — the whole point of the fix.
        var second = await manager.PruneAsync(Filters.Empty, CancellationToken.None);
        Assert.DoesNotContain(second.ImagesDeleted, i => i.Deleted == imageId);
    }

    [Fact]
    public async Task PruneAsync_DefaultOnlyPrunesDanglingImages_TaggedUnusedImageSurvives()
    {
        var (manager, _) = CreateManager();
        var alpineId = (await manager.InspectAsync("alpine:latest", CancellationToken.None)).Id;

        // The fixture's seeded "alpine:latest" is tagged and unused by any container — dockerd's
        // default prune (danglingOnly, i.e. no `dangling` filter or `dangling=true`) must leave it.
        var response = await manager.PruneAsync(Filters.Empty, CancellationToken.None);

        Assert.DoesNotContain(response.ImagesDeleted, i => i.Deleted == alpineId);
        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(alpineId, inspect.Id);
    }

    [Fact]
    public async Task PruneAsync_DanglingFalse_AlsoPrunesUnusedTaggedImages()
    {
        var (manager, _) = CreateManager();
        var alpineId = (await manager.InspectAsync("alpine:latest", CancellationToken.None)).Id;

        // dockerd's ImagePrune: `dangling=false` (what `docker image prune -a` sends) widens the
        // candidate set from imageStore.Heads() (dangling only) to imageStore.Map() (everything),
        // so an unused *tagged* image becomes eligible too — before this fix the manager
        // hard-coded `if (!IsDangling(image)) continue;` and the filter's value was never read.
        var response = await manager.PruneAsync(Filters.Parse("""{"dangling":["false"]}"""), CancellationToken.None);

        Assert.Contains(response.ImagesDeleted, i => i.Deleted == alpineId);
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync("alpine:latest", CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task RemoveAsync_ByTheNewContentAddressedId_DeletesEndToEnd()
    {
        // cider-ger.19 orchestrator follow-up (item 6, "verify docker rmi <new-id> end to end"):
        // Apple's `image delete <ref>` only ever resolves a *reference*, never a bare digest of any
        // kind (see RuntimeReferenceFor's own doc comment) -- so what actually has to keep working
        // here is that `docker rmi <id>`, typed as `docker images -q` now prints it (the
        // content-addressed config digest), still resolves to the right image and deletes it through
        // its real tag. It does: FindImageDetailAsync already matches a request by Id (full or
        // prefix), and RuntimeReferenceFor always prefers a real reference over the id once the image
        // is found -- this exercises that whole path together rather than each half in isolation.
        var (manager, runtime) = CreateManager();
        var configDigest = "sha256:" + new string('c', 64);
        var indexDigest = "sha256:" + new string('i', 64);
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = configDigest,
            IndexDigests = [indexDigest],
            References = ["rmi-by-id-check:v1"],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });

        var items = await manager.RemoveAsync(configDigest, force: false, noPrune: false, CancellationToken.None);

        Assert.Contains(items, i => i.Deleted == configDigest);
        var delete = Assert.Single(runtime.Calls, c => c.StartsWith("RemoveImageAsync:", StringComparison.Ordinal));
        Assert.Contains("rmi-by-id-check:v1", delete, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.InspectAsync(configDigest, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task PruneAsync_ImageBoundToARunningContainerOnlyByDigest_IsNotDeleted()
    {
        // cider-ger.19 orchestrator follow-up (item b, the regression this task most needed a test
        // for): RuntimeImage.Id is now the content-addressed config digest, but a container's own
        // RuntimeContainer.ImageDigest is still whatever raw digest the engine handed it at creation
        // time -- Apple's index digest on both the CLI and XPC transports -- which no longer equals
        // Id. Seeded here exactly like AppleContainerRuntime/XpcContainerRuntime now report it:
        // IndexDigests carries the raw value, distinct from Id. The container's own ImageReference
        // deliberately does not name the image at all, so the *only* thing that can recognize it as
        // in-use is the digest join -- before this fix, PruneAsync's `usedIds` set was built from
        // container.ImageDigest and checked only against image.Id, which never matched again once Id
        // stopped being the raw digest, so `docker image prune -a` would have deleted an image still
        // backing this running container.
        var (manager, runtime) = CreateManager();
        var configDigest = "sha256:" + new string('c', 64);
        var indexDigest = "sha256:" + new string('i', 64);
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = configDigest,
            IndexDigests = [indexDigest],
            References = ["digest-only:v1"],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web1",
            State = RuntimeContainerState.Running,
            ImageReference = "",
            ImageDigest = indexDigest,
        });

        // dangling=false (docker image prune -a) so the candidate set includes tagged images too --
        // otherwise the in-use join is never even reached for a tagged image.
        var response = await manager.PruneAsync(Filters.Parse("""{"dangling":["false"]}"""), CancellationToken.None);

        Assert.DoesNotContain(response.ImagesDeleted, i => i.Deleted == configDigest);
        var inspect = await manager.InspectAsync("digest-only:v1", CancellationToken.None);
        Assert.Equal(configDigest, inspect.Id);
    }

    [Fact]
    public async Task RemoveAsync_ImageBoundToARunningContainerOnlyByDigest_WithoutForce_Throws409()
    {
        // The rmi in-use guard shares IsBoundTo with PruneAsync (previous test) -- exercised here too
        // since ThrowIfUsedByRunningContainerAsync used to have its own reference-based fallback that
        // could mask this exact join bug; ImageReference is left empty specifically to rule that
        // fallback out and isolate the digest path.
        var (manager, runtime) = CreateManager();
        var configDigest = "sha256:" + new string('c', 64);
        var indexDigest = "sha256:" + new string('i', 64);
        runtime.SeedImage(new RuntimeImageDetail
        {
            Id = configDigest,
            IndexDigests = [indexDigest],
            References = ["digest-only:v2"],
            Size = 1_000,
            Created = DateTimeOffset.UtcNow,
            Config = new ImageConfig(),
            Architecture = "arm64",
            Os = "linux",
        });
        runtime.SeedContainer(new RuntimeContainer
        {
            RuntimeId = "web2",
            State = RuntimeContainerState.Running,
            ImageReference = "",
            ImageDigest = indexDigest,
        });

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.RemoveAsync("digest-only:v2", force: false, noPrune: false, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("web2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruneAsync_InvalidDanglingValue_Is400()
    {
        var (manager, _) = CreateManager();

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"dangling":["maybe"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal("invalid filter 'dangling=[maybe]'", ex.Message);
    }

    [Fact]
    public async Task PruneAsync_UnparseableUntil_Is400_AndNothingIsRemoved()
    {
        var (manager, _) = CreateManager();
        var alpineId = (await manager.InspectAsync("alpine:latest", CancellationToken.None)).Id;

        // Before this fix, ImageManager.PruneAsync never even read the `until` filter, so a garbage
        // value was silently ignored rather than rejected.
        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => manager.PruneAsync(Filters.Parse("""{"dangling":["false"],"until":["not-a-time"]}"""), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Equal(
            "invalid value for 'until' filter: parsing time \"not-a-time\" as \"2006-01-02\": cannot parse \"not-a-time\" as \"2006\"",
            ex.Message);
        var inspect = await manager.InspectAsync("alpine:latest", CancellationToken.None);
        Assert.Equal(alpineId, inspect.Id);
    }

    [Theory]
    [InlineData("busybox", 1)]
    [InlineData("busybox:latest", 1)]
    [InlineData("docker.io/library/busybox", 1)]
    [InlineData("busy*", 1)]
    [InlineData("nonesuch", 0)]
    // A name match, not a substring search: dockerd globs the familiar reference.
    [InlineData("box", 0)]
    [InlineData("usybox", 0)]
    public async Task ListAsync_LegacyNameFilter_MatchesRepositoryNames(string filter, int expected)
    {
        var (manager, _) = CreateManager();

        var listed = await manager.ListAsync(true, Filters.Empty, false, CancellationToken.None, filter);

        Assert.Equal(expected, listed.Count);
    }

    [Fact]
    public async Task ListAsync_LegacyNameFilter_CombinesWithFilters()
    {
        var (manager, _) = CreateManager();

        var both = await manager.ListAsync(
            true,
            Filters.Parse("""{"dangling":["false"]}"""),
            false,
            CancellationToken.None,
            "busybox");
        var contradictory = await manager.ListAsync(
            true,
            Filters.Parse("""{"dangling":["true"]}"""),
            false,
            CancellationToken.None,
            "busybox");

        Assert.Single(both);
        Assert.Empty(contradictory);
    }

    [Fact]
    public async Task ListAsync_ReferenceFilter_NeverMatchesTheSyntheticBuildTag()
    {
        // `docker images --filter reference=cider-build-*` must not surface an untagged build:
        // the synthetic tag is hidden from RepoTags, so it may not be filterable either.
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);
        await manager.BuildAsync(new BuildRequest(), tar, progress, CancellationToken.None);
        var imageId = Assert.IsType<BuildResultAux>(messages.Single(m => m.Aux is not null).Aux).ID;

        var filter = Filters.Parse("""{"reference":{"cider-build-":true}}""");
        var listed = await manager.ListAsync(true, filter, false, CancellationToken.None);

        Assert.DoesNotContain(listed, i => i.Id == imageId);
    }

    [Fact]
    public async Task BuildAsync_Quiet_EmitsOnlyFinalId()
    {
        var (manager, _) = CreateManager();
        await using var tar = BuildTarWithDockerfile();

        var request = new BuildRequest { Tags = ["myapp:2.0"], Quiet = true };
        var messages = new List<JsonMessage>();
        var progress = new SyncProgress<JsonMessage>(messages.Add);

        await manager.BuildAsync(request, tar, progress, CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.NotNull(message.Stream);
        Assert.StartsWith("sha256:", message.Stream, StringComparison.Ordinal);
    }

    // ---- load ----------------------------

    [Fact]
    public async Task LoadImagesAsync_ATarCarryingTwoTags_ReturnsBothNormalizedReferences()
    {
        var (manager, _) = CreateManager();
        var id = TestDigest("two-tags");
        await using var tar = BuildOciIndexTar(id, "app:1", "app:2");

        var references = await manager.LoadImagesAsync(tar, progress: null, CancellationToken.None);

        Assert.Equal(["docker.io/library/app:1", "docker.io/library/app:2"], references);
    }

    [Fact]
    public async Task LoadImagesAsync_ReloadingTheSameTar_FallsBackToTheLoadedNames()
    {
        var (manager, _) = CreateManager();
        var id = TestDigest("reload-same");

        await using (var first = BuildOciIndexTar(id, "app:1", "app:2"))
        {
            await manager.LoadImagesAsync(first, progress: null, CancellationToken.None);
        }

        // Nothing appeared or changed on the runtime the second time around — the diff of
        // ListImagesAsync before/after is empty, so the fallback to the runtime's own `loaded`
        // names must still hand back both references rather than an empty list.
        await using var second = BuildOciIndexTar(id, "app:1", "app:2");
        var references = await manager.LoadImagesAsync(second, progress: null, CancellationToken.None);

        Assert.Equal(["docker.io/library/app:1", "docker.io/library/app:2"], references);
    }

    [Fact]
    public async Task LoadAsync_DelegatesToLoadImagesAsync_ProgressAndEventsUnchanged()
    {
        var (manager, runtime, events) = CreateManagerWithEvents();
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        var id = TestDigest("progress-tar");
        await using var tar = BuildOciIndexTar(id, "app:1");
        var messages = new List<JsonMessage>();

        await manager.LoadAsync(tar, new SyncProgress<JsonMessage>(messages.Add), CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal("Loaded image: app:1\n", message.Stream);
        Assert.Contains("LoadImagesAsync", runtime.Calls);

        var published = await DrainEventsAsync(events, since);
        var loadEvent = Assert.Single(published, e => e.Type == "image" && e.Action == "load");
        Assert.Equal("app:1", loadEvent.Actor.Attributes["name"]);
    }

    private static string TestDigest(string seed) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static MemoryStream BuildOciIndexTar(string id, params string[] references)
    {
        var manifests = references.Select(reference => new
        {
            mediaType = "application/vnd.oci.image.index.v1+json",
            digest = id,
            size = 1,
            annotations = new Dictionary<string, string> { ["org.opencontainers.image.ref.name"] = reference },
        });
        var json = JsonSerializer.Serialize(new { schemaVersion = 2, manifests });

        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.json")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)),
            });
        }

        stream.Position = 0;
        return stream;
    }

    // ---- commit / import ----------------------------

    [Fact]
    public async Task CommitAsync_WritesWellFormedOciLayoutTheRuntimeLoads()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("true", "grj-commit");

        var id = await harness.Images.CommitAsync(record, "committed", "1", "a comment", "me", [], CancellationToken.None);

        Assert.StartsWith("sha256:", id, StringComparison.Ordinal);
        Assert.Contains($"ExportContainerAsync:{record.RuntimeId}", harness.Runtime.Calls);

        var entries = ReadTarEntries(harness.Runtime.LastLoadedTar!);
        Assert.Equal("{\"imageLayoutVersion\":\"1.0.0\"}", Encoding.UTF8.GetString(entries["oci-layout"]));

        // Every blob is stored under its own digest — that is the whole content-addressing contract.
        foreach (var (name, content) in entries)
        {
            if (name.StartsWith("blobs/sha256/", StringComparison.Ordinal))
            {
                Assert.Equal(name["blobs/sha256/".Length..], Convert.ToHexStringLower(SHA256.HashData(content)));
            }
        }

        // index.json -> nested index (the image id) -> manifest -> config + layer.
        var top = JsonDocument.Parse(entries["index.json"]).RootElement.GetProperty("manifests")[0];
        Assert.Equal(id, top.GetProperty("digest").GetString());
        Assert.Equal("application/vnd.oci.image.index.v1+json", top.GetProperty("mediaType").GetString());
        Assert.Equal(
            "docker.io/library/committed:1",
            top.GetProperty("annotations").GetProperty("org.opencontainers.image.ref.name").GetString());

        var index = JsonDocument.Parse(Blob(entries, id)).RootElement;
        var manifestDigest = index.GetProperty("manifests")[0].GetProperty("digest").GetString()!;
        var manifest = JsonDocument.Parse(Blob(entries, manifestDigest)).RootElement;

        var configDigest = manifest.GetProperty("config").GetProperty("digest").GetString()!;
        var layer = manifest.GetProperty("layers")[0];
        var layerBytes = Blob(entries, layer.GetProperty("digest").GetString()!);
        Assert.Equal(layerBytes.Length, layer.GetProperty("size").GetInt64());

        // The layer descriptor digests the *gzipped* blob, rootfs.diff_ids the plain tar inside it.
        var config = JsonDocument.Parse(Blob(entries, configDigest)).RootElement;
        Assert.Equal(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Gunzip(layerBytes))),
            config.GetProperty("rootfs").GetProperty("diff_ids")[0].GetString());
        Assert.Equal("me", config.GetProperty("author").GetString());
        Assert.Equal("a comment", config.GetProperty("history")[0].GetProperty("comment").GetString());
        Assert.Equal("arm64", config.GetProperty("architecture").GetString());
        Assert.Equal("linux", config.GetProperty("os").GetString());

        // ...and the image is really there afterwards, under the requested tag.
        var inspect = await harness.Images.InspectAsync("committed:1", CancellationToken.None);
        Assert.Equal(id, inspect.Id);
    }

    [Fact]
    public async Task CommitAsync_CarriesTheContainerConfigAndAppliesChanges()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateAsync("alpine", "grj-changes", request =>
        {
            request.Cmd = ["sh", "-c", "sleep 1"];
            request.Env = ["KEEP=1"];
        });

        var id = await harness.Images.CommitAsync(
            record,
            "changed",
            null,
            null,
            null,
            ["CMD [\"/bin/true\"]", "ENV FOO=bar", "EXPOSE 8080", "LABEL owner=grj", "WORKDIR /w", "USER app", "VOLUME /data"],
            CancellationToken.None);

        var config = ConfigBlockOf(ReadTarEntries(harness.Runtime.LastLoadedTar!), id);

        Assert.Equal(["/bin/true"], config.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Contains("KEEP=1", config.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("FOO=bar", config.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
        Assert.True(config.GetProperty("ExposedPorts").TryGetProperty("8080/tcp", out _));
        Assert.Equal("grj", config.GetProperty("Labels").GetProperty("owner").GetString());
        Assert.Equal("/w", config.GetProperty("WorkingDir").GetString());
        Assert.Equal("app", config.GetProperty("User").GetString());
        Assert.True(config.GetProperty("Volumes").TryGetProperty("/data", out _));

        // Default tag, exactly like `docker commit c changed`.
        var images = await harness.Images.ListAsync(true, Filters.Empty, false, CancellationToken.None);
        var summary = Assert.Single(images, i => i.Id == id);
        Assert.Contains("changed:latest", summary.RepoTags);
    }

    [Fact]
    public async Task CommitAsync_ShellFormCmd_IsWrappedInTheDefaultShell()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("true", "grj-shellform");

        var id = await harness.Images.CommitAsync(record, "shellform", "1", null, null, ["CMD echo hi"], CancellationToken.None);

        var config = ConfigBlockOf(ReadTarEntries(harness.Runtime.LastLoadedTar!), id);
        Assert.Equal(["/bin/sh", "-c", "echo hi"], config.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task CommitAsync_UnsupportedChange_Is400AndNothingIsExported()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("true", "grj-badchange");

        var ex = await Assert.ThrowsAsync<DockerApiException>(
            () => harness.Images.CommitAsync(record, "bad", "1", null, null, ["RUN apk add curl"], CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("RUN", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Runtime.Calls, c => c.StartsWith("ExportContainerAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitAsync_WithoutRepo_LeavesTheImageDanglingLikeDocker()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("true", "grj-untagged");

        var id = await harness.Images.CommitAsync(record, null, null, null, null, [], CancellationToken.None);

        var images = await harness.Images.ListAsync(true, Filters.Empty, false, CancellationToken.None);
        var summary = Assert.Single(images, i => i.Id == id);
        Assert.Empty(summary.RepoTags);

        var dangling = await harness.Images.ListAsync(true, Filters.Parse("{\"dangling\":[\"true\"]}"), false, CancellationToken.None);
        Assert.Contains(dangling, i => i.Id == id);
    }

    [Fact]
    public async Task CommitAsync_PublishesACommitEvent()
    {
        await using var harness = await ContainerTestHarness.CreateAsync();
        var record = await harness.CreateShellAsync("true", "grj-event");
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);

        var id = await harness.Images.CommitAsync(record, "evented", "1", null, null, [], CancellationToken.None);

        var events = await DrainEventsAsync(harness.Events, since);
        var message = Assert.Single(events, e => e.Type == "image" && e.Action == "commit");
        Assert.Equal("evented:1", message.Actor.Attributes["name"]);
        Assert.StartsWith("sha256:", id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_LoadsATarballAndReportsTheNewId()
    {
        var (manager, runtime) = CreateManager();
        await using var rootFs = BuildRootFsTar();
        var messages = new List<JsonMessage>();

        var id = await manager.ImportAsync(
            rootFs, "imported", "9", "from a tar", ["CMD [\"/bin/sh\"]"], new SyncProgress<JsonMessage>(messages.Add), CancellationToken.None);

        Assert.Equal(id, Assert.Single(messages).Status);

        var config = ConfigBlockOf(ReadTarEntries(runtime.LastLoadedTar!), id);
        Assert.Equal(["/bin/sh"], config.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var inspect = await manager.InspectAsync("imported:9", CancellationToken.None);
        Assert.Equal(id, inspect.Id);
    }

    [Fact]
    public async Task ImportAsync_AcceptsAGzippedTarball()
    {
        var (manager, runtime) = CreateManager();
        var plain = BuildRootFsTar().ToArray();
        var gzipped = new MemoryStream();
        using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        gzipped.Position = 0;

        var id = await manager.ImportAsync(gzipped, "gz", "1", null, [], new SyncProgress<JsonMessage>(_ => { }), CancellationToken.None);

        // The layer's diff id must digest the *decompressed* tar, i.e. exactly the bytes we handed in.
        var entries = ReadTarEntries(runtime.LastLoadedTar!);
        var index = JsonDocument.Parse(Blob(entries, id)).RootElement;
        var manifest = JsonDocument.Parse(Blob(entries, index.GetProperty("manifests")[0].GetProperty("digest").GetString()!)).RootElement;
        var config = JsonDocument.Parse(Blob(entries, manifest.GetProperty("config").GetProperty("digest").GetString()!)).RootElement;
        Assert.Equal(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(plain)),
            config.GetProperty("rootfs").GetProperty("diff_ids")[0].GetString());
    }

    [Fact]
    public async Task ImportAsync_UnsupportedChange_Is400()
    {
        var (manager, _) = CreateManager();
        await using var rootFs = BuildRootFsTar();

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => manager.ImportAsync(
            rootFs, "bad", "1", null, ["COPY . /"], new SyncProgress<JsonMessage>(_ => { }), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.Status);
    }

    private static Dictionary<string, byte[]> ReadTarEntries(byte[] tar)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = new MemoryStream(tar, writable: false);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null)
            {
                continue;
            }

            using var content = new MemoryStream();
            entry.DataStream.CopyTo(content);
            entries[entry.Name] = content.ToArray();
        }

        return entries;
    }

    private static byte[] Blob(Dictionary<string, byte[]> entries, string digest) =>
        entries[$"blobs/sha256/{digest["sha256:".Length..]}"];

    /// <summary>The <c>config.config</c> block of the image whose index digest is <paramref name="id"/>.</summary>
    private static JsonElement ConfigBlockOf(Dictionary<string, byte[]> entries, string id)
    {
        var index = JsonDocument.Parse(Blob(entries, id)).RootElement;
        var manifest = JsonDocument.Parse(Blob(entries, index.GetProperty("manifests")[0].GetProperty("digest").GetString()!)).RootElement;
        var config = JsonDocument.Parse(Blob(entries, manifest.GetProperty("config").GetProperty("digest").GetString()!)).RootElement;
        return config.GetProperty("config");
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static MemoryStream BuildRootFsTar()
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "x")
            {
                DataStream = new MemoryStream("hello\n"u8.ToArray()),
            });
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> capture for tests. The built-in <see cref="Progress{T}"/>
    /// marshals each <c>Report</c> through the <see cref="SynchronizationContext"/> captured at
    /// construction time; under the test host there is none, so it falls back to posting each report
    /// via the thread pool. That makes delivery asynchronous and unordered relative to the awaited
    /// call that produced it, so asserting on the collected list right after <c>await</c> races with
    /// still-pending callbacks. Production never hits this: the real <c>NdjsonProgress</c> (see
    /// Cider.Daemon/Hosting/DockerResults.cs) reports synchronously on the calling thread. This
    /// type matches that contract so tests observe the same ordering/completeness guarantees as prod.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when <see cref="Advance"/> is
    /// called — lets a test cross the image detail cache's TTL (cider-ede.17) without a real wait.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Wraps a <see cref="FakeContainerRuntime"/> to hold one <see cref="InspectImageAsync"/> call
    /// open until released — lets a test put a genuine concurrent operation in flight around a
    /// runtime call (the write/invalidate race, cider-ede.17) without needing the fake itself to know
    /// about blocking. Every other member forwards straight through, untouched.
    /// </summary>
    private sealed class GatedRuntime(FakeContainerRuntime inner) : IContainerRuntime
    {
        // `_releaseGate`/`_blockedSignal` are set by ArmInspectGate and never reassigned afterwards,
        // so ReleaseInspect (called from the test, arbitrarily later) always completes the same
        // TaskCompletionSource that InspectImageAsync is awaiting. `_armed` is the one-shot switch:
        // only the InspectImageAsync call that wins the Exchange blocks on the gate at all, so an
        // unrelated concurrent resolution started while the gate is held (e.g. a different
        // reference's lookup) passes straight through instead of piling up behind the same gate.
        private TaskCompletionSource<bool>? _releaseGate;
        private TaskCompletionSource<bool>? _blockedSignal;
        private int _armed;

        /// <summary>The next call to <see cref="InspectImageAsync"/> awaits until <see cref="ReleaseInspect"/> is called.</summary>
        public void ArmInspectGate()
        {
            _releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _armed, 1);
        }

        /// <summary>Completes once the gated <see cref="InspectImageAsync"/> call has started waiting.</summary>
        public Task WaitUntilInspectBlockedAsync() => _blockedSignal?.Task ?? Task.CompletedTask;

        public void ReleaseInspect() => _releaseGate?.TrySetResult(true);

        public async Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _blockedSignal!.TrySetResult(true);
                await _releaseGate!.Task.ConfigureAwait(false);
            }

            return await inner.InspectImageAsync(reference, ct).ConfigureAwait(false);
        }

        public Task<Cider.Core.Runtime.RuntimeInfo> GetInfoAsync(CancellationToken ct) => inner.GetInfoAsync(ct);

        public Task EnsureReadyAsync(CancellationToken ct) => inner.EnsureReadyAsync(ct);

        public Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct) => inner.CreateContainerAsync(spec, ct);

        public Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct) =>
            inner.StartContainerAsync(runtimeId, options, ct);

        public Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct) =>
            inner.StopContainerAsync(runtimeId, timeoutSeconds, signal, ct);

        public Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct) => inner.KillContainerAsync(runtimeId, signal, ct);

        public Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct) => inner.RemoveContainerAsync(runtimeId, force, ct);

        public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct) => inner.ListContainersAsync(ct);

        public Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct) => inner.InspectContainerAsync(runtimeId, ct);

        public Task<(int ExitCode, DateTimeOffset ExitedAt)?> WaitContainerAsync(string runtimeId, CancellationToken ct) =>
            inner.WaitContainerAsync(runtimeId, ct);

        public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct) => inner.ExecAsync(runtimeId, spec, ct);

        public Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct) =>
            inner.OpenLogsAsync(runtimeId, follow, tail, ct);

        public Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct) => inner.GetStatsAsync(runtimeId, ct);

        public Task CopyFromContainerAsync(string runtimeId, string containerPath, string localDestinationDir, CancellationToken ct) =>
            inner.CopyFromContainerAsync(runtimeId, containerPath, localDestinationDir, ct);

        public Task CopyToContainerAsync(string runtimeId, string localSourcePath, string containerPath, CancellationToken ct) =>
            inner.CopyToContainerAsync(runtimeId, localSourcePath, containerPath, ct);

        public Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct) => inner.ExportContainerAsync(runtimeId, tarOutput, ct);

        public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => inner.ListImagesAsync(ct);

        public Task PullImageAsync(string reference, string? platform, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct) =>
            inner.PullImageAsync(reference, platform, auth, progress, ct);

        public Task PushImageAsync(string reference, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct) =>
            inner.PushImageAsync(reference, auth, progress, ct);

        public Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct) =>
            inner.TagImageAsync(sourceReference, targetReference, ct);

        public Task RemoveImageAsync(string reference, bool force, CancellationToken ct) => inner.RemoveImageAsync(reference, force, ct);

        public Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct) =>
            inner.SaveImagesAsync(references, tarOutput, ct);

        public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => inner.LoadImagesAsync(tarInput, ct);

        public Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct) =>
            inner.BuildImageAsync(spec, progress, ct);

        public Task LoginAsync(RegistryAuth auth, CancellationToken ct) => inner.LoginAsync(auth, ct);

        public Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct) => inner.ListNetworksAsync(ct);

        public Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct) => inner.InspectNetworkAsync(name, ct);

        public Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct) => inner.CreateNetworkAsync(spec, ct);

        public Task RemoveNetworkAsync(string name, CancellationToken ct) => inner.RemoveNetworkAsync(name, ct);

        public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct) => inner.ListVolumesAsync(ct);

        public Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct) => inner.InspectVolumeAsync(name, ct);

        public Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct) => inner.CreateVolumeAsync(spec, ct);

        public Task RemoveVolumeAsync(string name, bool force, CancellationToken ct) => inner.RemoveVolumeAsync(name, force, ct);

        public Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct) => inner.GetDiskUsageAsync(ct);

        public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct) => inner.GetBuilderStatusAsync(ct);

        public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct) => inner.StartBuilderAsync(cpus, memoryBytes, ct);

        public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct) => inner.DialBuilderAsync(ct);
    }

    private static MemoryStream BuildTarWithDockerfile()
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("FROM scratch\n")),
            };
            writer.WriteEntry(entry);
        }

        stream.Position = 0;
        return stream;
    }
}
