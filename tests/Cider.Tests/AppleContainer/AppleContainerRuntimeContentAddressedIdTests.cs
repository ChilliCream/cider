using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// cider-ger.19: Apple's own <c>id</c> on an <c>image ls</c>/<c>image inspect</c> row is the OCI
/// *index* digest, and <c>container image load</c> recomputes/reassigns it on every import — even
/// for a byte-identical reload (reproduced with two loads of the same BuildKit-exported docker tar:
/// same manifest+config digests, different <c>container image ls</c> id each time). That drift
/// surfaced as <c>docker images -q</c>/<c>--iidfile</c> disagreeing for two builds of the same
/// Dockerfile (tests/compat/run-buildkit.sh scenario 6).
///
/// <see cref="AppleContainerRuntime"/> now recovers a genuinely content-addressed id from Apple's
/// local blob store instead: the picked variant's manifest (resolved the same way
/// <c>AppleContainerRuntimeExposedPortsTests</c> already exercises for config/layer recovery) names
/// its config blob's real digest, and that digest is what Docker's own image id actually is.
/// </summary>
public sealed class AppleContainerRuntimeContentAddressedIdTests : IDisposable
{
    private const string Reference = "docker.io/library/alpine:3.22";
    private const string ManifestDigestHex = "082519ca7ba7ee1780fae75960b9e349d9c02290cf0746895d41dc5b0c6f2091";
    private const string ConfigDigestHex = "a0f7ea41a0096192378f7e1eb0d8d1c2e98208f005954e513eea35996e13e394";

    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "cider-ger19-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_appRoot))
        {
            Directory.Delete(_appRoot, recursive: true);
        }
    }

    /// <summary>One `image ls`/`image inspect` row whose Apple-assigned <c>id</c> (the index digest)
    /// is <paramref name="appleIndexId"/> — the value that drifts across identical reloads — but
    /// whose variant manifest digest (and therefore config digest, once read from the local store) is
    /// always the same, matching the task's evidence that buildkit's own export is deterministic.</summary>
    private static string ImageRowJson(string appleIndexId) => $$"""
    [{
      "configuration": {
        "name": "docker.io/library/alpine:3.22",
        "descriptor": {"digest":"sha256:{{appleIndexId}}","mediaType":"application/vnd.oci.image.index.v1+json","size":1},
        "creationDate": "2026-08-25T19:33:00Z"
      },
      "id": "{{appleIndexId}}",
      "variants": [
        {
          "config": {
            "architecture": "arm64",
            "os": "linux",
            "created": "2026-08-25T19:33:00Z",
            "config": {"Cmd":["/bin/sh"]}
          },
          "digest": "sha256:{{ManifestDigestHex}}",
          "platform": {"architecture":"arm64","os":"linux"},
          "size": 12345
        }
      ]
    }]
    """;

    private static string StatusJson(string appRoot) =>
        $$"""{"status":"running","appRoot":"{{appRoot.Replace("\\", "\\\\")}}","installRoot":"/usr/local/"}""";

    private static string ManifestBlob(string configDigestHex) =>
        "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"," +
        "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\"," +
        "\"digest\":\"sha256:" + configDigestHex + "\",\"size\":1469}," +
        "\"layers\":[{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"sha256:layer1\",\"size\":3271046}]}";

    private void SeedLocalBlobStore()
    {
        var dir = Path.Combine(_appRoot, "content", "blobs", "sha256");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManifestDigestHex), ManifestBlob(ConfigDigestHex));
    }

    [Fact]
    public async Task ListImagesAsync_UsesTheLocalStoresConfigDigest_NotApplesIndexId()
    {
        SeedLocalBlobStore();
        var cli = new ScriptedListCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-first-load"));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        var image = Assert.Single(images);
        Assert.Equal($"sha256:{ConfigDigestHex}", image.Id);
        Assert.NotEqual("sha256:index-first-load", image.Id);
    }

    [Fact]
    public async Task ListImagesAsync_ReportsTheSameId_AcrossTwoLoadsOfIdenticalContent_DespiteAppleAssigningDifferentIndexIds()
    {
        // The exact bug: two `container image load` imports of byte-identical BuildKit output, each
        // getting its own fresh Apple index id, must not surface as two different Docker image ids.
        SeedLocalBlobStore();
        var runtime1 = new AppleContainerRuntime(
            new AppleContainerOptions(),
            NullLogger<AppleContainerRuntime>.Instance,
            new ScriptedListCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-after-load-1")));
        var runtime2 = new AppleContainerRuntime(
            new AppleContainerOptions(),
            NullLogger<AppleContainerRuntime>.Instance,
            new ScriptedListCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-after-load-2")));

        var afterFirstLoad = Assert.Single(await runtime1.ListImagesAsync(CancellationToken.None));
        var afterSecondLoad = Assert.Single(await runtime2.ListImagesAsync(CancellationToken.None));

        Assert.Equal(afterFirstLoad.Id, afterSecondLoad.Id);
        Assert.Equal($"sha256:{ConfigDigestHex}", afterFirstLoad.Id);
    }

    [Fact]
    public async Task ListImagesAsync_FallsBackToApplesId_WhenTheLocalBlobStoreHasNothingToRecover()
    {
        // No SeedLocalBlobStore(): AppRoot resolves, but the manifest blob does not exist on disk —
        // recovery is best-effort, exactly like the ExposedPorts/LayerSizes recovery it mirrors.
        var cli = new ScriptedListCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-only"));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var images = await runtime.ListImagesAsync(CancellationToken.None);

        var image = Assert.Single(images);
        Assert.Equal("sha256:index-only", image.Id);
    }

    [Fact]
    public async Task InspectImageAsync_UsesTheLocalStoresConfigDigest_NotApplesIndexId()
    {
        SeedLocalBlobStore();
        var cli = new ScriptedInspectCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-first-load"));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal($"sha256:{ConfigDigestHex}", detail!.Id);
    }

    [Fact]
    public async Task BuildImageAsync_ReportsTheContentAddressedConfigId_NotTheScrapedManifestDigest()
    {
        // cider-ger.20: a classic (untagged) build's own "exporting manifest sha256:<manifest digest>"
        // progress line is an OCI manifest digest, never the content-addressed *config* digest that
        // RuntimeImage.Id actually is elsewhere (ListImagesAsync/InspectImageAsync above) -- if
        // BuildImageAsync ever regressed to reporting that scraped digest again, its own freshly-built
        // id would never appear in a subsequent `docker images --filter dangling=true` listing. This is
        // the only test that would catch a regression of commit 12598cc.
        SeedLocalBlobStore();
        var cli = new ScriptedBuildCli(StatusJson(_appRoot), ImageRowJson(appleIndexId: "index-after-build"));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var id = await runtime.BuildImageAsync(
            new BuildSpec { ContextDir = Path.GetTempPath() },
            new Progress<ProgressEvent>(),
            CancellationToken.None);

        Assert.Equal($"sha256:{ConfigDigestHex}", id);
        Assert.NotEqual($"sha256:{ManifestDigestHex}", id);
    }

    /// <summary>Answers `system status`/`image ls` from canned strings; anything else fails like an
    /// unscripted call, exercised deliberately.</summary>
    private sealed class ScriptedListCli(string? statusJson, string imageLsJson)
        : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            if (args.Count >= 2 && args[0] == "system" && args[1] == "status")
            {
                return Task.FromResult(statusJson is null
                    ? new CliResult(1, "", "not running")
                    : new CliResult(0, statusJson, ""));
            }

            if (args.Count >= 2 && args[0] == "image" && (args[1] == "ls" || args[1] == "list"))
            {
                return Task.FromResult(new CliResult(0, imageLsJson, ""));
            }

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }

    /// <summary>Answers `system status`/`image inspect` from canned strings (siblings lookup via
    /// `image ls` is left unscripted on purpose — <c>WithSiblingReferencesAsync</c> tolerates that and
    /// falls back to the inspect-only detail, exactly like <c>AppleContainerRuntimeExposedPortsTests</c>).</summary>
    private sealed class ScriptedInspectCli(string? statusJson, string inspectJson)
        : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            if (args.Count >= 2 && args[0] == "system" && args[1] == "status")
            {
                return Task.FromResult(statusJson is null
                    ? new CliResult(1, "", "not running")
                    : new CliResult(0, statusJson, ""));
            }

            if (args.Count >= 2 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new CliResult(0, inspectJson, ""));
            }

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }

    /// <summary>Replays a classic build's progress output (an "exporting manifest sha256:…" line
    /// scraped for the pre-fix fallback id) and then answers `system status`/`image inspect` exactly
    /// like <see cref="ScriptedInspectCli"/>, so BuildImageAsync's post-build lookup resolves the
    /// content-addressed config id instead of the scraped manifest digest.</summary>
    private sealed class ScriptedBuildCli(string? statusJson, string inspectJson)
        : ContainerCli(new AppleContainerOptions(), NullLogger.Instance)
    {
        public override Task<CliResult> RunStreamingAsync(
            IReadOnlyList<string> args,
            Action<string, bool> onLine,
            CancellationToken ct,
            TimeSpan? timeout = null)
        {
            onLine($"#6 exporting manifest sha256:{ManifestDigestHex} done", false);
            return Task.FromResult(new CliResult(0, "", ""));
        }

        public override Task<CliResult> RunAsync(
            IReadOnlyList<string> args,
            CancellationToken ct,
            TimeSpan? timeout = null,
            string? stdin = null)
        {
            if (args.Count >= 2 && args[0] == "system" && args[1] == "status")
            {
                return Task.FromResult(statusJson is null
                    ? new CliResult(1, "", "not running")
                    : new CliResult(0, statusJson, ""));
            }

            if (args.Count >= 2 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new CliResult(0, inspectJson, ""));
            }

            return Task.FromResult(new CliResult(1, "", "not scripted"));
        }
    }
}
