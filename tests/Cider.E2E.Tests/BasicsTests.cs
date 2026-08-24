using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>E2E #1 — version/info handshake and stdio/exit-code fidelity of a one-shot run.</summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class BasicsTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task Version_and_info_describe_the_apple_container_engine()
    {
        var version = await daemon.DockerAsync("version");
        Assert.True(version.Ok, version.ToString());
        Assert.Contains("Server:", version.Stdout, StringComparison.Ordinal);
        Assert.Contains("29.0.0", version.Stdout, StringComparison.Ordinal);
        Assert.Contains("1.47", version.Stdout, StringComparison.Ordinal);

        var serverOs = await daemon.DockerAsync("version", "--format", "{{.Server.Os}}/{{.Server.Arch}}/{{.Server.APIVersion}}");
        Assert.True(serverOs.Ok, serverOs.ToString());
        Assert.Equal("linux/arm64/1.47", serverOs.Stdout.Trim());

        var info = await daemon.DockerAsync("info", "--format", "{{.Driver}}|{{.OSType}}|{{.Architecture}}|{{.ServerVersion}}|{{.Swarm.LocalNodeState}}");
        Assert.True(info.Ok, info.ToString());
        var fields = info.Stdout.Trim().Split('|');
        Assert.Equal("apple-container", fields[0]);
        Assert.Equal("linux", fields[1]);
        Assert.Equal("aarch64", fields[2]);
        Assert.Equal("29.0.0", fields[3]);
        Assert.Equal("inactive", fields[4]);

        // `docker info` prints a "Swarm: inactive" line and no warnings about the daemon.
        var plain = await daemon.DockerAsync("info");
        Assert.True(plain.Ok, plain.ToString());
        Assert.Contains("Swarm: inactive", plain.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DNS server comes up even when another cider instance already holds the configured
    /// port (it walks the next 20), and this instance's forwarder container is named after its own
    /// data dir so two daemons never adopt each other's.
    /// </summary>
    [E2EFact]
    public async Task Dns_survives_a_taken_port_and_names_its_forwarder_after_this_instance()
    {
        // Force a forwarder to exist by putting a container on the default network.
        var probe = await daemon.DockerAsync(["run", "--rm", Image, "true"], timeout: TimeSpan.FromMinutes(4));
        Assert.True(probe.Ok, probe.ToString());

        var log = daemon.DaemonLog;
        Assert.Contains(log, line => line.Contains("DNS server listening on", StringComparison.Ordinal)
            || line.Contains("the DNS server listens on", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("container name resolution is off", StringComparison.Ordinal));

        var hash = Cider.Daemon.Dns.DnsForwarderService.DataDirHash(daemon.Options.DataDir);
        var listed = await DaemonFixture.EventuallyAsync(
            async () =>
            {
                var containers = await Cmd.RunAsync("container", ["ls", "-a"], timeout: TimeSpan.FromSeconds(60));
                return containers.Stdout.Contains("cider-dns-bridge-" + hash, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2));

        Assert.True(listed, $"no forwarder named cider-dns-bridge-{hash} was created");
    }

    [E2EFact]
    public async Task Run_separates_stdout_from_stderr_and_propagates_the_exit_code()
    {
        var result = await daemon.DockerAsync(
            ["run", "--rm", Image, "sh", "-c", "echo out; echo err 1>&2; exit 3"],
            timeout: TimeSpan.FromMinutes(4));

        Assert.False(result.TimedOut, result.ToString());
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Stdout.Trim());
        Assert.Contains("err", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("out", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("err", result.Stdout, StringComparison.Ordinal);
    }
}
