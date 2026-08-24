using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Cli.Models;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>
/// `container image ls|inspect --format json` fixtures captured from 1.2.2
/// (docs/apple-container-notes.md §2): one row per reference, a variant per platform,
/// plus one attestation manifest per platform that must be filtered out.
/// </summary>
public class ImageParsingTests
{
    private const string AlpineJson = """
    [{
      "configuration": {
        "creationDate": "2026-06-22T19:19:15Z",
        "descriptor": {"digest":"sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce","mediaType":"application/vnd.oci.image.index.v1+json","size":9218},
        "name": "docker.io/library/alpine:3.22"
      },
      "id": "14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce",
      "variants": [
        {
          "config": {
            "architecture": "amd64",
            "config": {"Cmd":["/bin/sh"],"Env":["PATH=/usr/bin"],"WorkingDir":"/"},
            "created": "2026-06-22T19:20:21.712285437Z",
            "os": "linux",
            "rootfs": {"diff_ids":["sha256:6f09edfb3f6d7173733adc8eec8ea00626550dc6fc2dcf07d40e13f5c1e907c4"],"type":"layers"}
          },
          "digest": "sha256:7c8cb692ae09657cbc4a3f3cbd0e8d5a2690ba38386aaaf252dbb060bf5eb2e6",
          "platform": {"architecture":"amd64","os":"linux"},
          "size": 3789228
        },
        {
          "config": {"architecture":"unknown","config":{},"os":"unknown","rootfs":{"diff_ids":["sha256:28abb80ea16212538ed24954a3ecb9868d9debfd37b5e37517ee02ba79fa46ab"],"type":"layers"}},
          "digest": "sha256:8111a3899a205022c7374987b130da08b38283267d40ffcce4bc6aef4975c865",
          "platform": {"architecture":"unknown","os":"unknown"},
          "size": 86506
        },
        {
          "config": {
            "architecture": "arm64",
            "config": {
              "Cmd":["/bin/sh"],
              "Entrypoint":["/entry.sh"],
              "Env":["PATH=/usr/bin","LANG=C"],
              "ExposedPorts":{"80/tcp":{},"53/udp":{}},
              "Labels":{"maintainer":"alpine"},
              "StopSignal":"SIGQUIT",
              "User":"nobody",
              "Volumes":{"/var/lib/data":{}},
              "WorkingDir":"/srv",
              "Healthcheck":{"Test":["CMD-SHELL","true"],"Interval":30000000000,"Timeout":5000000000,"Retries":3,"StartPeriod":1000000000}
            },
            "created": "2026-06-22T19:21:00.5Z",
            "os": "linux",
            "variant": "v8",
            "author": "alpine maintainers",
            "rootfs": {"diff_ids":["sha256:aaaa","sha256:bbbb"],"type":"layers"}
          },
          "digest": "sha256:cccc",
          "platform": {"architecture":"arm64","os":"linux","variant":"v8"},
          "size": 3600000
        },
        {
          "config": {"architecture":"unknown","config":{},"os":"unknown"},
          "digest": "sha256:dddd",
          "platform": {"architecture":"unknown","os":"unknown"},
          "size": 86234
        }
      ]
    }]
    """;

    private static AppleImageJson Parse() =>
        ContainerCli.ParseJson<List<AppleImageJson>>(AlpineJson, "test")![0];

