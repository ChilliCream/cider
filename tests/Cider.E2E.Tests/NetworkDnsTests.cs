using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #5 — user-defined networks, container-name DNS through the CoreDNS forwarder,
/// <c>host.docker.internal</c>, host reachability from a guest, and network removal semantics.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class NetworkDnsTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    [E2EFact]
    public async Task Container_names_and_host_docker_internal_resolve_on_a_user_defined_network()
    {
        var network = DaemonFixture.NewName("net");
        var peer = DaemonFixture.NewName("peer");

        var createNetwork = await daemon.DockerAsync(["network", "create", network], timeout: TimeSpan.FromMinutes(2));
        Assert.True(createNetwork.Ok, createNetwork.ToString());

        var runPeer = await daemon.DockerAsync(
            ["run", "-d", "--name", peer, "--network", network, Image, "sleep", "180"],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(runPeer.Ok, runPeer.ToString());

        try
        {
            var peerIp = (await daemon.DockerAsync("inspect", "-f", $"{{{{(index .NetworkSettings.Networks \"{network}\").IPAddress}}}}", peer)).Stdout.Trim();
            Assert.False(string.IsNullOrEmpty(peerIp), "the peer container has no address on " + network);

            // ---- container-name DNS ----
            var lookup = await daemon.DockerAsync(
                ["run", "--rm", "--network", network, Image, "nslookup", peer],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(lookup.Ok, lookup.ToString());
            Assert.Contains(peerIp, lookup.Stdout, StringComparison.Ordinal);

            // ---- host.docker.internal → the network gateway ----
            var gateway = (await daemon.DockerAsync("network", "inspect", "-f", "{{(index .IPAM.Config 0).Gateway}}", network)).Stdout.Trim();
            Assert.False(string.IsNullOrEmpty(gateway), "network " + network + " reports no gateway");

            var host = await daemon.DockerAsync(
                ["run", "--rm", "--network", network, Image, "sh", "-c", "getent hosts host.docker.internal || nslookup host.docker.internal"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(host.Ok, host.ToString());
            Assert.Contains(gateway, host.Stdout, StringComparison.Ordinal);

            // ---- the host itself is reachable on that gateway ----
            var port = FreeHostPort();
            var directory = Path.Combine(daemon.ScratchDir, DaemonFixture.NewName("www"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "host-is-up\n");

            await using var server = Cmd.Start(
                "python3",
                ["-m", "http.server", port.ToString(CultureInfo.InvariantCulture), "--bind", "0.0.0.0", "--directory", directory]);
            await WaitForHostServerAsync(port);

            var fetch = await daemon.DockerAsync(
                ["run", "--rm", "--network", network, Image, "wget", "-q", "-T", "20", "-O", "-", $"http://host.docker.internal:{port.ToString(CultureInfo.InvariantCulture)}/index.html"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(fetch.Ok, fetch.ToString());
            Assert.Equal("host-is-up", fetch.Stdout.Trim());

            // ---- network inspect lists the attached container ----
            var inspect = await daemon.DockerAsync("network", "inspect", network);
            Assert.True(inspect.Ok, inspect.ToString());
            Assert.Contains(peer, inspect.Stdout, StringComparison.Ordinal);

            // ---- removal is refused while a container is attached ----
            var refused = await daemon.DockerAsync(["network", "rm", network], timeout: TimeSpan.FromMinutes(2));
            Assert.False(refused.Ok, "network rm should fail while a container is attached: " + refused);
            Assert.Contains("active endpoints", refused.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", peer], timeout: TimeSpan.FromMinutes(2));
        }

        var removed = await daemon.DockerAsync(["network", "rm", network], timeout: TimeSpan.FromMinutes(2));
        Assert.True(removed.Ok, removed.ToString());

        var list = await daemon.DockerAsync("network", "ls", "--format", "{{.Name}}");
        Assert.DoesNotContain(network, list.Stdout, StringComparison.Ordinal);
    }

    [E2EFact]
    public async Task Network_connect_before_the_first_start_attaches_a_second_network()
    {
        var network = DaemonFixture.NewName("net");
        var container = DaemonFixture.NewName("con");

        var createNetwork = await daemon.DockerAsync(["network", "create", network], timeout: TimeSpan.FromMinutes(2));
        Assert.True(createNetwork.Ok, createNetwork.ToString());

        try
        {
            var create = await daemon.DockerAsync(
                ["create", "--name", container, Image, "sleep", "180"],
                timeout: TimeSpan.FromMinutes(4));
            Assert.True(create.Ok, create.ToString());

            // Apple fixes a container's networks at create time, so this re-creates the Apple
            // container with `--network default --network <network>` behind the scenes.
            var connect = await daemon.DockerAsync(["network", "connect", network, container], timeout: TimeSpan.FromMinutes(2));
            Assert.True(connect.Ok, connect.ToString());

            var start = await daemon.DockerAsync(["start", container], timeout: TimeSpan.FromMinutes(4));
            Assert.True(start.Ok, start.ToString());

            var bridgeIp = (await daemon.DockerAsync(
                "inspect", "-f", "{{(index .NetworkSettings.Networks \"bridge\").IPAddress}}", container)).Stdout.Trim();
            Assert.False(string.IsNullOrEmpty(bridgeIp), "the container has no address on bridge");

            var connectedIp = (await daemon.DockerAsync(
                "inspect", "-f", $"{{{{(index .NetworkSettings.Networks \"{network}\").IPAddress}}}}", container)).Stdout.Trim();
            Assert.False(string.IsNullOrEmpty(connectedIp), "the container has no address on " + network);
            Assert.NotEqual(bridgeIp, connectedIp);

            // The container is running now: its networks are frozen until it is re-created.
            var disconnect = await daemon.DockerAsync(["network", "disconnect", network, container], timeout: TimeSpan.FromMinutes(2));
            Assert.False(disconnect.Ok, "network disconnect should fail while the container runs: " + disconnect);
            Assert.Contains("not supported", disconnect.Stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", container], timeout: TimeSpan.FromMinutes(2));
            await daemon.DockerAsync(["network", "rm", network], timeout: TimeSpan.FromMinutes(2));
        }
    }

    private static async Task WaitForHostServerAsync(int port)
    {
        var up = await DaemonFixture.EventuallyAsync(
            async () =>
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(TimeSpan.FromSeconds(2));
                    return true;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(250));

        Assert.True(up, "the host-side python http.server never came up");
    }

    private static int FreeHostPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)listener.LocalEndPoint!).Port;
    }
}
