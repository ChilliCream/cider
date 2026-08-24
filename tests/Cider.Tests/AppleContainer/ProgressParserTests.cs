using Cider.AppleContainer.Cli;
using Xunit;

namespace Cider.Tests.AppleContainer;

/// <summary>Pull and build output lines captured from 1.2.2 (notes §2 and §10).</summary>
public class ProgressParserTests
{
    [Fact]
    public void Pull_phase_lines_carry_the_step_as_id()
    {
        var evt = ProgressParser.ParsePullLine("[1/2] Fetching image [0s]")!;

        Assert.Equal("1/2", evt.Id);
        Assert.Equal("Fetching image", evt.Status);
        Assert.Null(evt.Current);
        Assert.Null(evt.Total);
    }

    [Fact]
    public void Pull_blob_counters_become_current_and_total()
    {
        var evt = ProgressParser.ParsePullLine("[1/2] Fetching image 12% (20 of 56 blobs, 3.6/28.3 MB, 4 KB/s) [10s]")!;

        Assert.Equal("1/2", evt.Id);
        Assert.Equal(20, evt.Current);
        Assert.Equal(56, evt.Total);
        Assert.Equal("Fetching image 12% (20 of 56 blobs, 3.6/28.3 MB, 4 KB/s)", evt.Status);
    }

    [Fact]
    public void Pull_unpacking_lines_keep_the_platform_detail()
    {
        var evt = ProgressParser.ParsePullLine("[2/2] Unpacking image for platform linux/arm64/v8 0% [24s]")!;

        Assert.Equal("2/2", evt.Id);
        Assert.Equal("Unpacking image for platform linux/arm64/v8 0%", evt.Status);
    }

    [Fact]
    public void Unstructured_lines_pass_through_as_status()
    {
        var evt = ProgressParser.ParsePullLine("some other message")!;

        Assert.Equal("some other message", evt.Status);
        Assert.Null(evt.Id);
    }

    [Fact]
    public void Blank_lines_are_dropped() => Assert.Null(ProgressParser.ParsePullLine("   "));

    [Fact]
    public void Build_image_id_comes_from_the_exporting_manifest_list_line()
    {
        const string line = "#6 exporting manifest list sha256:611305aa6efdbc4c0bbd5a5e0451b715cf7fd0e342b635a2ed1ed3758e0eb3b5 done";

        Assert.Equal(
            "sha256:611305aa6efdbc4c0bbd5a5e0451b715cf7fd0e342b635a2ed1ed3758e0eb3b5",
            ProgressParser.ParseBuiltImageId(line));
    }

    [Fact]
    public void Build_falls_back_to_the_plain_manifest_line()
    {
        const string line = "#6 exporting manifest sha256:fb0ab44fae445d65cea5a5b442677355627de1025b6a33d19544cc84b8a75143 done";

        Assert.Equal(
            "sha256:fb0ab44fae445d65cea5a5b442677355627de1025b6a33d19544cc84b8a75143",
            ProgressParser.ParseBuiltImageId(line));
    }

    [Theory]
    [InlineData("#5 [linux/arm64 1/2] RUN echo hello > /hello")]
    [InlineData("#5 DONE 0.0s")]
    [InlineData("#6 exporting config sha256:00c3135d03704f0b574c7f8fdd4881c0b39e0955ccc26c77e4e8cc0d505b8952 done")]
    public void Ordinary_build_lines_carry_no_image_id(string line) =>
        Assert.Null(ProgressParser.ParseBuiltImageId(line));
}
