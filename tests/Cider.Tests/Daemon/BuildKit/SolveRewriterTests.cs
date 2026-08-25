using Cider.Core.Ids;
using Cider.Daemon.BuildKit;
using Grpc.Core;
using Moby.Buildkit.V1;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="SolveRewriter"/> in isolation — pure request mutation, no builder link, no session.
/// Exactly cider-ger.10's verification section.
/// </summary>
public sealed class SolveRewriterTests
{
    [Fact]
    public void Rewrite_turns_a_moby_exporter_into_docker_and_normalizes_tags()
    {
        var request = new SolveRequest
        {
            ExporterDeprecated = "moby",
        };
        request.ExporterAttrsDeprecated["name"] = "app:1,app";
        request.ExporterAttrsDeprecated["push"] = "true";
        request.Exporters.Add(new Exporter { Type = "moby" });
        request.Exporters[0].Attrs["name"] = "app:1,app";
        request.Exporters[0].Attrs["push"] = "true";

        var result = SolveRewriter.Rewrite(request);

        Assert.Equal(new HashSet<int> { 0 }, result.CaptureExporterIds);
        Assert.Single(result.Exporters);
        Assert.Equal(
            new List<string> { "docker.io/library/app:1", "docker.io/library/app:latest" },
            result.Exporters[0].Tags);
        Assert.Null(result.Exporters[0].SyntheticTag);

        Assert.Equal("docker", request.Exporters[0].Type);
        Assert.Equal("docker.io/library/app:1,docker.io/library/app:latest", request.Exporters[0].Attrs["name"]);
        Assert.Equal("true", request.Exporters[0].Attrs["tar"]);
        Assert.False(request.Exporters[0].Attrs.ContainsKey("push"));

        Assert.Equal("docker", request.ExporterDeprecated);
        Assert.Equal("docker.io/library/app:1,docker.io/library/app:latest", request.ExporterAttrsDeprecated["name"]);
        Assert.Equal("true", request.ExporterAttrsDeprecated["tar"]);
        Assert.False(request.ExporterAttrsDeprecated.ContainsKey("push"));
    }

    [Fact]
    public void Rewrite_drops_push_push_by_digest_unpack_and_buildinfo_attrs_but_keeps_other_attrs()
    {
        var request = new SolveRequest();
        var exporter = new Exporter { Type = "moby" };
        exporter.Attrs["name"] = "app";
        exporter.Attrs["push"] = "true";
        exporter.Attrs["push-by-digest"] = "true";
        exporter.Attrs["unpack"] = "true";
        exporter.Attrs["buildinfo-attrs"] = "true";
        exporter.Attrs["source-date-epoch"] = "123";
        exporter.Attrs["compression"] = "gzip";
        exporter.Attrs["oci-mediatypes"] = "true";
        exporter.Attrs["annotation.foo"] = "bar";
        exporter.Attrs["rewrite-timestamp"] = "true";
        request.Exporters.Add(exporter);

        SolveRewriter.Rewrite(request);

        var attrs = request.Exporters[0].Attrs;
        Assert.False(attrs.ContainsKey("push"));
        Assert.False(attrs.ContainsKey("push-by-digest"));
        Assert.False(attrs.ContainsKey("unpack"));
        Assert.False(attrs.ContainsKey("buildinfo-attrs"));
        Assert.Equal("123", attrs["source-date-epoch"]);
        Assert.Equal("gzip", attrs["compression"]);
        Assert.Equal("true", attrs["oci-mediatypes"]);
        Assert.Equal("bar", attrs["annotation.foo"]);
        Assert.Equal("true", attrs["rewrite-timestamp"]);
    }

