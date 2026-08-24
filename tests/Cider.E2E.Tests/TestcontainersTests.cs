using Cider.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #11 — a real third-party Docker client (Testcontainers for .NET, which talks Docker.DotNet's
/// hand-rolled HTTP over the unix socket) driving cider from its own process.
/// The fixture project lives in <c>tests/e2e-testcontainers</c> and is deliberately not part of the
/// solution; the test shells out to <c>dotnet run</c> with <c>DOCKER_HOST</c> pointing here.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class TestcontainersTests(DaemonFixture daemon, ITestOutputHelper output)
{
    private static string ProjectDirectory =>
        Path.Combine(RepositoryRoot(), "tests", "e2e-testcontainers");

    [E2EFact]
    public async Task Testcontainers_drives_the_daemon_with_the_reaper_disabled()
    {
        var result = await RunFixtureAsync(ryukDisabled: true);
        output.WriteLine(result.ToString());

        Assert.Contains($"RESOLVED_ENDPOINT={daemon.DockerHost}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Apple container", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("TC_READY", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("EXEC_EXIT=0 EXEC_OUT=EXEC_OK", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("TESTCONTAINERS_OK", result.Stdout, StringComparison.Ordinal);
        Assert.True(result.Ok, result.ToString());
    }

    /// <summary>
    /// With Ryuk enabled Testcontainers starts <c>testcontainers/ryuk</c> with the daemon socket
    /// bind-mounted (which cider rewrites to its own socket — the container is created and
    /// started through it) and then connects to Ryuk's <em>published</em> port from the host. In the
    /// default <c>proxy</c> port-publishing mode the daemon binds that host port itself and forwards
    /// into the container, so the connect succeeds and Ryuk really reaps. (Apple container 1.2.2's
    /// own <c>-p</c> forwarder still does not relay traffic — see <see cref="PortTests"/> and its
    /// <c>CIDER_PORT_PUBLISHING=apple</c>-gated characterization test — but that path is not
    /// exercised here since proxy mode is the default.)
    /// </summary>
    [E2EFact]
    public async Task Testcontainers_with_the_reaper_enabled_works_through_the_port_proxy()
    {
        var result = await RunFixtureAsync(ryukDisabled: false);
        output.WriteLine(result.ToString());

        Assert.Contains($"RESOLVED_ENDPOINT={daemon.DockerHost}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("TESTCONTAINERS_OK", result.Stdout, StringComparison.Ordinal);
        Assert.True(result.Ok, result.ToString());
    }

    private Task<CommandResult> RunFixtureAsync(bool ryukDisabled)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = daemon.DockerHost,
            ["DOCKER_CONTEXT"] = null,
            ["DOCKER_CONFIG"] = daemon.DockerConfigDir,
            ["TESTCONTAINERS_RYUK_DISABLED"] = ryukDisabled ? "true" : null,
        };

        return Cmd.RunAsync(
            "dotnet",
            ["run", "-c", "Release", "--project", ProjectDirectory],
            environment,
            stdin: null,
            timeout: TimeSpan.FromMinutes(12),
            workingDirectory: ProjectDirectory);
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
