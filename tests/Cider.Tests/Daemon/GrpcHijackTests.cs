using System.Net.Sockets;
using System.Text;
using Cider.Core.Configuration;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Hosting;
using Cider.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// <c>POST /grpc</c> and <c>POST /session</c> driven over the raw socket the way the docker CLI (and
/// therefore buildx's docker driver) dials them: bare path, no body, <c>Connection: Upgrade</c> +
/// <c>Upgrade: h2c</c>. Proves the two things buildx's driver-detection and buildkit's session
/// manager both depend on: a strict <c>101</c> (never any other status) when BuildKit is enabled, and
/// a plain <c>404</c> — indistinguishable from <see cref="Routes.StubRoutes"/>'s stub — the moment
/// it is not.
/// </summary>
public sealed class GrpcHijackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // 9-byte HTTP/2 frame header for an empty SETTINGS frame (length 0, type 0x04, flags 0, stream 0).
    private static readonly byte[] EmptySettingsFrame = [0, 0, 0, 0x04, 0, 0, 0, 0, 0];

    [Fact]
    public async Task Grpc_hijack_answers_101_and_speaks_http2_prior_knowledge()
    {
        await using var host = await TestHost.StartAsync();
        await using var stream = await host.ConnectAsync();

        await WriteAsync(stream, "POST /grpc HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: h2c\r\n\r\n");

        var head = await ReadHeadAsync(stream);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", head, StringComparison.Ordinal);
        Assert.Contains("Upgrade: h2c", head, StringComparison.Ordinal);

        // The h2c prior-knowledge preface buildx's gRPC client dials with, then an empty client
        // SETTINGS frame — the server must answer with its own SETTINGS frame (RFC 7540 §3.5: the
        // first frame either endpoint sends on a new connection is always SETTINGS).
        await WriteAsync(stream, "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        await stream.WriteAsync(EmptySettingsFrame).AsTask().WaitAsync(Timeout);
        await stream.FlushAsync().WaitAsync(Timeout);

        var frameHeader = await ReadExactAsync(stream, 9);
        Assert.Equal(0x04, frameHeader[3]);
    }

    [Fact]
    public async Task Grpc_is_dialable_repeatedly_and_concurrently()
    {
        await using var host = await TestHost.StartAsync();

        async Task<byte[]> DialOnceAsync()
        {
            await using var stream = await host.ConnectAsync();
            await WriteAsync(stream, "POST /grpc HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: h2c\r\n\r\n");
            var head = await ReadHeadAsync(stream);
            Assert.StartsWith("HTTP/1.1 101 Switching Protocols", head, StringComparison.Ordinal);

            await WriteAsync(stream, "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            await stream.WriteAsync(EmptySettingsFrame).AsTask().WaitAsync(Timeout);
            await stream.FlushAsync().WaitAsync(Timeout);
            return await ReadExactAsync(stream, 9);
        }

        var results = await Task.WhenAll(DialOnceAsync(), DialOnceAsync(), DialOnceAsync());
        Assert.All(results, header => Assert.Equal(0x04, header[3]));
    }

    [Fact]
    public async Task Grpc_answers_a_plain_404_with_no_upgrade_when_BuildKit_is_disabled()
    {
        await using var host = await TestHost.StartAsync(buildKitEnabled: false);
        await using var stream = await host.ConnectAsync();

        await WriteAsync(stream, "POST /grpc HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: h2c\r\n\r\n");

        var text = await ReadAllAsync(stream);
        Assert.StartsWith("HTTP/1.1 404", text, StringComparison.Ordinal);
        Assert.DoesNotContain("101", text[..15], StringComparison.Ordinal);
        Assert.Contains("page not found", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_hijack_registers_the_session_while_open_and_removes_it_on_close()
    {
        await using var host = await TestHost.StartAsync();
        var registry = host.Services.GetRequiredService<CliSessionRegistry>();

        var stream = await host.ConnectAsync();
        await WriteAsync(stream, SessionRequest("sess-open"));

        var head = await ReadHeadAsync(stream);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", head, StringComparison.Ordinal);
        Assert.Contains("Upgrade: h2c", head, StringComparison.Ordinal);

        Assert.True(registry.TryGet("sess-open", out var session));
        Assert.NotNull(session);
        Assert.Equal("key-1", session.SharedKey);
        Assert.Contains("/moby.filesync.v1.filesync/diffcopy", session.Methods);

        await stream.DisposeAsync();

        await WaitUntilAsync(() => !registry.TryGet("sess-open", out _));
    }

    [Fact]
    public async Task A_duplicate_session_id_gets_a_non_101_response()
    {
        await using var host = await TestHost.StartAsync();

        var stream1 = await host.ConnectAsync();
        await WriteAsync(stream1, SessionRequest("dup-session"));
        var head1 = await ReadHeadAsync(stream1);
        Assert.StartsWith("HTTP/1.1 101", head1, StringComparison.Ordinal);

        await using var stream2 = await host.ConnectAsync();
        await WriteAsync(stream2, SessionRequest("dup-session"));
        var text2 = await ReadAllAsync(stream2);
        Assert.DoesNotContain("101", text2[..15], StringComparison.Ordinal);
        Assert.Contains("409", text2, StringComparison.Ordinal);

        await stream1.DisposeAsync();
    }

    [Fact]
    public async Task Session_answers_a_plain_404_with_no_upgrade_when_BuildKit_is_disabled()
    {
        await using var host = await TestHost.StartAsync(buildKitEnabled: false);
        await using var stream = await host.ConnectAsync();

        await WriteAsync(stream, SessionRequest("disabled-session"));

        var text = await ReadAllAsync(stream);
        Assert.StartsWith("HTTP/1.1 404", text, StringComparison.Ordinal);
        Assert.Contains("page not found", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_without_a_session_id_gets_a_400()
    {
        await using var host = await TestHost.StartAsync();
        await using var stream = await host.ConnectAsync();

        await WriteAsync(stream, "POST /session HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: h2c\r\n\r\n");

        var text = await ReadAllAsync(stream);
        Assert.StartsWith("HTTP/1.1 400", text, StringComparison.Ordinal);
    }

    private static string SessionRequest(string sessionId) =>
        "POST /session HTTP/1.1\r\n" +
        "Host: docker\r\n" +
        "Connection: Upgrade\r\n" +
        "Upgrade: h2c\r\n" +
        $"X-Docker-Expose-Session-Uuid: {sessionId}\r\n" +
        "X-Docker-Expose-Session-Sharedkey: key-1\r\n" +
        "X-Docker-Expose-Session-Grpc-Method: /moby.filesync.v1.FileSync/DiffCopy\r\n" +
        "\r\n";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("condition was never satisfied within the timeout");
            }

            await Task.Delay(20);
        }
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask().WaitAsync(Timeout);
        await stream.FlushAsync().WaitAsync(Timeout);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read)).AsTask().WaitAsync(Timeout);
            Assert.True(n > 0, "the connection closed before enough bytes arrived");
            read += n;
        }

        return buffer;
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var received = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            Assert.True(read > 0, "the connection closed before the response head arrived");
            received.Write(buffer, 0, read);

            var text = Encoding.ASCII.GetString(received.ToArray());
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd >= 0)
            {
                return text[..headEnd];
            }
        }
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        var received = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    /// <summary>
    /// A throwaway daemon on a real Unix socket (the hijack interceptor only sits on that listener,
    /// never on <see cref="Cider.Daemon.Tunnel.TunnelTransport"/>'s in-process endpoint), with the
    /// fake engine and in-memory stores <see cref="Cider.Tests.Daemon.DaemonTestHost"/> also uses,
    /// built locally so each test can toggle <see cref="CiderOptions.BuildKitEnabled"/> and reach the
    /// app's <see cref="IServiceProvider"/> directly.
    /// </summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestHost(WebApplication app, CiderOptions options)
        {
            _app = app;
            Options = options;
        }

        public CiderOptions Options { get; }

        public IServiceProvider Services => _app.Services;

        public static async Task<TestHost> StartAsync(bool buildKitEnabled = true)
        {
            var id = Guid.NewGuid().ToString("n")[..10];
            var options = new CiderOptions
            {
                DataDir = Path.Combine(Path.GetTempPath(), "ad-grpc-hijack-tests", id),
                SocketPath = $"/tmp/cider-grpc-test-{id}.sock",
                LogLevel = Environment.GetEnvironmentVariable("CIDER_TEST_LOGLEVEL") ?? "Warning",
                DnsEnabled = false,
                PollIntervalSeconds = 1,
                BuildKitEnabled = buildKitEnabled,
            };
            options.EnsureDirectories();

            var app = DaemonHost.Create(options, new DaemonHostSettings
            {
                DnsEnabled = false,
                ConfigureServices = services =>
                {
                    services.AddSingleton<IContainerRuntime>(new FakeContainerRuntime());
                    services.AddSingleton<IRecordStore<ContainerRecord>>(new InMemoryRecordStore<ContainerRecord>());
                    services.AddSingleton<IRecordStore<NetworkRecord>>(new InMemoryRecordStore<NetworkRecord>());
                    services.AddSingleton<IRecordStore<VolumeRecord>>(new InMemoryRecordStore<VolumeRecord>());
                    services.AddSingleton<IDnsForwarderService>(NullDnsForwarderService.Instance);
                },
            });

            await app.StartAsync();

            var host = new TestHost(app, options);
            await host.WaitForPingAsync();
            return host;
        }

        public async Task<NetworkStream> ConnectAsync()
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Options.SocketPath)).WaitAsync(Timeout);
            return new NetworkStream(socket, ownsSocket: true);
        }

        private async Task WaitForPingAsync()
        {
            using var client = DaemonClient.Create(Options.SocketPath, TimeSpan.FromSeconds(30));
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri("/_ping", UriKind.Relative));
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                }

                await Task.Delay(50);
            }

            throw new InvalidOperationException($"the test daemon never answered on {Options.SocketPath}");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }

            await _app.DisposeAsync();

            try
            {
                if (File.Exists(Options.SocketPath))
                {
                    File.Delete(Options.SocketPath);
                }

                if (Directory.Exists(Options.DataDir))
                {
                    Directory.Delete(Options.DataDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
