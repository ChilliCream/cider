using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E — Apple <c>container</c> has no commit primitive, so <c>docker commit</c> and
/// <c>docker import</c> are emulated by exporting a root filesystem into a one-layer OCI image and
/// loading that back. The point of these tests is that the result is a *real,
/// runnable* image, not just an entry in <c>docker images</c>.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class CommitTests(DaemonFixture daemon)
{
    private const string BaseImage = "alpine:3.22";

    [E2EFact]
    public async Task Commit_of_a_stopped_container_produces_a_runnable_image_with_its_filesystem()
    {
        var name = DaemonFixture.NewName("grj-commit");
        var committed = $"ad-grj/committed-{Guid.NewGuid():n}"[..24] + ":1";

        var run = await daemon.DockerAsync(
            ["run", "--name", name, BaseImage, "sh", "-c", "echo x > /x"],
            timeout: TimeSpan.FromMinutes(6));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var commit = await daemon.DockerAsync(
                ["commit", "--change", "CMD [\"/bin/sh\"]", "-m", "grj", "-a", "cider", name, committed],
                timeout: TimeSpan.FromMinutes(5));
            Assert.True(commit.Ok, commit.ToString());
            Assert.Contains("sha256:", commit.Stdout, StringComparison.Ordinal);

            var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}:{{.Tag}}");
            Assert.True(images.Ok, images.ToString());
            Assert.Contains(committed, images.Stdout, StringComparison.Ordinal);

            // The whole point: the committed image runs, and carries the file the container wrote.
            var cat = await daemon.DockerAsync(["run", "--rm", committed, "cat", "/x"], timeout: TimeSpan.FromMinutes(5));
            Assert.True(cat.Ok, cat.ToString());
            Assert.Equal("x", cat.Stdout.Trim());

            var inspect = await daemon.DockerAsync("inspect", committed, "--format", "{{json .Config.Cmd}}");
            Assert.True(inspect.Ok, inspect.ToString());
            Assert.Contains("/bin/sh", inspect.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync("rmi", "-f", committed);
            await daemon.DockerAsync("rm", "-f", name);
        }
    }

    [E2EFact]
    public async Task Import_of_a_docker_export_tarball_produces_a_runnable_image()
    {
        var name = DaemonFixture.NewName("grj-import");
        var imported = $"ad-grj/imported-{Guid.NewGuid():n}"[..24] + ":1";

        var run = await daemon.DockerAsync(
            ["run", "--name", name, BaseImage, "sh", "-c", "echo y > /y"],
            timeout: TimeSpan.FromMinutes(6));
        Assert.True(run.Ok, run.ToString());

        try
        {
            // The tar is binary, so it goes through a shell pipe rather than the fixture's string stdin.
            var pipe = await Cmd.RunAsync(
                "/bin/sh",
                ["-c", $"set -e; docker export {name} | docker import --change 'CMD [\"/bin/sh\"]' - {imported}"],
                daemon.BuildEnvironment(null),
                stdin: null,
                timeout: TimeSpan.FromMinutes(6));
            Assert.True(pipe.Ok, pipe.ToString());
            Assert.Contains("sha256:", pipe.Stdout, StringComparison.Ordinal);

            var cat = await daemon.DockerAsync(["run", "--rm", imported, "cat", "/y"], timeout: TimeSpan.FromMinutes(5));
            Assert.True(cat.Ok, cat.ToString());
            Assert.Equal("y", cat.Stdout.Trim());
        }
        finally
        {
            await daemon.DockerAsync("rmi", "-f", imported);
            await daemon.DockerAsync("rm", "-f", name);
        }
    }
}
