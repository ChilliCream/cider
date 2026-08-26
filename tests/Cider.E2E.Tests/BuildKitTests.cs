using System.Security.Cryptography;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E coverage for real BuildKit builds through the *default* builder — buildx's <c>docker</c>
/// driver, talking to cider's own <c>/grpc</c> + <c>/session</c> (cider-ger.5-.11); no
/// <c>docker buildx create</c> anywhere in this file. See cider-ger.12.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class BuildKitTests(DaemonFixture daemon)
{
    private const string Alpine = "FROM alpine:3.22\n";

    /// <summary>Explicit even though modern <c>docker</c> defaults to BuildKit, so intent reads at every call site.</summary>
    private static readonly IReadOnlyDictionary<string, string?> BuildKitEnv =
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["DOCKER_BUILDKIT"] = "1" };

    [E2EFact]
    public async Task Default_builder_builds_tags_and_runs_an_image()
    {
        var tag = UniqueTag("bk-basic");
        var context = await NewContextAsync("bk-basic", Alpine + "RUN echo hello > /hello\nCMD [\"cat\",\"/hello\"]\n");

        var build = await BuildAsync(["build", "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            var images = await daemon.DockerAsync("images", tag);
            Assert.True(images.Ok, images.ToString());
            Assert.Contains(tag, images.Stdout, StringComparison.Ordinal);

            var run = await daemon.DockerAsync(["run", "--rm", tag], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
            Assert.Equal("hello", run.Stdout.Trim());
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Build_arg_and_target_select_the_right_stage()
    {
        const string dockerfile = """
            FROM alpine:3.22 AS base
            ARG GREETING=unset
            RUN echo "$GREETING" > /greeting

            FROM alpine:3.22 AS unreachable
            RUN false

            FROM base AS final
            CMD ["cat", "/greeting"]
            """;
        var tag = UniqueTag("bk-target");
        var context = await NewContextAsync("bk-target", dockerfile);

        // `unreachable` is not an ancestor of `final`, so selecting --target final must never build
        // it — if it did, this build would fail on the `RUN false`.
        var build = await BuildAsync(
            ["build", "--build-arg", "GREETING=hello-target", "--target", "final", "-t", tag, "."],
            context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            var run = await daemon.DockerAsync(["run", "--rm", tag], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
            Assert.Equal("hello-target", run.Stdout.Trim());
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Secret_mount_exposes_the_secrets_contents_to_the_step()
    {
        const string dockerfile = """
            # syntax=docker/dockerfile:1
            FROM alpine:3.22
            RUN --mount=type=secret,id=tok cat /run/secrets/tok > /secret-out
            CMD ["cat", "/secret-out"]
            """;
        var tag = UniqueTag("bk-secret");
        var context = await NewContextAsync("bk-secret", dockerfile);
        var secretFile = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("secret") + ".txt");
        await File.WriteAllTextAsync(secretFile, "s3cr3t-value");

        var build = await BuildAsync(["build", "--secret", "id=tok,src=" + secretFile, "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            var run = await daemon.DockerAsync(["run", "--rm", tag], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
            Assert.Equal("s3cr3t-value", run.Stdout.Trim());
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Cache_mount_and_heredoc_run_both_succeed()
    {
        const string dockerfile = """
            # syntax=docker/dockerfile:1
            FROM alpine:3.22
            RUN --mount=type=cache,target=/cache echo cache-note > /cache/note && cp /cache/note /from-cache
            RUN <<EOF
            echo heredoc-hello > /heredoc
            EOF
            CMD ["sh", "-c", "cat /from-cache; cat /heredoc"]
            """;
        var tag = UniqueTag("bk-cache-heredoc");
        var context = await NewContextAsync("bk-cache-heredoc", dockerfile);

        var build = await BuildAsync(["build", "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            var run = await daemon.DockerAsync(["run", "--rm", tag], timeout: TimeSpan.FromMinutes(4));
            Assert.True(run.Ok, run.ToString());
            Assert.Contains("cache-note", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("heredoc-hello", run.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Progress_plain_reports_the_internal_build_definition_step()
    {
        var tag = UniqueTag("bk-progress");
        var context = await NewContextAsync("bk-progress", Alpine + "RUN echo hi > /hi\n");

        var build = await BuildAsync(["build", "--progress", "plain", "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());
        try
        {
            Assert.Contains("#1 [internal] load build definition", build.Stdout + build.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Iidfile_matches_the_tagged_images_id()
    {
        var tag = UniqueTag("bk-iidfile");
        var context = await NewContextAsync("bk-iidfile", Alpine + "RUN echo hi > /hi\n");
        var iidFile = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("iid") + ".txt");

        var build = await BuildAsync(["build", "--iidfile", iidFile, "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            Assert.True(File.Exists(iidFile), "docker build did not write --iidfile " + iidFile);
            var iid = NormalizeId((await File.ReadAllTextAsync(iidFile)).Trim());
            Assert.NotEmpty(iid);

            var images = await daemon.DockerAsync("images", "--no-trunc", "-q", tag);
            Assert.True(images.Ok, images.ToString());
            var imagesId = NormalizeId(images.Stdout.Trim());

            Assert.Equal(imagesId, iid);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Quiet_flag_prints_the_built_images_id()
    {
        var tag = UniqueTag("bk-quiet");
        var context = await NewContextAsync("bk-quiet", Alpine + "RUN echo hi > /hi\n");

        var build = await BuildAsync(["build", "-q", "-t", tag, "."], context);
        Assert.True(build.Ok, build.ToString());

        try
        {
            var quietId = NormalizeId(build.Stdout.Trim());
            Assert.NotEmpty(quietId);

            var images = await daemon.DockerAsync("images", "--no-trunc", "-q", tag);
            Assert.True(images.Ok, images.ToString());
            Assert.Equal(NormalizeId(images.Stdout.Trim()), quietId);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Untagged_build_is_dangling_and_prunable()
    {
        var context = await NewContextAsync("bk-untagged", Alpine + "RUN echo hi > /hi\n");

        // `-q` (rather than parsing progress output for a "Successfully built" line, which
        // BuildKit's own progress renderer does not print) hands back exactly the id this build
        // produced, so the assertions below are about *this* image and not some other dangling
        // leftover.
        var build = await BuildAsync(["build", "-q", "."], context);
        Assert.True(build.Ok, build.ToString());
        var builtId = ShortId(NormalizeId(build.Stdout.Trim()));
        Assert.NotEmpty(builtId);

        var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}");
        Assert.True(images.Ok, images.ToString());
        Assert.DoesNotContain("cider-build", images.Stdout, StringComparison.Ordinal);

        var dangling = await daemon.DockerAsync("images", "--filter", "dangling=true", "-q");
        Assert.True(dangling.Ok, dangling.ToString());
        Assert.Contains(builtId, ShortIds(dangling.Stdout));

        var prune = await daemon.DockerAsync("image", "prune", "-f");
        Assert.True(prune.Ok, prune.ToString());

        var afterPrune = await daemon.DockerAsync("images", "--filter", "dangling=true", "-q");
        Assert.True(afterPrune.Ok, afterPrune.ToString());
        Assert.DoesNotContain(builtId, ShortIds(afterPrune.Stdout));
    }

    [E2EFact]
    public async Task Two_concurrent_builds_with_different_tags_both_succeed()
    {
        var tagA = UniqueTag("bk-concurrent-a");
        var tagB = UniqueTag("bk-concurrent-b");
        var contextA = await NewContextAsync("bk-concurrent-a", Alpine + "RUN echo a > /marker\n");
        var contextB = await NewContextAsync("bk-concurrent-b", Alpine + "RUN echo b > /marker\n");

        var buildA = BuildAsync(["build", "-t", tagA, "."], contextA);
        var buildB = BuildAsync(["build", "-t", tagB, "."], contextB);
        await Task.WhenAll(buildA, buildB);

        Assert.True(buildA.Result.Ok, buildA.Result.ToString());
        Assert.True(buildB.Result.Ok, buildB.Result.ToString());

        try
        {
            var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}:{{.Tag}}");
            Assert.True(images.Ok, images.ToString());
            Assert.Contains(tagA, images.Stdout, StringComparison.Ordinal);
            Assert.Contains(tagB, images.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tagA, tagB], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task No_cache_build_still_succeeds()
    {
        var tag = UniqueTag("bk-no-cache");
        var context = await NewContextAsync("bk-no-cache", Alpine + "RUN echo hi > /hi\n");

        var first = await BuildAsync(["build", "-t", tag, "."], context);
        Assert.True(first.Ok, first.ToString());

        var second = await BuildAsync(["build", "--no-cache", "-t", tag, "."], context);
        Assert.True(second.Ok, second.ToString());

        await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
    }

    [E2EFact]
    public async Task Local_output_writes_the_final_stages_files_to_a_directory()
    {
        const string dockerfile = """
            FROM alpine:3.22 AS build
            RUN echo hello-local > /hello

            FROM scratch
            COPY --from=build /hello /hello
            """;
        var context = await NewContextAsync("bk-output-local", dockerfile);
        var dest = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("local-out"));
        Directory.CreateDirectory(dest);

        var build = await BuildAsync(["build", "--output", "type=local,dest=" + dest, "."], context);
        Assert.True(build.Ok, build.ToString());

        var file = Path.Combine(dest, "hello");
        Assert.True(File.Exists(file), "expected " + file + " from the local exporter; dir has: " +
            string.Join(", ", Directory.Exists(dest) ? Directory.GetFileSystemEntries(dest) : []));
        Assert.Equal("hello-local", (await File.ReadAllTextAsync(file)).Trim());
    }

    [E2EFact]
    public async Task Tar_output_writes_a_tar_archive()
    {
        const string dockerfile = """
            FROM alpine:3.22 AS build
            RUN echo hello-tar > /hello

            FROM scratch
            COPY --from=build /hello /hello
            """;
        var context = await NewContextAsync("bk-output-tar", dockerfile);
        var tarPath = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("tar-out") + ".tar");

        var build = await BuildAsync(["build", "--output", "type=tar,dest=" + tarPath, "."], context);
        Assert.True(build.Ok, build.ToString());

        Assert.True(File.Exists(tarPath), "expected a tar file at " + tarPath);
        Assert.True(new FileInfo(tarPath).Length > 0, "the tar output was empty");

        var listing = await Cmd.RunAsync("tar", ["-tf", tarPath], timeout: TimeSpan.FromSeconds(30));
        Assert.True(listing.Ok, listing.ToString());
        Assert.Contains("hello", listing.Stdout, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task Buildx_inspect_default_reports_running_on_the_apple_platform()
    {
        var inspect = await daemon.DockerAsync(["buildx", "inspect", "default", "--bootstrap"], timeout: TimeSpan.FromMinutes(2));
        Assert.True(inspect.Ok, inspect.ToString());

        var output = inspect.Stdout + inspect.Stderr;
        Assert.Matches(@"Status:\s+running", output);
        Assert.Contains("linux/arm64", output, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task Buildx_du_and_prune_and_builder_prune_all_succeed()
    {
        var du = await daemon.DockerAsync(["buildx", "du"], timeout: TimeSpan.FromMinutes(2));
        Assert.True(du.Ok, du.ToString());

        var buildxPrune = await daemon.DockerAsync(["buildx", "prune", "-f"], timeout: TimeSpan.FromMinutes(2));
        Assert.True(buildxPrune.Ok, buildxPrune.ToString());

        var builderPrune = await daemon.DockerAsync(["builder", "prune", "-f"], timeout: TimeSpan.FromMinutes(2));
        Assert.True(builderPrune.Ok, builderPrune.ToString());
    }

    [E2EFact]
    public async Task Compose_build_with_two_services_sharing_one_context_builds_and_runs_both()
    {
        const string composeFile = """
            services:
              svc-a:
                build:
                  context: .
                  dockerfile: Dockerfile.a
                command: ["sleep", "300"]
              svc-b:
                build:
                  context: .
                  dockerfile: Dockerfile.b
                command: ["sleep", "300"]
            """;

        var project = "e2ebk" + Guid.NewGuid().ToString("n")[..8];
        var directory = Path.Combine(daemon.ScratchDir, project);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "docker-compose.yml"), composeFile);
        await File.WriteAllTextAsync(Path.Combine(directory, "shared.txt"), "shared-context-marker\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile.a"), Alpine + "COPY shared.txt /shared.txt\nRUN echo svc-a >> /shared.txt\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile.b"), Alpine + "COPY shared.txt /shared.txt\nRUN echo svc-b >> /shared.txt\n");

        try
        {
            var build = await daemon.DockerAsync(
                ["compose", "-p", project, "build"],
                timeout: TimeSpan.FromMinutes(6),
                extraEnvironment: BuildKitEnv,
                workingDirectory: directory);
            Assert.True(build.Ok, build.ToString());

            var up = await daemon.DockerAsync(
                ["compose", "-p", project, "up", "-d"],
                timeout: TimeSpan.FromMinutes(3),
                extraEnvironment: BuildKitEnv,
                workingDirectory: directory);
            Assert.True(up.Ok, up.ToString());

            var ps = await daemon.DockerAsync(
                ["compose", "-p", project, "ps", "--format", "{{.Service}}: {{.State}}"],
                timeout: TimeSpan.FromMinutes(2),
                workingDirectory: directory);
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains("svc-a: running", ps.Stdout, StringComparison.Ordinal);
            Assert.Contains("svc-b: running", ps.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            var down = await daemon.DockerAsync(
                ["compose", "-p", project, "down", "-v", "--remove-orphans", "--rmi", "local"],
                timeout: TimeSpan.FromMinutes(5),
                workingDirectory: directory);
            Assert.True(down.Ok, down.ToString());
        }
    }

    [E2EFact]
    public async Task Buildx_bake_with_two_targets_sharing_context_builds_both()
    {
        var tagA = UniqueTag("bk-bake-a");
        var tagB = UniqueTag("bk-bake-b");
        var directory = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("bake"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "shared.txt"), "bake-shared-context\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile.a"), Alpine + "COPY shared.txt /shared.txt\nRUN echo bake-a >> /shared.txt\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile.b"), Alpine + "COPY shared.txt /shared.txt\nRUN echo bake-b >> /shared.txt\n");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "docker-bake.hcl"),
            $$"""
            group "default" {
              targets = ["a", "b"]
            }

            target "a" {
              context    = "."
              dockerfile = "Dockerfile.a"
              tags       = ["{{tagA}}"]
            }

            target "b" {
              context    = "."
              dockerfile = "Dockerfile.b"
              tags       = ["{{tagB}}"]
            }
            """);

        var bake = await daemon.DockerAsync(
            ["buildx", "bake"],
            timeout: TimeSpan.FromMinutes(6),
            extraEnvironment: BuildKitEnv,
            workingDirectory: directory);
        Assert.True(bake.Ok, bake.ToString());

        try
        {
            var images = await daemon.DockerAsync("images", "--format", "{{.Repository}}:{{.Tag}}");
            Assert.True(images.Ok, images.ToString());
            Assert.Contains(tagA, images.Stdout, StringComparison.Ordinal);
            Assert.Contains(tagB, images.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await daemon.DockerAsync(["rmi", "-f", tagA, tagB], timeout: TimeSpan.FromMinutes(2));
        }
    }

    [E2EFact]
    public async Task Large_context_build_succeeds_within_budget()
    {
        var megabytes = Environment.GetEnvironmentVariable("CIDER_E2E_CONTEXT_MB") is { Length: > 0 } raw &&
            int.TryParse(raw, out var parsed) && parsed > 0
                ? parsed
                : 20;

        var tag = UniqueTag("bk-large-ctx");
        var context = await NewContextAsync("bk-large-ctx", Alpine + "COPY . /ctx\nRUN ls -la /ctx > /listing\n");
        await WriteRandomFileAsync(Path.Combine(context, "payload.bin"), megabytes * 1024L * 1024L);

        var build = await BuildAsync(["build", "-t", tag, "."], context, TimeSpan.FromSeconds(180));
        Assert.True(build.Ok, $"a {megabytes} MiB build context did not build within the 180s budget: " + build);

        await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Not gated behind CIDER_E2E_CONTEXT_MB (that variable is for the always-on 20 MiB check
    /// above): a fixed, deliberately large 200 MiB context, whose outcome is the evidence
    /// cider-ger.15 (non-exec bulk transport to the builder VM) was created to act on.
    /// </summary>
    [LargeContextFact]
    public async Task Large_200mib_context_characterization()
    {
        var tag = UniqueTag("bk-large-200");
        var context = await NewContextAsync("bk-large-200", Alpine + "COPY . /ctx\nRUN ls -la /ctx > /listing\n");
        await WriteRandomFileAsync(Path.Combine(context, "payload.bin"), 200L * 1024 * 1024);

        var build = await BuildAsync(["build", "-t", tag, "."], context, TimeSpan.FromMinutes(10));

        try
        {
            // No pass/fail budget here on purpose: this is characterization for cider-ger.15, not a
            // pass/fail contract on how fast the current exec-based context transport must be. The
            // build itself must still succeed, whatever it costs.
            Assert.True(build.Ok, build.ToString());
        }
        finally
        {
            if (build.Ok)
            {
                await daemon.DockerAsync(["rmi", "-f", tag], timeout: TimeSpan.FromMinutes(2));
            }
        }
    }

    private Task<CommandResult> BuildAsync(IEnumerable<string> arguments, string workingDirectory, TimeSpan? timeout = null) =>
        daemon.DockerAsync(arguments, timeout: timeout ?? TimeSpan.FromMinutes(6), extraEnvironment: BuildKitEnv, workingDirectory: workingDirectory);

    private async Task<string> NewContextAsync(string suffix, string dockerfile)
    {
        var context = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName(suffix));
        Directory.CreateDirectory(context);
        await File.WriteAllTextAsync(Path.Combine(context, "Dockerfile"), dockerfile);
        return context;
    }

    private static async Task WriteRandomFileAsync(string path, long sizeBytes)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        var remaining = sizeBytes;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(bufferSize, remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
            await stream.WriteAsync(buffer.AsMemory(0, chunk));
            remaining -= chunk;
        }
    }

    private static string UniqueTag(string suffix) => "e2e/" + DaemonFixture.NewName(suffix);

    /// <summary>Strips a leading <c>sha256:</c> so ids from different commands compare equal.</summary>
    private static string NormalizeId(string id) =>
        id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    /// <summary><c>docker images -q</c> ids, normalized to the short form <see cref="ShortId"/> produces.</summary>
    private static IReadOnlyList<string> ShortIds(string output) =>
    [
        .. output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => ShortId(NormalizeId(id))),
    ];
}
