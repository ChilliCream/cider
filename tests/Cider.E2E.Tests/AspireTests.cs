using System.Globalization;
using Cider.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #13 — .NET Aspire 13.5.0 and the DCP orchestrator it ships, driving cider from their
/// own process. The AppHost fixture lives in <c>tests/e2e-aspire</c> and is deliberately not part of
/// the solution; the test shells out to <c>dotnet run</c> with <c>DOCKER_HOST</c> pointing here.
/// <para>
/// A green run means an Aspire app really worked: DCP created its session network and both container
/// resources on <em>this</em> daemon, copied its development certificates into them between create
/// and start, published their ports, and the consumer project round-tripped through redis and
/// postgres over those ports and printed <c>ASPIRE_OK</c>. Six daemon gaps used to stop this before
/// anything started; both were fixed.
/// </para>
/// <para>
/// The decisive assertion is made against <em>this fixture's own daemon socket</em>, not against the
/// AppHost's output: Testcontainers has been observed silently falling back to
/// <c>/var/run/docker.sock</c> (OrbStack) and looking perfectly healthy while doing so. Requiring
/// Aspire's containers to be listed by our daemon <em>while the AppHost is up</em> cannot be
/// satisfied by a run that talked to some other daemon; point <c>DOCKER_HOST</c> anywhere else and
/// this fails.
/// </para>
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class AspireTests(DaemonFixture daemon, ITestOutputHelper output)
{
    /// <summary>Every Aspire resource in the fixture is named with this prefix so teardown is checkable.</summary>
    private const string ResourcePrefix = "op3";

    /// <summary>The two container resources the AppHost declares (<c>AddRedis</c> / <c>AddPostgres</c>).</summary>
    private static readonly string[] ContainerResources = ["op3cache", "op3pg"];

    /// <summary>How long the AppHost waits for its resources before giving up and reporting failure.</summary>
    private const int StartTimeoutSeconds = 420;

    private static string AppHostDirectory =>
        Path.Combine(RepositoryRoot(), "tests", "e2e-aspire", "E2EAspireAppHost");

    [E2EFact]
    public async Task Aspire_runs_redis_and_postgres_on_this_daemon_and_the_consumer_talks_to_both()
    {
        var sentinel = Path.Combine(daemon.ScratchDir, "aspire-op3.sentinel");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = daemon.DockerHost,
            ["DOCKER_CONTEXT"] = null,
            ["DOCKER_CONFIG"] = daemon.DockerConfigDir,
            ["ASPIRE_E2E_SENTINEL"] = sentinel,
            ["ASPIRE_E2E_TIMEOUT_SECONDS"] = StartTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
            ["DOTNET_ENVIRONMENT"] = "Development",

            // `dotnet test` exports its own MSBuild location (the .NET 11 preview SDK the repository
            // global.json rolls forward to). Inherited, it overrides tests/e2e-aspire/global.json and
            // the AppHost build dies with MSB4216/MSB4027 before Aspire ever starts.
            ["MSBuildSDKsPath"] = null,
            ["MSBUILD_EXE_PATH"] = null,
            ["MSBuildExtensionsPath"] = null,
            ["MSBuildExtensionsPath32"] = null,
            ["MSBuildExtensionsPath64"] = null,
            ["MSBuildLoadMicrosoftTargetsReadOnly"] = null,
            ["MSBuildToolsPath"] = null,
            ["DOTNET_HOST_PATH"] = null,
        };

        // DCP starts its API server detached and it inherits whatever stdout the AppHost was given,
        // so a pipe would stay open past the AppHost's own exit; the run is redirected into a file
        // the test reads back instead.
        var log = Path.Combine(daemon.ScratchDir, "aspire-apphost.log");
        var run = Cmd.RunAsync(
            "/bin/sh",
            ["-c", $"exec dotnet run -c Release --project '{AppHostDirectory}' > '{log}' 2>&1"],
            environment,
            stdin: null,
            timeout: TimeSpan.FromMinutes(15),
            workingDirectory: AppHostDirectory);

        var running = await WatchForAspireContainersAsync(run);
        var result = await run;

        var apphostLog = ReadLog(log);
        output.WriteLine(result.ToString());
        output.WriteLine("--- containers on " + daemon.DockerHost + " while the AppHost was up ---\n" + running);
        output.WriteLine("--- apphost log ---\n" + apphostLog);

        // The AppHost really started, and it started against us.
        Assert.False(result.TimedOut, "the AppHost never exited\n" + apphostLog);
        Assert.Contains("APPHOST_DOCKER_HOST=" + daemon.DockerHost, apphostLog, StringComparison.Ordinal);

        // THE load-bearing assertion: Aspire's containers were listed by *our* daemon while the app
        // was up. A run that silently fell back to another docker daemon cannot satisfy this.
        foreach (var resource in ContainerResources)
        {
            Assert.Contains(resource, running, StringComparison.Ordinal);
        }

        // ... and the consumer really talked to both of them over their published ports.
        Assert.Contains("CONSUMER_REDIS_ROUNDTRIP=", apphostLog, StringComparison.Ordinal);
        Assert.Contains("CONSUMER_POSTGRES_QUERY=42", apphostLog, StringComparison.Ordinal);
        Assert.Contains("ASPIRE_OK", apphostLog, StringComparison.Ordinal);
        Assert.Contains("ASPIRE_RUN_OK", apphostLog, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);

        // DCP takes its containers, networks and volumes with it when the AppHost stops.
        await AssertNothingLeftBehindAsync();
    }

    /// <summary>
    /// Polls this fixture's own socket while the AppHost runs and returns the first listing that
    /// shows Aspire's containers (or the last one seen, if the AppHost exited first).
    /// </summary>
    private async Task<string> WatchForAspireContainersAsync(Task<CommandResult> run)
    {
        var last = "";
        while (!run.IsCompleted)
        {
            var ps = await daemon.DockerAsync(["ps", "--format", "{{.Names}}"], timeout: TimeSpan.FromSeconds(60));
            last = ps.Stdout;
            if (ContainerResources.All(resource => last.Contains(resource, StringComparison.Ordinal)))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return last;
    }

    /// <summary>
    /// Aspire/DCP must take its containers, networks and volumes with it when it stops. Its api
    /// server is detached and deletes them only once the AppHost process it was told to
    /// <c>--monitor</c> is gone, so teardown finishes a few seconds after <c>dotnet run</c> returns.
    /// </summary>
    private async Task AssertNothingLeftBehindAsync()
    {
        var cleaned = await DaemonFixture.EventuallyAsync(
            IsCleanAsync,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(2));

        var (containers, volumes, networks) = await ListObjectsAsync();
        Assert.True(
            cleaned,
            FormattableString.Invariant(
                $"Aspire left objects behind on {daemon.DockerHost}:\ncontainers: {containers}\nvolumes: {volumes}\nnetworks: {networks}"));
    }

    private async Task<bool> IsCleanAsync()
    {
        var (containers, volumes, networks) = await ListObjectsAsync();
        return !containers.Contains(ResourcePrefix, StringComparison.Ordinal)
            && !volumes.Contains(ResourcePrefix, StringComparison.Ordinal)

            // Only Aspire's own networks are this test's business: other tests in the collection may
            // legitimately have networks on this daemon.
            && !networks.Contains("aspire", StringComparison.OrdinalIgnoreCase)
            && !networks.Contains(ResourcePrefix, StringComparison.Ordinal);
    }

    private async Task<(string Containers, string Volumes, string Networks)> ListObjectsAsync()
    {
        var containers = await daemon.DockerAsync(["ps", "-a", "--format", "{{.Names}}"], timeout: TimeSpan.FromSeconds(60));
        var volumes = await daemon.DockerAsync(["volume", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
        var networks = await daemon.DockerAsync(["network", "ls", "--format", "{{.Name}}"], timeout: TimeSpan.FromSeconds(60));
        return (containers.Stdout, volumes.Stdout, networks.Stdout);
    }

    private static string ReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is { Length: > 0 })
        {
            if (File.Exists(Path.Combine(directory, "Cider.sln")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory) ?? "";
        }

        throw new InvalidOperationException("could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