    [Fact]
    public void Attestation_variants_are_filtered_out()
    {
        var variants = RuntimeMapper.RealVariants(Parse());

        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.NotEqual("unknown", v.Platform!.Architecture));
    }

    [Fact]
    public void Summary_prefixes_the_id_sums_sizes_and_lists_platforms()
    {
        var image = RuntimeMapper.ToImage(Parse());

        Assert.Equal("sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce", image.Id);
        Assert.Equal(new[] { "docker.io/library/alpine:3.22" }, image.References);

        // Attestation sizes (86506 + 86234) are excluded.
        Assert.Equal(3789228 + 3600000, image.Size);
        Assert.Equal(new[] { "linux/amd64", "linux/arm64/v8" }, image.Platforms);
    }

    [Fact]
    public void Detail_picks_the_host_arm64_variant_and_maps_the_oci_config()
    {
        var detail = RuntimeMapper.ToImageDetail(Parse(), platform: null)!;

        Assert.Equal("arm64", detail.Architecture);
        Assert.Equal("linux", detail.Os);
        Assert.Equal("v8", detail.Variant);
        Assert.Equal("alpine maintainers", detail.Author);
        Assert.Equal(3600000, detail.Size);
        Assert.Equal(new[] { "sha256:aaaa", "sha256:bbbb" }, detail.Layers);
        Assert.Equal(
            new[] { "docker.io/library/alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce" },
            detail.RepoDigests);

        Assert.Equal(new[] { "/bin/sh" }, detail.Config.Cmd);
        Assert.Equal(new[] { "/entry.sh" }, detail.Config.Entrypoint);
        Assert.Equal(new[] { "PATH=/usr/bin", "LANG=C" }, detail.Config.Env);
        Assert.Equal("/srv", detail.Config.WorkingDir);
        Assert.Equal("nobody", detail.Config.User);
        Assert.Equal("SIGQUIT", detail.Config.StopSignal);
        Assert.Contains("80/tcp", detail.Config.ExposedPorts);
        Assert.Contains("53/udp", detail.Config.ExposedPorts);
        Assert.Equal(new[] { "/var/lib/data" }, detail.Config.Volumes);
        Assert.Equal("alpine", detail.Config.Labels["maintainer"]);

        var health = detail.Config.Healthcheck!;
        Assert.Equal(new[] { "CMD-SHELL", "true" }, health.Test);
        Assert.Equal(30000000000, health.Interval);
        Assert.Equal(3, health.Retries);
    }

    [Fact]
    public void Detail_honours_an_explicit_platform()
    {
        var detail = RuntimeMapper.ToImageDetail(Parse(), "linux/amd64")!;

        Assert.Equal("amd64", detail.Architecture);
        Assert.Equal(new[] { "/bin/sh" }, detail.Config.Cmd);
        Assert.Empty(detail.Config.Entrypoint);
    }

    [Fact]
    public void Missing_config_sections_produce_empty_collections_not_nulls()
    {
        const string json = """
        [{"configuration":{"name":"docker.io/library/x:1"},"id":"aa","variants":[{"config":{"architecture":"arm64","os":"linux","config":{}},"platform":{"architecture":"arm64","os":"linux"},"size":1}]}]
        """;

        var image = ContainerCli.ParseJson<List<AppleImageJson>>(json, "test")![0];
        var detail = RuntimeMapper.ToImageDetail(image, null)!;

        Assert.Empty(detail.Config.Cmd);
        Assert.Empty(detail.Config.Entrypoint);
        Assert.Empty(detail.Config.Env);
        Assert.Empty(detail.Config.ExposedPorts);
        Assert.Empty(detail.Config.Labels);
        Assert.Null(detail.Config.Healthcheck);
        Assert.Empty(detail.Layers);
    }

    /// <summary>
    /// `container image tag alpine:3.22 adtest/alpine:x` makes `image ls` print two top-level rows
    /// with the same id (docs/apple-container-notes.md §2) — Docker's shape is one image per digest.
    /// </summary>
    [Fact]
    public void Rows_sharing_an_id_merge_into_one_image_carrying_every_reference()
    {
        var alias = AlpineJson.Replace(
            "\"name\": \"docker.io/library/alpine:3.22\"",
            "\"name\": \"docker.io/adtest/alpine:x\"",
            StringComparison.Ordinal);
        const string other = """
        [{"configuration":{"name":"docker.io/library/busybox:1"},"id":"bb","variants":[{"config":{"architecture":"arm64","os":"linux","config":{}},"platform":{"architecture":"arm64","os":"linux"},"size":7}]}]
        """;

        var rows = new List<AppleImageJson>
        {
            Parse(),
            ContainerCli.ParseJson<List<AppleImageJson>>(other, "test")![0],
            ContainerCli.ParseJson<List<AppleImageJson>>(alias, "test")![0],
        };

        var images = RuntimeMapper.ToImages(rows);

        Assert.Equal(2, images.Count);
        var alpine = images[0];
        Assert.Equal("sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce", alpine.Id);
        Assert.Equal(new[] { "docker.io/library/alpine:3.22", "docker.io/adtest/alpine:x" }, alpine.References);

        // The merged image keeps the first row's facts — the size is not counted once per tag.
        Assert.Equal(3789228 + 3600000, alpine.Size);
        Assert.Equal(new[] { "linux/amd64", "linux/arm64/v8" }, alpine.Platforms);
        Assert.Equal(new[] { "docker.io/library/busybox:1" }, images[1].References);
    }

    [Fact]
    public void Rows_without_an_id_are_never_merged_together()
    {
        const string json = """
        [{"configuration":{"name":"docker.io/library/x:1"}},{"configuration":{"name":"docker.io/library/y:1"}}]
        """;

        var images = RuntimeMapper.ToImages(ContainerCli.ParseJson<List<AppleImageJson>>(json, "test")!);

        Assert.Equal(2, images.Count);
    }

    [Fact]
    public void Bare_hex_ids_get_the_sha256_prefix_and_prefixed_ids_are_left_alone()
    {
        Assert.Equal("sha256:abc", RuntimeMapper.ToImageId("abc"));
        Assert.Equal("sha256:abc", RuntimeMapper.ToImageId("sha256:abc"));
        Assert.Equal("", RuntimeMapper.ToImageId(null));
    }
}
