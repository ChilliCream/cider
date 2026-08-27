using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cider.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Net;

/// <summary>
/// The daemon's own port forwarders, exercised end to end on loopback: a real server stands in for
/// the container, the proxy binds a random host port in front of it, and the traffic is checked to
/// come back byte for byte — including half-close, which is what makes <c>nc</c>- and HTTP/1.0-style
/// servers work through it.
/// </summary>
public sealed class PortProxyTests
{
    private const string ContainerId = "c0ffee";

    [Fact]
    public async Task Tcp_traffic_is_relayed_in_both_directions()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await PublishAsync(proxy, "tcp", server.Port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"));
        Assert.Equal("PING\n", await ReadAsync(stream, 5));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("pong\n"));
        Assert.Equal("PONG\n", await ReadAsync(stream, 5));
    }

    [Fact]
    public async Task A_half_close_from_the_client_is_propagated_to_the_container()
    {
        await using var server = EchoServer.Start(untilEof: true);
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await PublishAsync(proxy, "tcp", server.Port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("everything"));

        // The server only answers once it has seen EOF, so this hangs unless the half-close made it
        // all the way through the proxy.
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.Equal("EVERYTHING", await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task Several_connections_are_served_at_once()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await PublishAsync(proxy, "tcp", server.Port);

        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
                clients.Add(client);
            }

            for (var i = 0; i < clients.Count; i++)
            {
                await clients[i].GetStream().WriteAsync(Encoding.UTF8.GetBytes($"c{i}\n"));
            }

            for (var i = 0; i < clients.Count; i++)
            {
                Assert.Equal($"C{i}\n", await ReadAsync(clients[i].GetStream(), 3));
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Unpublishing_closes_the_listener()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await PublishAsync(proxy, "tcp", server.Port);
        Assert.True(proxy.IsPublished(ContainerId));
        Assert.Single(proxy.Snapshot());

        proxy.Unpublish(ContainerId);

        Assert.False(proxy.IsPublished(ContainerId));
        Assert.Empty(proxy.Snapshot());

        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () =>
            await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Udp_datagrams_are_relayed_and_the_reply_finds_its_way_back()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var echo = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var datagram = await server.ReceiveAsync();
                var reply = Encoding.UTF8.GetString(datagram.Buffer).ToUpperInvariant();
                await server.SendAsync(Encoding.UTF8.GetBytes(reply), datagram.RemoteEndPoint);
            }
        });

        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);
        var handle = await PublishAsync(proxy, "udp", serverPort);

        using var client = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, handle.Port.HostPort);

        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), target);
        var first = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("HELLO", Encoding.UTF8.GetString(first.Buffer));

        // The same source endpoint reuses its session, so the second reply comes back too.
        await client.SendAsync(Encoding.UTF8.GetBytes("again"), target);
        var second = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("AGAIN", Encoding.UTF8.GetString(second.Buffer));

        await echo.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_connection_accepted_before_the_address_is_known_is_held_and_relayed_once_it_resolves()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await proxy.PublishAsync(
            ContainerId, "tcp", IPAddress.Loopback, 0, null, server.Port, CancellationToken.None);
        Assert.Null(handle.Port.ContainerIp);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"));

        // Held, not refused: the connect above already succeeded, and nothing comes back yet because
        // the backend address is still unknown.
        using (var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var probe = new byte[16];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                _ = await stream.ReadAsync(probe, probeCts.Token));
        }

        proxy.ResolveAddress(ContainerId, IPAddress.Loopback);

        // Once resolved, the connection that was held is relayed, in both directions, without having
        // to be re-established.
        Assert.Equal("PING\n", await ReadAsync(stream, 5));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("pong\n"));
        Assert.Equal("PONG\n", await ReadAsync(stream, 5));
    }

    [Fact]
    public async Task A_forwarder_with_no_target_closes_a_held_connection_once_its_wait_timeout_elapses()
    {
        using var forwarder = new TcpPortForwarder(
            new IPEndPoint(IPAddress.Loopback, 0),
            ContainerId,
            null,
            9,
            NullLogger<PortProxyManager>.Instance,
            TimeSpan.FromMilliseconds(500));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.HostEndPoint.Port);
        var stream = client.GetStream();

        // Never calls ResolveTarget: the held connection must give up and close on its own once the
        // (short, test-only) wait timeout elapses, rather than hang until the real 30 s default.
        var probe = new byte[16];
        var read = await stream.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task A_held_connection_is_relayed_once_a_late_starting_backend_accepts_it()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var backendPort = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        reserved.Dispose();

        using var forwarder = new TcpPortForwarder(
            new IPEndPoint(IPAddress.Loopback, 0),
            ContainerId,
            null,
            backendPort,
            NullLogger<PortProxyManager>.Instance,
            TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.HostEndPoint.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"));

        // Resolve the address immediately, but nothing listens on backendPort yet: the retry loop
        // (cider-ede.18) must keep dialing rather than dropping the held connection on the first
        // connection-refused.
        forwarder.ResolveTarget(IPAddress.Loopback);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await using var server = EchoServer.StartOn(backendPort);

        Assert.Equal("PING\n", await ReadAsync(stream, 5));

        await stream.WriteAsync(Encoding.UTF8.GetBytes("pong\n"));
        Assert.Equal("PONG\n", await ReadAsync(stream, 5));
    }

    /// <summary>
    /// cider-bum, resolve-then-flow: a publication bound pending and resolved before any client
    /// shows up relays like one that was published with the address up front.
    /// </summary>
    [Fact]
    public async Task A_publication_resolved_before_the_first_connection_relays_normally()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await proxy.PublishAsync(
            ContainerId, "tcp", IPAddress.Loopback, 0, null, server.Port, CancellationToken.None);
        Assert.Null(handle.Port.ContainerIp);

        proxy.ResolveAddress(ContainerId, IPAddress.Loopback);
        Assert.Equal(IPAddress.Loopback, handle.Port.ContainerIp);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"));
        Assert.Equal("PING\n", await ReadAsync(stream, 5));
    }

    /// <summary>
    /// cider-bum, resolve-never: a held connection dropped because the backend address never arrived
    /// must leave a warning naming the container and the container port — the live failure was
    /// diagnosed against a daemon.log with zero forwarder lines — and the client must observe the
    /// drop rather than an open-forever socket.
    /// </summary>
    [Fact]
    public async Task A_held_connection_dropped_on_target_wait_timeout_warns_with_container_and_port()
    {
        var logger = new RecordingLogger();
        using var forwarder = new TcpPortForwarder(
            new IPEndPoint(IPAddress.Loopback, 0),
            ContainerId,
            null,
            5432,
            logger,
            TimeSpan.FromMilliseconds(300));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.HostEndPoint.Port);
        var stream = client.GetStream();

        // Bytes sent while held go nowhere; once the wait times out the connection is dropped and
        // the client sees the reset — either straight at the read (the unread bytes make the close
        // an RST, exactly what the live postgres probe observed) or, after a bare FIN, on writing.
        await stream.WriteAsync(Encoding.UTF8.GetBytes("SSLRequest"));
        var reset = await Assert.ThrowsAsync<IOException>(async () =>
        {
            var probe = new byte[16];
            if (await stream.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) > 0)
            {
                Assert.Fail("the held connection relayed data although no target was ever resolved");
            }

            for (var i = 0; i < 50; i++)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("more"));
                await Task.Delay(20);
            }
        });
        Assert.IsType<SocketException>(reset.InnerException);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(ContainerId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("5432", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// cider-bum, the root cause itself: a publication carrying a stale address (the record kept the
    /// previous boot's IP across a restart) must be retargeted when the address is resolved again,
    /// not skipped as "already resolved" — that skip is what left the live forwarder dialing a dead
    /// target while <c>docker inspect</c> showed the right one.
    /// </summary>
    [Fact]
    public async Task A_publication_with_a_stale_address_is_retargeted_when_the_address_is_resolved_again()
    {
        await using var server = EchoServer.Start();
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        // 127.0.0.2 answers with connection-refused on macOS loopback: a live but wrong backend.
        var stale = IPAddress.Parse("127.0.0.2");
        var handle = await proxy.PublishAsync(
            ContainerId, "tcp", IPAddress.Loopback, 0, stale, server.Port, CancellationToken.None);

        Assert.True(proxy.NeedsAddress(ContainerId, IPAddress.Loopback));

        proxy.ResolveAddress(ContainerId, IPAddress.Loopback);
        Assert.Equal(IPAddress.Loopback, handle.Port.ContainerIp);
        Assert.False(proxy.NeedsAddress(ContainerId, IPAddress.Loopback));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping\n"));
        Assert.Equal("PING\n", await ReadAsync(stream, 5));
    }

    [Fact]
    public async Task An_unresolved_connection_is_closed_when_the_publication_is_disposed()
    {
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        var handle = await proxy.PublishAsync(
            ContainerId, "tcp", IPAddress.Loopback, 0, null, 9, CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port.HostPort);
        var stream = client.GetStream();

        proxy.Unpublish(ContainerId);
        Assert.False(proxy.IsPublished(ContainerId));

        // The held connection observes a close instead of hanging until TcpPortForwarder.TargetWaitTimeout.
        var probe = new byte[16];
        var read = await stream.ReadAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Publishing_a_udp_mapping_without_an_address_is_rejected()
    {
        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            proxy.PublishAsync(ContainerId, "udp", IPAddress.Loopback, 0, null, 9999, CancellationToken.None));

        Assert.False(proxy.IsPublished(ContainerId));
    }

    [Fact]
    public async Task Binding_a_host_port_that_is_taken_is_reported_as_a_socket_failure()
    {
        using var taken = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        taken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        taken.Listen(1);
        var port = ((IPEndPoint)taken.LocalEndPoint!).Port;

        using var proxy = new PortProxyManager(NullLogger<PortProxyManager>.Instance);

        await Assert.ThrowsAsync<SocketException>(() => proxy.PublishAsync(
            ContainerId, "tcp", IPAddress.Loopback, port, IPAddress.Loopback, 9, CancellationToken.None));

        Assert.False(proxy.IsPublished(ContainerId));
    }

    private static Task<PublishedPortHandle> PublishAsync(PortProxyManager proxy, string proto, int containerPort) =>
        proxy.PublishAsync(ContainerId, proto, IPAddress.Loopback, 0, IPAddress.Loopback, containerPort, CancellationToken.None);

    private static async Task<string> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>Captures log entries so the tests can assert on level and rendered message.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Stands in for the container: upper-cases whatever it is sent, on loopback.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private EchoServer(bool untilEof, int port)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _listener.Listen(16);
            Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
            _loop = Task.Run(() => AcceptAsync(untilEof, _cts.Token));
        }

        public int Port { get; }

        public static EchoServer Start(bool untilEof = false) => new(untilEof, 0);

        /// <summary>Binds a specific, already-reserved port instead of an OS-picked one.</summary>
        public static EchoServer StartOn(int port) => new(untilEof: false, port);

        private async Task AcceptAsync(bool untilEof, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listener.AcceptAsync(ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client, untilEof, ct), CancellationToken.None);
            }
        }

        private static async Task ServeAsync(Socket client, bool untilEof, CancellationToken ct)
        {
            using var socket = client;
            var buffer = new byte[4096];
            var pending = new MemoryStream();
            try
            {
                while (true)
                {
                    var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (untilEof)
                    {
                        pending.Write(buffer, 0, read);
                        continue;
                    }

                    var upper = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer, 0, read).ToUpperInvariant());
                    await socket.SendAsync(upper, SocketFlags.None, ct);
                }

                if (untilEof)
                {
                    var upper = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(pending.ToArray()).ToUpperInvariant());
                    await socket.SendAsync(upper, SocketFlags.None, ct);
                    socket.Shutdown(SocketShutdown.Send);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Dispose();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }
}
