using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #4 — named volumes, bind mounts, <c>docker cp</c> both ways, anonymous volumes.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class VolumeTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task Named_volume_survives_between_two_containers_and_is_removable()
    {
        var volume = DaemonFixture.NewName("vol");
        var create = await daemon.DockerAsync("volume", "create", volume);
        Assert.True(create.Ok, create.ToString());
        Assert.Equal(volume, create.Stdout.Trim());

        try
        {
            var list = await daemon.DockerAsync("volume", "ls", "--format", "{{.Name}}");
            Assert.True(list.Ok, list.ToString());
            Assert.Contains(volume, list.Stdout, StringComparison.Ordinal);

            var write = await daemon.DockerAsync(
                ["run", "--rm", "-v", $"{volume}:/data", Image, "sh", "-c", "echo 1 > /data/f"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(write.Ok, write.ToString());

            var read = await daemon.DockerAsync(
                ["run", "--rm", "-v", $"{volume}:/data", Image, "cat", "/data/f"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(read.Ok, read.ToString());
            Assert.Equal("1", read.Stdout.Trim());

            var inspect = await daemon.DockerAsync("volume", "inspect", "-f", "{{.Driver}}|{{.Scope}}", volume);
            Assert.True(inspect.Ok, inspect.ToString());
            Assert.Equal("local|local", inspect.Stdout.Trim());
        }
        finally
        {
            var remove = await daemon.DockerAsync(["volume", "rm", volume], timeout: TimeSpan.FromMinutes(2));
            Assert.True(remove.Ok, remove.ToString());
        }

        var gone = await daemon.DockerAsync("volume", "inspect", volume);
        Assert.False(gone.Ok, gone.ToString());
    }

    [E2EFact]
    public async Task Bind_mount_of_a_host_directory_is_visible_inside_the_container()
    {
        var directory = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("bind"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "host.txt"), "from-host\n");

        var read = await daemon.DockerAsync(
            ["run", "--rm", "-v", $"{directory}:/host:ro", Image, "cat", "/host/host.txt"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(read.Ok, read.ToString());
        Assert.Equal("from-host", read.Stdout.Trim());

        var write = await daemon.DockerAsync(
            ["run", "--rm", "-v", $"{directory}:/host", Image, "sh", "-c", "echo from-guest > /host/guest.txt"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(write.Ok, write.ToString());
        Assert.Equal("from-guest", (await File.ReadAllTextAsync(Path.Combine(directory, "guest.txt"))).Trim());
    }

    [E2EFact]
    public async Task Docker_cp_moves_files_in_both_directions()
    {
        var name = DaemonFixture.NewName("cp");
        var run = await daemon.DockerAsync(["run", "-d", "--name", name, Image, "sleep", "180"], timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        var workspace = Path.Combine(daemon.ScratchDir, name);
        Directory.CreateDirectory(workspace);

        try
        {
            // ---- container → host ----
            var down = await daemon.DockerAsync(
                ["cp", $"{name}:/etc/hostname", "."],
                timeout: TimeSpan.FromMinutes(2),
                workingDirectory: workspace);
            Assert.True(down.Ok, down.ToString());
            var pulled = Path.Combine(workspace, "hostname");
            Assert.True(File.Exists(pulled), "docker cp did not create ./hostname: " + down);
            Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(pulled)));

            // ---- host → container ----
            var pushed = Path.Combine(workspace, "payload.txt");
            await File.WriteAllTextAsync(pushed, "payload\n");
            var up = await daemon.DockerAsync(["cp", pushed, $"{name}:/tmp/"], timeout: TimeSpan.FromMinutes(2));
            Assert.True(up.Ok, up.ToString());

            var check = await daemon.DockerAsync(["exec", name, "cat", "/tmp/payload.txt"], timeout: TimeSpan.FromMinutes(2));
            Assert.True(check.Ok, check.ToString());
            Assert.Equal("payload", check.Stdout.Trim());
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Docker_cp_retrieves_an_artefact_from_an_exited_container()
    {
        var name = DaemonFixture.NewName("cpout");
        var token = "artefact-" + Guid.NewGuid().ToString("n")[..8];
        var run = await daemon.DockerAsync(
            ["run", "--name", name, Image, "sh", "-c", $"echo {token} > /out.txt"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        var workspace = Path.Combine(daemon.ScratchDir, name);
        Directory.CreateDirectory(workspace);

        try
        {
            var status = await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}", name);
            Assert.True(status.Ok, status.ToString());
            Assert.Equal("exited", status.Stdout.Trim());

            // Apple `container cp` refuses a container that is not running, so this used to 409 —
            // while retrieving build output from an exited container is ordinary Docker.
            // It is served from the container's own rootfs export.
            var down = await daemon.DockerAsync(
                ["cp", $"{name}:/out.txt", "."],
                timeout: TimeSpan.FromMinutes(2),
                workingDirectory: workspace);
            Assert.True(down.Ok, down.ToString());
            Assert.Equal(token, (await File.ReadAllTextAsync(Path.Combine(workspace, "out.txt"))).Trim());

            // Nothing about the stopped container may change on the way: no restart, same exit code.
            var after = await daemon.DockerAsync("inspect", "-f", "{{.State.Status}}:{{.State.ExitCode}}:{{.State.Running}}", name);
            Assert.True(after.Ok, after.ToString());
            Assert.Equal("exited:0:false", after.Stdout.Trim());

            // A path that is not in the container is still an error, not an empty file.
            var missing = await daemon.DockerAsync(
                ["cp", $"{name}:/definitely-not-here.txt", "."],
                timeout: TimeSpan.FromMinutes(2),
                workingDirectory: workspace);
            Assert.False(missing.Ok, missing.ToString());
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Anonymous_volume_is_removed_with_rm_dash_v()
    {
        var name = DaemonFixture.NewName("anon");
        var before = await VolumeNamesAsync();

        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, "-v", "/data", Image, "sleep", "120"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        var created = (await VolumeNamesAsync()).Except(before, StringComparer.Ordinal).ToList();
        try
        {
            Assert.True(created.Count == 1, "expected exactly one anonymous volume, got: " + string.Join(", ", created));

            var mounts = await daemon.DockerAsync("inspect", "-f", "{{range .Mounts}}{{.Type}}:{{.Destination}} {{end}}", name);
            Assert.True(mounts.Ok, mounts.ToString());
            Assert.Contains("volume:/data", mounts.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            var remove = await daemon.DockerAsync(["rm", "-f", "-v", name], timeout: TimeSpan.FromMinutes(2));
            Assert.True(remove.Ok, remove.ToString());
        }

        var after = await VolumeNamesAsync();
        Assert.DoesNotContain(created[0], after);
    }

    private async Task<string[]> VolumeNamesAsync()
    {
        var list = await daemon.DockerAsync("volume", "ls", "--format", "{{.Name}}");
        Assert.True(list.Ok, list.ToString());
        return list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
