using System.Net;
using Cider.Daemon.Hosting;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #6 — the classic builder works with <c>DOCKER_BUILDKIT=0</c>, BuildKit is refused.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class BuildTests(DaemonFixture daemon)
{
    private const string Tag = "e2e/built:1";
    private const string BuildkitTag = "e2e/buildkit:1";

    [E2EFact]
    public async Task Classic_builder_builds_tags_and_runs_an_image()
    {
        var context = await NewContextAsync("classic");

        var build = await daemon.DockerAsync(
            ["build", "-t", Tag, "."],
            timeout: TimeSpan.FromMinutes(6),
            extraEnvironment: new Dictionary<string, string?> { ["DOCKER_BUILDKIT"] = "0" },
            workingDirectory: context);
        Assert.True(build.Ok, build.ToString());

        var output = build.Stdout + build.Stderr;
        Assert.Contains("Successfully built", output, StringComparison.Ordinal);
        Assert.Contains("Successfully tagged", output, StringComparison.Ordinal);
        Assert.Contains(Tag, output, StringComparison.Ordinal);

        try
        {
            var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}:{{.Tag}}");
            Assert.True(images.Ok, images.ToString());
            Assert.Contains(Tag, images.Stdout, StringComparison.Ordinal);

            var run = await daemon.DockerAsync(["run", "--rm", Tag], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
            Assert.Equal("hello", run.Stdout.Trim());
        }
        finally
        {
            var remove = await daemon.DockerAsync(["rmi", "-f", Tag], timeout: TimeSpan.FromMinutes(2));
            Assert.True(remove.Ok, remove.ToString());
        }
    }

    [E2EFact]
    public async Task Untagged_build_is_dangling_and_prunable_with_no_synthetic_repo_name()
    {
        var context = await NewContextAsync("untagged");

        var build = await daemon.DockerAsync(
            ["build", "."],
            timeout: TimeSpan.FromMinutes(6),
            extraEnvironment: new Dictionary<string, string?> { ["DOCKER_BUILDKIT"] = "0" },
            workingDirectory: context);
        Assert.True(build.Ok, build.ToString());

        var buildOutput = build.Stdout + build.Stderr;
        Assert.Contains("Successfully built", buildOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Successfully tagged", buildOutput, StringComparison.Ordinal);

        // Every assertion below is about *this* image. Concurrent builds and leftovers make "some
        // dangling image exists, and some dangling image went away" pass without the fix, so the id
        // the build just printed is what is tracked.
        var builtId = BuiltImageId(buildOutput);

        // Real Docker never shows an untagged build's synthetic repo name; it renders <none>:<none>.
        var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}");
        Assert.True(images.Ok, images.ToString());
        Assert.DoesNotContain("cider-build", images.Stdout, StringComparison.Ordinal);

        var dangling = await daemon.DockerAsync("images", "--filter", "dangling=true", "-q");
        Assert.True(dangling.Ok, dangling.ToString());
        var danglingIds = ShortIds(dangling.Stdout);
        Assert.Contains(builtId, danglingIds);

        var prune = await daemon.DockerAsync("image", "prune", "-f");
        Assert.True(prune.Ok, prune.ToString());

        var afterPrune = await daemon.DockerAsync("images", "--filter", "dangling=true", "-q");
        Assert.True(afterPrune.Ok, afterPrune.ToString());
        Assert.DoesNotContain(builtId, ShortIds(afterPrune.Stdout));
    }

    /// <summary>The short id from the classic builder's <c>Successfully built &lt;id&gt;</c> line.</summary>
    private static string BuiltImageId(string buildOutput)
    {
        const string marker = "Successfully built ";
        var start = buildOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the build printed no \"Successfully built\" line: " + buildOutput);

        var id = buildOutput[(start + marker.Length)..].Split('\n', 2)[0].Trim();
        Assert.NotEmpty(id);
        return id;
    }

    /// <summary><c>docker images -q</c> ids, normalized to the short form the build line prints.</summary>
    private static IReadOnlyList<string> ShortIds(string output) =>
    [
        .. output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id)
            .Select(id => id.Length > 12 ? id[..12] : id),
    ];

    [E2EFact]
    public async Task Buildkit_is_refused_and_a_buildkit_build_never_succeeds()
    {
        // The contract: cider speaks no BuildKit, and says so where BuildKit clients look.
        using var client = DaemonClient.Create(daemon.Options.SocketPath, TimeSpan.FromSeconds(30));
        foreach (var path in new[] { "/session", "/grpc" })
        {
            using var response = await client.PostAsync(new Uri("/v1.47" + path, UriKind.Relative), new StringContent(""));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var ping = await daemon.DockerAsync("version", "--format", "{{.Server.APIVersion}}");
        Assert.True(ping.Ok, ping.ToString());

        // And a real `docker build` with BuildKit on never produces an image. (With buildx
        // installed the CLI does not fail fast: it stalls pulling moby/buildkit for its
        // docker-container driver, so the assertion is "did not succeed within the budget".)
        var context = await NewContextAsync("buildkit");
        var build = await daemon.DockerAsync(
            ["build", "-t", BuildkitTag, "."],
            timeout: TimeSpan.FromSeconds(60),
            extraEnvironment: new Dictionary<string, string?>
            {
                ["DOCKER_BUILDKIT"] = "1",
                ["BUILDX_BUILDER"] = null,
            },
            workingDirectory: context);

        Assert.False(build.Ok, "the daemon does not speak BuildKit, so this build must not succeed: " + build);

        var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}:{{.Tag}}");
        Assert.True(images.Ok, images.ToString());
        Assert.DoesNotContain(BuildkitTag, images.Stdout, StringComparison.Ordinal);
    }

    private async Task<string> NewContextAsync(string suffix)
    {
        var context = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName(suffix));
        Directory.CreateDirectory(context);
        await File.WriteAllTextAsync(
            Path.Combine(context, "Dockerfile"),
            "FROM alpine:3.22\nRUN echo hello > /hello\nCMD [\"cat\",\"/hello\"]\n");
        return context;
    }
}