    [Fact]
    public void Rewrite_finds_a_moby_exporter_at_a_later_index_and_leaves_earlier_ones_untouched()
    {
        var request = new SolveRequest();
        request.Exporters.Add(new Exporter { Type = "local" });
        request.Exporters[0].Attrs["dest"] = "/out";
        request.Exporters.Add(new Exporter { Type = "moby" });
        request.Exporters[1].Attrs["name"] = "app";

        var result = SolveRewriter.Rewrite(request);

        Assert.Equal(new HashSet<int> { 1 }, result.CaptureExporterIds);
        Assert.Equal("local", request.Exporters[0].Type);
        Assert.Equal("/out", request.Exporters[0].Attrs["dest"]);
        Assert.Equal("docker", request.Exporters[1].Type);
    }

    [Fact]
    public void Rewrite_mints_a_synthetic_tag_for_an_empty_name()
    {
        var request = new SolveRequest();
        var exporter = new Exporter { Type = "moby" };
        request.Exporters.Add(exporter);

        var result = SolveRewriter.Rewrite(request);

        Assert.Single(result.Exporters);
        Assert.Empty(result.Exporters[0].Tags);
        Assert.NotNull(result.Exporters[0].SyntheticTag);
        Assert.True(SyntheticBuildTag.IsSyntheticBuildTag(result.Exporters[0].SyntheticTag));

        // The synthetic tag is still applied to the exporter's own `name` attr so the loaded image
        // is dangling-visible, even though SolveRewriter never shows it back to a caller as a tag.
        Assert.Equal(result.Exporters[0].SyntheticTag, request.Exporters[0].Attrs["name"]);
    }

    [Fact]
    public void Rewrite_handles_the_deprecated_only_shape_when_Exporters_is_empty()
    {
        var request = new SolveRequest { ExporterDeprecated = "moby" };
        request.ExporterAttrsDeprecated["name"] = "app";

        var result = SolveRewriter.Rewrite(request);

        Assert.Equal(new HashSet<int> { 0 }, result.CaptureExporterIds);
        Assert.Equal("docker", request.ExporterDeprecated);
        Assert.Equal("docker.io/library/app:latest", request.ExporterAttrsDeprecated["name"]);
        Assert.Empty(request.Exporters);
    }

    [Fact]
    public void Rewrite_leaves_an_Internal_request_untouched()
    {
        var request = new SolveRequest { Internal = true, ExporterDeprecated = "moby" };
        request.ExporterAttrsDeprecated["name"] = "app";
        var exporter = new Exporter { Type = "moby" };
        exporter.Attrs["name"] = "app";
        request.Exporters.Add(exporter);

        var result = SolveRewriter.Rewrite(request);

        Assert.Empty(result.Exporters);
        Assert.Empty(result.CaptureExporterIds);
        Assert.Equal("moby", request.ExporterDeprecated);
        Assert.Equal("moby", request.Exporters[0].Type);
        Assert.Equal("app", request.Exporters[0].Attrs["name"]);
    }

    [Fact]
    public void Rewrite_ignores_a_non_moby_exporter()
    {
        var request = new SolveRequest();
        request.Exporters.Add(new Exporter { Type = "image" });
        request.Exporters[0].Attrs["name"] = "app";

        var result = SolveRewriter.Rewrite(request);

        Assert.Empty(result.Exporters);
        Assert.Equal("image", request.Exporters[0].Type);
        Assert.Equal("app", request.Exporters[0].Attrs["name"]);
    }

    [Fact]
    public void Rewrite_rejects_a_multi_platform_docker_export()
    {
        var request = new SolveRequest();
        request.FrontendAttrs["platform"] = "linux/amd64,linux/arm64";
        var exporter = new Exporter { Type = "moby" };
        exporter.Attrs["name"] = "app";
        request.Exporters.Add(exporter);

        var ex = Assert.Throws<RpcException>(() => SolveRewriter.Rewrite(request));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void Rewrite_allows_a_single_platform_alongside_a_docker_export()
    {
        var request = new SolveRequest();
        request.FrontendAttrs["platform"] = "linux/arm64";
        var exporter = new Exporter { Type = "moby" };
        exporter.Attrs["name"] = "app";
        request.Exporters.Add(exporter);

        var result = SolveRewriter.Rewrite(request);

        Assert.Single(result.Exporters);
    }
}
