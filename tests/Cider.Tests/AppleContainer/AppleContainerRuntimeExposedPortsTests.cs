using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// `container image inspect` on 1.2.2 silently drops the OCI config's empty-object-valued
/// dictionaries — <c>ExposedPorts</c> and <c>Volumes</c> — even when the image genuinely declares
/// them (probe: docker.io/library/postgres:18.3's real config carries <c>{"5432/tcp":{}}</c>, but
/// every variant of its <c>image inspect</c> output omits the key; <c>Cmd</c>/<c>Entrypoint</c>/
/// <c>Env</c>/<c>StopSignal</c> of the same object survive). <see cref="AppleContainerRuntime"/>
/// recovers the true config from Apple's local content-addressed blob store
/// (AppRoot/content/blobs/sha256/&lt;digest&gt;) instead of trusting the CLI's truncated echo.
/// </summary>
public sealed class AppleContainerRuntimeExposedPortsTests : IDisposable
{
    private const string Reference = "docker.io/library/postgres:18.3";
    private const string ManifestDigestHex = "a145910d7079e9fbf73e6df19d5fcca0ce59d747cf7d97ac772bff28c3759c32";
    private const string ConfigDigestHex = "fbaa243599038521bbda8f6fa286d2a8fc1236509606f22de81d0739b0610ba7";

    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "cider-w40-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_appRoot))
        {
            Directory.Delete(_appRoot, recursive: true);
        }
    }

    /// <summary>The CLI's own `image inspect` reply: a real variant whose `config.config` is
    /// truncated exactly like 1.2.2's — no `ExposedPorts`, no `Volumes`.</summary>
    private static string InspectJson(string manifestDigestHex) => $$"""
    [{
      "configuration": {
        "name": "docker.io/library/postgres:18.3",
        "descriptor": {"digest":"sha256:index","mediaType":"application/vnd.oci.image.index.v1+json","size":1},
        "creationDate": "2026-05-08T19:33:00Z"
      },
      "id": "index",
      "variants": [
        {
          "config": {
            "architecture": "amd64",
            "os": "linux",
            "created": "2026-05-08T19:33:00.122558993Z",
            "config": {"Cmd":["postgres"],"Entrypoint":["docker-entrypoint.sh"],"Env":["PG_MAJOR=18"],"StopSignal":"SIGINT"}
          },
          "digest": "sha256:{{manifestDigestHex}}",
          "platform": {"architecture":"amd64","os":"linux"},
          "size": 12345
        }
      ]
    }]
    """;

    private static string StatusJson(string appRoot) =>
        $$"""{"status":"running","appRoot":"{{appRoot.Replace("\\", "\\\\")}}","installRoot":"/usr/local/"}""";

    // `layers[].size` is what `docker history` (cider-ede.20) needs: `container image inspect`
    // reports only one total size per platform variant, never a per-layer breakdown, but the real
    // manifest blob in Apple's local content store carries it (docs/spikes/xpc/03-limitations-audit-1.3.md
    // history row) — recovered from disk the same way the config blob below is.
    private static readonly string ManifestBlob =
        "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"," +
        "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\"," +
        "\"digest\":\"sha256:" + ConfigDigestHex + "\",\"size\":10036}," +
        "\"layers\":[" +
        "{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"sha256:layer1\",\"size\":111111}," +
        "{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"sha256:layer2\",\"size\":222222}" +
        "]}";

    private const string ConfigBlob = """
    {
      "architecture": "amd64",
      "os": "linux",
      "created": "2026-05-08T19:33:00.122558993Z",
      "config": {
        "ExposedPorts": {"5432/tcp": {}},
        "Volumes": {"/var/lib/postgresql": {}},
        "Env": ["PG_MAJOR=18"],
        "Entrypoint": ["docker-entrypoint.sh"],
        "Cmd": ["postgres"],
        "StopSignal": "SIGINT"
      }
    }
    """;

    private void SeedLocalBlobStore()
    {
        var dir = Path.Combine(_appRoot, "content", "blobs", "sha256");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManifestDigestHex), ManifestBlob);
        File.WriteAllText(Path.Combine(dir, ConfigDigestHex), ConfigBlob);
    }

    [Fact]
    public async Task InspectImageAsync_RecoversExposedPortsAndVolumes_FromTheLocalBlobStore()
    {
        SeedLocalBlobStore();
        var cli = new ScriptedCli(StatusJson(_appRoot), InspectJson(ManifestDigestHex));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains("5432/tcp", detail!.Config.ExposedPorts);
        Assert.Contains("/var/lib/postgresql", detail.Config.Volumes);

        // The CLI's own (truncated) fields still come through untouched.
        Assert.Equal(new[] { "postgres" }, detail.Config.Cmd);
        Assert.Equal("SIGINT", detail.Config.StopSignal);
    }

    [Fact]
    public async Task InspectImageAsync_RecoversPerLayerSizes_FromTheLocalManifestBlob()
    {
        SeedLocalBlobStore();
        var cli = new ScriptedCli(StatusJson(_appRoot), InspectJson(ManifestDigestHex));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(new long[] { 111111, 222222 }, detail!.LayerSizes);
    }

    [Fact]
    public async Task InspectImageAsync_LeavesLayerSizesEmpty_WhenTheLocalBlobStoreHasNothingToRecover()
    {
        // No SeedLocalBlobStore(): AppRoot resolves, but the manifest blob does not exist on disk.
        var cli = new ScriptedCli(StatusJson(_appRoot), InspectJson(ManifestDigestHex));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail!.LayerSizes);
    }

    [Fact]
    public async Task InspectImageAsync_LeavesExposedPortsEmpty_WhenTheLocalBlobStoreHasNothingToRecover()
    {
        // No SeedLocalBlobStore(): AppRoot resolves, but neither blob exists on disk.
        var cli = new ScriptedCli(StatusJson(_appRoot), InspectJson(ManifestDigestHex));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail!.Config.ExposedPorts);
        Assert.Empty(detail.Config.Volumes);
    }

    [Fact]
    public async Task InspectImageAsync_LeavesExposedPortsEmpty_WhenSystemStatusIsUnavailable()
    {
        SeedLocalBlobStore();
        var cli = new ScriptedCli(statusJson: null, InspectJson(ManifestDigestHex));
        var runtime = new AppleContainerRuntime(new AppleContainerOptions(), NullLogger<AppleContainerRuntime>.Instance, cli);

        var detail = await runtime.InspectImageAsync(Reference, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail!.Config.ExposedPorts);
    }

    /// <summary>Answers `system status`/`image inspect` from canned strings; anything else (e.g.
    /// `image ls` for sibling references) fails like an unscripted call, exercised deliberately.</summary>
    private sealed class ScriptedCli(string? statusJson, string inspectJson)
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
}
