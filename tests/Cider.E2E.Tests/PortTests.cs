using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cider.E2E.Tests.Infrastructure;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E #3 — published ports. In the default <c>proxy</c> mode the daemon binds the host port itself
/// and forwards into the container's VM, so a published port really carries traffic; the bookkeeping
/// (<c>docker port</c>, <c>inspect</c>, <c>ps</c>) is identical either way. The Apple-mode
/// characterization test only runs with <c>CIDER_PORT_PUBLISHING=apple</c>.
/// </summary>
[Collection(DaemonCollection.Name)]
[Trait("Category", "E2E")]
public sealed class PortTests(DaemonFixture daemon)
{
    private const string Image = "alpine:3.22";

    private const string Server =
        "while true; do { printf 'HTTP/1.1 200 OK\\r\\nContent-Length: 2\\r\\nConnection: close\\r\\n\\r\\nhi'; sleep 1; } | nc -l -p 8080 >/dev/null; done";

    private const string UdpEcho = "while true; do nc -u -l -p 9999 -e /bin/cat; done";

    [E2EFact]
    public async Task Published_ports_are_allocated_reported_and_carry_traffic()
    {
        var name = DaemonFixture.NewName("port");
        var fixedPort = FreeHostPort();

        var run = await daemon.DockerAsync(
            [
                "run", "-d", "--name", name,
                "-p", "0:8080",
                "-p", $"{fixedPort.ToString(CultureInfo.InvariantCulture)}:8080",
                Image, "sh", "-c", Server,
            ],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            // ---- docker port: both mappings, one of them OS-allocated ----
            var port = await daemon.DockerAsync("port", name, "8080");
            Assert.True(port.Ok, port.ToString());
            var mapped = ParsePorts(port.Stdout);
            Assert.Contains(fixedPort, mapped);
            var ephemeral = mapped.FirstOrDefault(candidate => candidate != fixedPort);
            Assert.True(ephemeral > 0, "no host port was allocated for `-p 0:8080`: " + port);
            Assert.Contains("0.0.0.0", port.Stdout, StringComparison.Ordinal);

            // ---- inspect NetworkSettings.Ports carries the same two bindings ----
            var inspect = await daemon.DockerAsync("inspect", "-f", "{{json .NetworkSettings.Ports}}", name);
            Assert.True(inspect.Ok, inspect.ToString());
            Assert.Contains("8080/tcp", inspect.Stdout, StringComparison.Ordinal);
            Assert.Contains(ephemeral.ToString(CultureInfo.InvariantCulture), inspect.Stdout, StringComparison.Ordinal);
            Assert.Contains(fixedPort.ToString(CultureInfo.InvariantCulture), inspect.Stdout, StringComparison.Ordinal);

            // ---- and `docker ps` shows them ----
            var ps = await daemon.DockerAsync("ps", "--filter", "name=" + name, "--format", "{{.Ports}}");
            Assert.True(ps.Ok, ps.ToString());
            Assert.Contains("8080/tcp", ps.Stdout, StringComparison.Ordinal);

            // ---- both host ports are bound ----
            Assert.True(await CanConnectAsync(ephemeral), $"nothing accepts TCP on the allocated host port {ephemeral}");
            Assert.True(await CanConnectAsync(fixedPort), $"nothing accepts TCP on the fixed host port {fixedPort}");

            if (DaemonFixture.AppleModePorts)
            {
                // Apple's own forwarder accepts and then fails to dial the guest; that defect is
                // pinned down by the characterization test below.
                return;
            }

            // ---- ... and in proxy mode they actually carry the traffic ----
            Assert.Equal("hi", await HttpGetAsync("127.0.0.1", fixedPort, TimeSpan.FromSeconds(60)));
            Assert.Equal("hi", await HttpGetAsync("127.0.0.1", ephemeral, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>A host-IP-qualified mapping binds that address only, exactly as dockerd does.</summary>
    [E2EFact]
    public async Task A_loopback_binding_is_reachable_on_loopback_only()
    {
        if (DaemonFixture.AppleModePorts)
        {
            return;
        }

        var name = DaemonFixture.NewName("lo");
        var hostPort = FreeHostPort();

        var run = await daemon.DockerAsync(
            [
                "run", "-d", "--name", name,
                "-p", $"127.0.0.1:{hostPort.ToString(CultureInfo.InvariantCulture)}:8080",
                Image, "sh", "-c", Server,
            ],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var port = await daemon.DockerAsync("port", name, "8080");
            Assert.True(port.Ok, port.ToString());
            Assert.Contains("127.0.0.1:" + hostPort.ToString(CultureInfo.InvariantCulture), port.Stdout, StringComparison.Ordinal);

            Assert.Equal("hi", await HttpGetAsync("127.0.0.1", hostPort, TimeSpan.FromSeconds(60)));

            // Nothing listens on the machine's routable address for this mapping.
            if (await LanAddressAsync() is { } lan)
            {
                Assert.False(
                    await CanConnectAsync(hostPort, IPAddress.Parse(lan), TimeSpan.FromSeconds(3)),
                    $"a 127.0.0.1-qualified publish must not be reachable on {lan}");
            }
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>UDP mappings are relayed too, replies included.</summary>
    [E2EFact]
    public async Task A_published_udp_port_relays_datagrams_both_ways()
    {
        if (DaemonFixture.AppleModePorts)
        {
            return;
        }

        var name = DaemonFixture.NewName("udp");
        var hostPort = FreeHostPort();

        var run = await daemon.DockerAsync(
            [
                "run", "-d", "--name", name,
                "-p", $"{hostPort.ToString(CultureInfo.InvariantCulture)}:9999/udp",
                Image, "sh", "-c", UdpEcho,
            ],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var port = await daemon.DockerAsync("port", name, "9999/udp");
            Assert.True(port.Ok, port.ToString());
            Assert.Contains(hostPort.ToString(CultureInfo.InvariantCulture), port.Stdout, StringComparison.Ordinal);

            var echoed = "";
            var relayed = await DaemonFixture.EventuallyAsync(
                async () =>
                {
                    echoed = await UdpRoundTripAsync(hostPort, "hello-udp");
                    return string.Equals(echoed, "hello-udp", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(2));

            Assert.True(relayed, $"the published UDP port did not echo the datagram back (got '{echoed}')");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// Characterizes a live Apple container 1.2.2 defect (only reachable with
    /// <c>CIDER_PORT_PUBLISHING=apple</c>): the host-side forwarder accepts the connection and
    /// then fails to dial the guest ("backend - connect failed: No route to host", visible in
    /// <c>container system logs</c>), so no bytes ever come back — while the very same server answers
    /// fine on the container's own VM IP. The daemon's default <c>proxy</c> mode exists because of it;
    /// when this test starts failing, Apple has fixed <c>-p</c> and <c>apple</c> mode is a real option
    /// again.
    /// </summary>
    [AppleModePortFact]
    public async Task Apple_container_does_not_relay_published_port_traffic_although_the_server_is_reachable_directly()
    {
        var name = DaemonFixture.NewName("relay");
        var hostPort = FreeHostPort();
        var run = await daemon.DockerAsync(
            ["run", "-d", "--name", name, "-p", $"{hostPort.ToString(CultureInfo.InvariantCulture)}:8080", Image, "sh", "-c", Server],
            timeout: TimeSpan.FromMinutes(4));
        Assert.True(run.Ok, run.ToString());

        try
        {
            var ip = await daemon.DockerAsync("inspect", "-f", "{{.NetworkSettings.IPAddress}}", name);
            Assert.True(ip.Ok, ip.ToString());
            var address = ip.Stdout.Trim();
            Assert.False(string.IsNullOrEmpty(address), "the container has no IP: " + ip);

            // The server itself answers on the VM's own address.
            var direct = await HttpGetAsync(address, 8080, TimeSpan.FromSeconds(60));
            Assert.Equal("hi", direct);

            // ... but not through the published host port. Poll for a while so a *working* Apple
            // build is detected rather than mistaken for the broken one.
            var throughProxy = await HttpGetAsync("127.0.0.1", hostPort, TimeSpan.FromSeconds(20));
            Assert.True(
                throughProxy.Length == 0,
                "Apple container's published-port forwarder now relays data — `apple` mode is usable "
                + $"again and this characterization test can go (got '{throughProxy}')");
        }
        finally
        {
            await daemon.DockerAsync(["rm", "-f", name], timeout: TimeSpan.FromMinutes(2));
        }
    }

    private static List<int> ParsePorts(string stdout) => stdout
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => int.Parse(line[(line.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture))
        .ToList();

    private static async Task<string> HttpGetAsync(string host, int port, TimeSpan budget)
    {
        var body = "";
        await DaemonFixture.EventuallyAsync(
            async () =>
            {
                var result = await Cmd.RunAsync(
                    "curl",
                    ["-s", "--max-time", "5", $"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/"],
                    timeout: TimeSpan.FromSeconds(15));
                body = result.Stdout.Trim();
                return result.Ok && body.Length > 0;
            },
            budget,
            TimeSpan.FromSeconds(1));

        return body;
    }

    /// <summary>Sends one datagram to a published UDP port and returns whatever comes back.</summary>
    private static async Task<string> UdpRoundTripAsync(int hostPort, string payload)
    {
        using var client = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, hostPort);
        await client.SendAsync(System.Text.Encoding.UTF8.GetBytes(payload), target);

        try
        {
            var reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return System.Text.Encoding.UTF8.GetString(reply.Buffer).Trim();
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException)
        {
            return "";
        }
    }

    private static Task<bool> CanConnectAsync(int hostPort) =>
        DaemonFixture.EventuallyAsync(
            () => TryConnectAsync(IPAddress.Loopback, hostPort, TimeSpan.FromSeconds(5)),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1));

    private static async Task<bool> CanConnectAsync(int hostPort, IPAddress address, TimeSpan timeout) =>
        await TryConnectAsync(address, hostPort, timeout);

    private static async Task<bool> TryConnectAsync(IPAddress address, int hostPort, TimeSpan timeout)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, hostPort)).WaitAsync(timeout);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>The machine's routable IPv4 address, when it has one.</summary>
    private static async Task<string?> LanAddressAsync()
    {
        foreach (var device in (string[])["en0", "en1"])
        {
            var result = await Cmd.RunAsync("ipconfig", ["getifaddr", device], timeout: TimeSpan.FromSeconds(10));
            var address = result.Stdout.Trim();
            if (result.Ok && IPAddress.TryParse(address, out _))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>Grabs a free TCP port from the OS and releases it again.</summary>
    private static int FreeHostPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.LocalEndPoint!).Port;
    }
}
