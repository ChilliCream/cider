using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E — Apple lists one <c>image ls</c> row per reference; Docker has one image per digest with
/// every tag in <c>RepoTags</c>, and <c>rmi &lt;tag&gt;</c> of a multi-tag image only untags.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class ImageTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";
    private const string Alias = "ad-4rs/alias:1";

    [E2EFact]
    public async Task Second_tag_joins_the_same_image_and_rmi_of_one_tag_only_untags()
    {
        var pull = await daemon.DockerAsync(["pull", Image], timeout: TimeSpan.FromMinutes(6));
        Assert.True(pull.Ok, pull.ToString());

        var tag = await daemon.DockerAsync("tag", Image, Alias);
        Assert.True(tag.Ok, tag.ToString());

        try
        {
            // Both references answer with the full tag list, whichever one is inspected.
            foreach (var reference in new[] { Image, Alias })
            {
                var inspect = await daemon.DockerAsync("inspect", reference, "--format", "{{json .RepoTags}}");
                Assert.True(inspect.Ok, inspect.ToString());
                Assert.Contains(Image, inspect.Stdout, StringComparison.Ordinal);
                Assert.Contains(Alias, inspect.Stdout, StringComparison.Ordinal);
            }

            // The docker CLI expands RepoTags into one row per tag; both rows are the same image.
            var images = await daemon.DockerAsync("images", "--format", "{{.ID}}\t{{.Repository}}:{{.Tag}}");
            Assert.True(images.Ok, images.ToString());
            var rows = images.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2 && (parts[1] == Image || parts[1] == Alias))
                .ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(rows[0][0], rows[1][0]);

            var remove = await daemon.DockerAsync("rmi", Alias);
            Assert.True(remove.Ok, remove.ToString());
            var output = remove.Stdout + remove.Stderr;
            Assert.Contains($"Untagged: {Alias}", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Deleted:", output, StringComparison.Ordinal);

            // The image is still there under its remaining tag — and still runnable.
            var after = await daemon.DockerAsync("inspect", Image, "--format", "{{json .RepoTags}}");
            Assert.True(after.Ok, after.ToString());
            Assert.Contains(Image, after.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(Alias, after.Stdout, StringComparison.Ordinal);

            var run = await daemon.DockerAsync(["run", "--rm", Image, "true"], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
        }
        finally
        {
            // Only ever the alias this test created; alpine:3.22 is shared with the other suites.
            await daemon.DockerAsync("rmi", Alias);
        }
    }
}
