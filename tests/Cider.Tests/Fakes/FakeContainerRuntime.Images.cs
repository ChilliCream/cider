using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cider.Core.Ids;
using Cider.Core.Runtime;

namespace Cider.Tests.Fakes;

public sealed partial class FakeContainerRuntime
{
    /// <summary>
    /// Test hook: fails the next <see cref="PullImageAsync"/> with this error — simulates the real
    /// adapter's registry 404/401 (RuntimeErrorKind.NotFound) so a caller can verify the failure
    /// surfaces before any progress is written.
    /// </summary>
    public RuntimeException? PullFailure { get; set; }

    /// <summary>
    /// Test hook: progress events the failing <see cref="PullImageAsync"/> reports before throwing
    /// <see cref="PullFailure"/> — e.g. a terminal error-only event, the way a runtime adapter may
    /// announce the failure it is about to throw.
    /// </summary>
    public IList<ProgressEvent> PullFailureProgress { get; } = [];

    private readonly List<RuntimeImageDetail> _images =
    [
        new RuntimeImageDetail
        {
            Id = FixedDigest("alpine:latest"),
            References = ["docker.io/library/alpine:latest"],
            Size = 7_800_000,
            Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Config = new ImageConfig
            {
                Cmd = ["/bin/sh"],
                Env = ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],
            },
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-alpine-1", "layer-alpine-2", "layer-alpine-3"],

            // The manifest's real per-layer sizes (cider-ede.20) — oldest first, same order as
            // Layers, summing to the image's total Size above.
            LayerSizes = [2_000_000, 3_000_000, 2_800_000],

            // Apple carries the image config's history array through verbatim, including the entries
            // that produced no layer; `docker history` is built from it. Five rows, two of them
            // EmptyLayer, so exactly three rows consume the three LayerSizes above.
            History =
            [
                new RuntimeImageHistory
                {
                    Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    CreatedBy = "ADD alpine-minirootfs.tar.gz / # buildkit",
                    Comment = "buildkit.dockerfile.v0",
                },
                new RuntimeImageHistory
                {
                    Created = DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
                    CreatedBy = "RUN apk add --no-cache curl",
                    Comment = "buildkit.dockerfile.v0",
                },
                new RuntimeImageHistory
                {
                    Created = DateTimeOffset.Parse("2026-01-01T00:00:02Z"),
                    CreatedBy = "ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                    Comment = "buildkit.dockerfile.v0",
                    EmptyLayer = true,
                },
                new RuntimeImageHistory
                {
                    Created = DateTimeOffset.Parse("2026-01-01T00:00:03Z"),
                    CreatedBy = "RUN adduser -D app",
                    Comment = "buildkit.dockerfile.v0",
                },
                new RuntimeImageHistory
                {
                    Created = DateTimeOffset.Parse("2026-01-01T00:00:04Z"),
                    CreatedBy = "CMD [\"/bin/sh\"]",
                    Comment = "buildkit.dockerfile.v0",
                    EmptyLayer = true,
                },
            ],
        },
        new RuntimeImageDetail
        {
            Id = FixedDigest("hello-world:latest"),
            References = ["docker.io/library/hello-world:latest"],
            Size = 13_300,
            Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Config = new ImageConfig { Cmd = ["/hello"] },
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-hello-1"],
        },
        new RuntimeImageDetail
        {
            Id = FixedDigest("busybox:latest"),
            References = ["docker.io/library/busybox:latest"],
            Size = 4_200_000,
            Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Config = new ImageConfig { Cmd = ["sh"] },
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-busybox-1"],
        },
        new RuntimeImageDetail
        {
            Id = FixedDigest("nginx:latest"),
            References = ["docker.io/library/nginx:latest"],
            Size = 142_000_000,
            Created = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Config = new ImageConfig { Cmd = ["nginx", "-g", "daemon off;"], ExposedPorts = ["80/tcp"] },
            Architecture = "arm64",
            Os = "linux",
            Layers = ["layer-nginx-1", "layer-nginx-2"],
        },
    ];

    /// <summary>
    /// Test hook (cider-ede.24 comment 66): fails the next <see cref="ListImagesAsync"/> call(s) with
    /// this error, simulating a poisoned Apple image store's <c>ListImagesAsync</c> now throwing on a
    /// total enumeration failure — so a caller-side test can prove
    /// <c>ImageManager.LoadAsync</c>/<c>LoadImagesAsync</c> still succeeds (falling back to the
    /// runtime's own <c>Loaded image:</c> names) when the before/after snapshot diff it uses can no
    /// longer be trusted. By default this fails every subsequent call (see
    /// <see cref="ListImagesFailuresRemaining"/> to arm a one-shot failure instead — e.g. the
    /// before-snapshot throws while the after-snapshot succeeds).
    /// </summary>
    public RuntimeException? ListImagesFailure { get; set; }

    /// <summary>
    /// How many more times <see cref="ListImagesFailure"/> throws before <see cref="ListImagesAsync"/>
    /// goes back to answering normally — decremented on each throw. Defaults to unlimited (every call
    /// fails once <see cref="ListImagesFailure"/> is set); set to a small number (e.g. 1) to model the
    /// asymmetric before-fails/after-succeeds ordering that a poisoned-store repair or a transient
    /// listing failure produces.
    /// </summary>
    public int ListImagesFailuresRemaining { get; set; } = int.MaxValue;

    public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct)
    {
        Record("ListImagesAsync");
        if (ListImagesFailure is { } failure && ListImagesFailuresRemaining > 0)
        {
            ListImagesFailuresRemaining--;
            throw failure;
        }

        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<RuntimeImage>>(_images.Cast<RuntimeImage>().ToList());
        }
    }

    /// <summary>
    /// Test hook (cider-ede.24): simulates a runtime whose <see cref="ListImagesAsync"/> has already
    /// degraded to an empty listing — the shape both transports now answer with instead of throwing
    /// when Apple's store holds a dangling content reference — so a caller-side test can prove
    /// <c>ImageManager.ListAsync</c> forwards that as a plain empty result (Docker's <c>200</c> with
    /// <c>[]</c>) rather than surfacing it as a failure.
    /// </summary>
    public void ClearImages()
    {
        lock (_sync)
        {
            _images.Clear();
        }
    }

    /// <summary>
    /// Test hook (cider-ede.32): appends <paramref name="detail"/> straight to the fixture, bypassing
    /// every normal write path (build/pull/tag), each of which only ever produces a well-formed
    /// <c>References</c> entry via <see cref="ImageReference.Parse"/>. A test that needs a reference
    /// no real write path can produce — e.g. one that fails to parse as an image reference at all, to
    /// exercise <c>ImageManager</c>'s <c>VisibleReferences</c>/<c>IsDangling</c> edge case — has no
    /// other way to get it into the fixture.
    /// </summary>
    public void AddImageDetail(RuntimeImageDetail detail)
    {
        lock (_sync)
        {
            _images.Add(detail);
        }
    }

    public Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct)
    {
        Record($"InspectImageAsync:{reference}");
        lock (_sync)
        {
            return Task.FromResult(FindImage(reference));
        }
    }

    public Task PullImageAsync(string reference, string? platform, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct)
    {
        Record($"PullImageAsync:{reference}");

        if (PullFailure is { } failure)
        {
            PullFailure = null;
            foreach (var reported in PullFailureProgress)
            {
                progress.Report(reported);
            }

            PullFailureProgress.Clear();
            throw failure;
        }

        if (!ImageReference.TryParse(reference, out var parsed))
        {
            throw RuntimeException.InvalidArgument($"invalid reference: {reference}");
        }

        var normalized = parsed.Normalize();

        progress.Report(new ProgressEvent { Status = $"Pulling from {normalized.Path}", Id = normalized.Tag ?? normalized.Digest });
        progress.Report(new ProgressEvent { Status = "Downloading", Id = "layer1", Current = 1, Total = 2 });
        progress.Report(new ProgressEvent { Status = "Downloading", Id = "layer1", Current = 2, Total = 2 });
        progress.Report(new ProgressEvent { Status = "Pull complete", Id = "layer1" });

        lock (_sync)
        {
            var normalizedRef = normalized.ToString();
            var existing = FindImage(normalized.Familiar());
            if (existing is null)
            {
                _images.Add(new RuntimeImageDetail
                {
                    Id = FixedDigest(normalizedRef),
                    References = [normalizedRef],
                    Size = 5_000_000,
                    Created = DateTimeOffset.UtcNow,
                    Config = new ImageConfig { Cmd = ["/bin/sh"] },
                    Architecture = "arm64",
                    Os = "linux",
                    Layers = ["layer-pulled-1"],
                });
            }
            else if (!existing.References.Contains(normalizedRef, StringComparer.Ordinal))
            {
                var index = _images.IndexOf(existing);
                _images[index] = existing with { References = [.. existing.References, normalizedRef] };
            }
        }

        return Task.CompletedTask;
    }

    public Task PushImageAsync(string reference, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct)
    {
        Record($"PushImageAsync:{reference}");
        progress.Report(new ProgressEvent { Status = "Pushed" });
        return Task.CompletedTask;
    }

    public Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct)
    {
        Record($"TagImageAsync:{sourceReference}->{targetReference}");
        lock (_sync)
        {
            var source = FindImage(sourceReference) ?? throw RuntimeException.NotFound($"no such image: {sourceReference}");
            var targetNormalized = ImageReference.Parse(targetReference).Normalize().ToString();
            if (!source.References.Contains(targetNormalized, StringComparer.Ordinal))
            {
                var index = _images.IndexOf(source);
                _images[index] = source with { References = [.. source.References, targetNormalized] };
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveImageAsync(string reference, bool force, CancellationToken ct)
    {
        Record($"RemoveImageAsync:{reference}:{force}");

        // Apple's `container image delete` resolves a *reference* only: handed a sha256:… id it
        // fails with "image with reference sha256:… not found", which is why every caller routes
        // through ImageManager.RuntimeReferenceFor. Modelling that refusal here is what lets the
        // prune and rmi paths catch a regression back to deleting by raw digest.
        if (IsDigestReference(reference))
        {
            throw RuntimeException.NotFound($"image with reference {reference} not found");
        }

        lock (_sync)
        {
            var image = FindImage(reference) ?? throw RuntimeException.NotFound($"no such image: {reference}");
            var normalizedTag = TryNormalizedTag(reference);
            if (normalizedTag is not null && image.References.Count > 1 && image.References.Contains(normalizedTag, StringComparer.Ordinal))
            {
                var index = _images.IndexOf(image);
                _images[index] = image with { References = image.References.Where(r => r != normalizedTag).ToList() };
                return Task.CompletedTask;
            }

            _images.Remove(image);
        }

        return Task.CompletedTask;
    }

    /// <summary>Records the call (and, cider-ehn, the deleted-image digests <c>PruneAsync</c> passed
    /// along) so tests can assert <c>PruneAsync</c> triggers this exactly once per prune (never per
    /// deleted image, never from a plain <c>rmi</c>) — the real XPC/CLI transports' own default no-op
    /// behavior is irrelevant to this fake, which exists only to let a test observe when/how often
    /// <see cref="IContainerRuntime.PruneImagesAsync"/> was called.</summary>
    public Task PruneImagesAsync(IReadOnlyList<string> deletedImageDigests, CancellationToken ct)
    {
        Record($"PruneImagesAsync:{string.Join(",", deletedImageDigests)}");
        return Task.CompletedTask;
    }

    public async Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct)
    {
        Record($"SaveImagesAsync:{string.Join(",", references)}");
        var bytes = Encoding.UTF8.GetBytes($"fake-tar:{string.Join(",", references)}");
        await tarOutput.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The bytes of the last archive handed to <see cref="LoadImagesAsync"/> — the commit/import
    /// paths build one themselves, so tests assert on it directly.
    /// </summary>
    public byte[]? LastLoadedTar { get; private set; }

    public async Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct)
    {
        Record("LoadImagesAsync");
        using var buffer = new MemoryStream();
        await tarInput.CopyToAsync(buffer, ct).ConfigureAwait(false);
        LastLoadedTar = buffer.ToArray();

        // A real OCI layout (what OciImageWriter produces for commit/import, or an index.json with
        // several manifests for a load-two-tags-at-once test) is registered under the id and
        // reference(s) it actually declares, exactly like `container image load` does — Apple keys
        // the image by the digest of the index blob index.json points at. Several manifest entries
        // sharing one digest is the multi-tag case: one image, several `Loaded image:` lines.
        if (TryReadOciLayout(LastLoadedTar, out var ociEntries))
        {
            lock (_sync)
            {
                foreach (var group in ociEntries.GroupBy(entry => entry.Id, StringComparer.Ordinal))
                {
                    var references = group.Select(entry => entry.Reference).ToList();
                    var existing = FindImage(group.Key);
                    if (existing is null)
                    {
                        _images.Add(new RuntimeImageDetail
                        {
                            Id = group.Key,
                            References = references,
                            Size = LastLoadedTar.Length,
                            Created = DateTimeOffset.UtcNow,
                            Config = new ImageConfig(),
                            Architecture = "arm64",
                            Os = "linux",
                            Layers = ["layer-loaded-1"],
                        });
                    }
                    else
                    {
                        var index = _images.IndexOf(existing);
                        _images[index] = existing with
                        {
                            References = existing.References.Union(references, StringComparer.Ordinal).ToList(),
                        };
                    }
                }
            }

            return ociEntries.Select(entry => entry.Reference).ToList();
        }

        const string reference = "docker.io/library/loaded:latest";
        lock (_sync)
        {
            if (FindImage(reference) is null)
            {
                _images.Add(new RuntimeImageDetail
                {
                    Id = FixedDigest(reference + buffer.Length),
                    References = [reference],
                    Size = buffer.Length,
                    Created = DateTimeOffset.UtcNow,
                    Config = new ImageConfig(),
                    Architecture = "arm64",
                    Os = "linux",
                });
            }
        }

        return [reference];
    }

    /// <summary>
    /// Test hook (cider-ede.31 correction): lets a test hold <see cref="BuildImageAsync"/> open
    /// mid-write, the same shape <c>XpcContainerRuntimeRemoveImageTests</c>' own pull gate uses, to
    /// prove <see cref="Cider.AppleContainer.BlobSweepGate"/> genuinely serializes an XPC-transport
    /// build against a concurrent <c>PruneImagesAsync</c> sweep rather than merely documenting that it
    /// should. <c>null</c> (the default) never blocks — every other test using this fake is unaffected.
    /// </summary>
    private TaskCompletionSource<bool>? _buildGate;
    private TaskCompletionSource<bool>? _buildBlockedSignal;

    public void ArmBuildGate()
    {
        _buildGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _buildBlockedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitUntilBuildBlockedAsync() => _buildBlockedSignal!.Task;

    public void ReleaseBuild() => _buildGate?.TrySetResult(true);

    public async Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct)
    {
        Record($"BuildImageAsync:{spec.ContextDir}");

        if (_buildGate is not null)
        {
            _buildBlockedSignal!.TrySetResult(true);
            await _buildGate.Task.ConfigureAwait(false);
        }

        progress.Report(new ProgressEvent { Stream = "Step 1/1 : FROM scratch\n" });

        // Mirrors AppleContainerRuntime.BuildImageAsync: an untagged build still gets a real
        // reference on the Apple side, just a synthetic one the manager must hide.
        var tags = spec.Tags.Count > 0 ? spec.Tags : [SyntheticBuildTag.New()];
        var references = tags.Select(t => ImageReference.Parse(t).Normalize().ToString()).ToList();
        var id = FixedDigest(tags[0] + Guid.NewGuid());

        lock (_sync)
        {
            _images.Add(new RuntimeImageDetail
            {
                Id = id,
                References = references,
                Size = 1_000,
                Created = DateTimeOffset.UtcNow,
                Config = new ImageConfig(),
                Architecture = "arm64",
                Os = "linux",
                Layers = ["layer-built-1"],
            });
        }

        // A runtime adapter that mistakes the Docker-shaped closing lines for its own emits these —
        // AppleContainerRuntime did until that was fixed, synthetic build tag and all. Keeping them
        // here is what makes ImageManager's "exactly once" assertions able to fail: without the
        // manager dropping runtime-reported terminal lines, the client sees each of them twice.
        progress.Report(new ProgressEvent { Stream = $"Successfully built {DockerId.Short(IdWithoutPrefix(id))}\n" });
        foreach (var tag in tags)
        {
            progress.Report(new ProgressEvent { Stream = $"Successfully tagged {tag}\n" });
        }

        return id;
    }

    public Task LoginAsync(RegistryAuth auth, CancellationToken ct)
    {
        Record($"LoginAsync:{auth.Username}");
        return Task.CompletedTask;
    }

    public Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct)
    {
        Record("GetDiskUsageAsync");
        lock (_sync)
        {
            return Task.FromResult(new RuntimeDiskUsage
            {
                ImagesBytes = _images.Sum(i => i.Size),
                ContainersBytes = 0,
                VolumesBytes = _volumes.Sum(v => v.SizeBytes ?? 0),
                BuildCacheBytes = 0,
                ImagesCount = _images.Count,
                ContainersCount = _containers.Count,
                VolumesCount = _volumes.Count,
            });
        }
    }

    private RuntimeImageDetail? FindImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var stripped = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference["sha256:".Length..] : reference;
        if (DockerId.IsFullId(stripped))
        {
            var byId = _images.FirstOrDefault(i => IdWithoutPrefix(i.Id) == stripped);
            if (byId is not null)
            {
                return byId;
            }
        }
        else if (DockerId.IsHexPrefix(stripped) && stripped.Length >= 4)
        {
            var matches = _images.Where(i => IdWithoutPrefix(i.Id).StartsWith(stripped, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        if (!ImageReference.TryParse(reference, out var parsed))
        {
            return null;
        }

        var familiar = parsed.Normalize().Familiar();
        var normalizedForm = parsed.Normalize().ToString();
        foreach (var image in _images)
        {
            foreach (var r in image.References)
            {
                if (!ImageReference.TryParse(r, out var rp))
                {
                    continue;
                }

                if (string.Equals(rp.Normalize().Familiar(), familiar, StringComparison.Ordinal) ||
                    string.Equals(rp.Normalize().ToString(), normalizedForm, StringComparison.Ordinal))
                {
                    return image;
                }
            }
        }

        return null;
    }

    /// <summary>A bare image id — the one thing Apple's reference-taking verbs cannot resolve.</summary>
    private static bool IsDigestReference(string reference)
    {
        var stripped = IdWithoutPrefix(reference);
        return reference.StartsWith("sha256:", StringComparison.Ordinal) || DockerId.IsFullId(stripped);
    }

    private static string? TryNormalizedTag(string reference)
    {
        var stripped = reference.StartsWith("sha256:", StringComparison.Ordinal) ? reference["sha256:".Length..] : reference;
        if (DockerId.IsFullId(stripped) || DockerId.IsHexPrefix(stripped))
        {
            return null;
        }

        return ImageReference.TryParse(reference, out var parsed) ? parsed.Normalize().ToString() : null;
    }

    private static string IdWithoutPrefix(string id) =>
        id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;

    /// <summary>Reads <c>index.json</c> out of an OCI-layout tar: one descriptor per referenced tag.</summary>
    private static bool TryReadOciLayout(byte[] tar, out IReadOnlyList<(string Reference, string Id)> entries)
    {
        entries = [];
        try
        {
            using var stream = new MemoryStream(tar, writable: false);
            using var reader = new TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                if (!string.Equals(entry.Name, "index.json", StringComparison.Ordinal) || entry.DataStream is null)
                {
                    continue;
                }

                using var content = new MemoryStream();
                entry.DataStream.CopyTo(content);
                using var document = JsonDocument.Parse(content.ToArray());
                var found = new List<(string, string)>();
                foreach (var descriptor in document.RootElement.GetProperty("manifests").EnumerateArray())
                {
                    var id = descriptor.GetProperty("digest").GetString() ?? "";
                    var reference = descriptor.GetProperty("annotations")
                        .GetProperty("org.opencontainers.image.ref.name").GetString() ?? "";
                    if (id.Length > 0 && reference.Length > 0)
                    {
                        found.Add((reference, id));
                    }
                }

                entries = found;
                return found.Count > 0;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return false;
        }

        return false;
    }

    private static string FixedDigest(string seed) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
}
