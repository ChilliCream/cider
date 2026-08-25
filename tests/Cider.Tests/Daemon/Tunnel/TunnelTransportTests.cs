using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using Cider.Core.Configuration;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Daemon.Hosting;
using Cider.Daemon.Tunnel;
using Cider.Tests.Fakes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moby.Buildkit.V1;
using Xunit;

namespace Cider.Tests.Daemon.Tunnel;

/// <summary>
/// The in-process tunnel end to end: a real <see cref="DaemonHost"/> with
/// <see cref="Grpc.HealthCheck.HealthServiceImpl"/> mapped behind <see cref="TunnelRoutes.RequireTunnel{TBuilder}"/>
/// for the <see cref="TunnelKind.Control"/> leg, served over a synthetic duplex pipe pair via
/// <see cref="TunnelTransport"/> and dialed with <see cref="StreamHttp2Client"/> — proving Kestrel's
/// HTTP/2 engine, the gRPC plumbing and the tunnel guard all fit together without a real socket.
/// </summary>
public sealed class TunnelTransportTests : IAsyncLifetime
{
    private CiderOptions _options = null!;
    private WebApplication _app = null!;
    private TunnelTransport _transport = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("n")[..10];
        _options = new CiderOptions
        {
            DataDir = Path.Combine(Path.GetTempPath(), "ad-tunnel-tests", id),
            SocketPath = $"/tmp/cider-tunnel-test-{id}.sock",
            LogLevel = Environment.GetEnvironmentVariable("CIDER_TEST_LOGLEVEL") ?? "Warning",
            DnsEnabled = false,
            PollIntervalSeconds = 1,
        };
        _options.EnsureDirectories();

        var settings = new DaemonHostSettings
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
        };

        _app = DaemonHost.Create(_options, settings);

        // A marker gRPC service this suite maps itself, gated to the Control leg only, purely to
        // prove RequireTunnel gates a mapped service correctly — a Session-kind tunnel must see it
        // as unimplemented. Not Grpc.HealthCheck.HealthServiceImpl: DaemonHost.Create (cider-ger.9)
        // now maps that one for real, on the Session leg, so mapping it again here would collide.
        _app.MapGrpcService<TunnelGateMarkerService>().RequireTunnel(TunnelKind.Control);

        // Proves ErrorMiddleware really steps aside for tunnel requests (fix direction step 6):
        // this endpoint's exception must reach the client as whatever Kestrel does by default, never
        // as ErrorMiddleware's `{"message": ...}` JSON envelope.
        _app.MapGet("/_tunnel-test/throw", IResult () => throw new InvalidOperationException("boom"))
            .RequireTunnel(TunnelKind.Control);

        await _app.StartAsync();
        _transport = _app.Services.GetRequiredService<TunnelTransport>();
    }

    public async Task DisposeAsync()
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
            if (File.Exists(_options.SocketPath))
            {
                File.Delete(_options.SocketPath);
            }

            if (Directory.Exists(_options.DataDir))
            {
                Directory.Delete(_options.DataDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Unary_call_over_a_Control_tunnel_succeeds()
    {
        var (server, client) = CreateDuplexPair();
        var serveTask = _transport.ServeAsync(server, TunnelKind.Control);

        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "cider-tunnel");
        try
        {
            var control = new Control.ControlClient(channel);
            var response = await control.InfoAsync(new InfoRequest(), deadline: DateTime.UtcNow.AddSeconds(10));

            Assert.NotNull(response);
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
            await channel.ShutdownAsync();
        }

        await client.DisposeAsync();
        await WaitAsync(serveTask);
    }

    [Fact]
    public async Task Unary_call_over_a_Session_tunnel_is_unimplemented()
    {
        var (server, client) = CreateDuplexPair();
        var serveTask = _transport.ServeAsync(server, TunnelKind.Session);

        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "cider-tunnel");
        try
        {
            var control = new Control.ControlClient(channel);
            var ex = await Assert.ThrowsAsync<RpcException>(() =>
                control.InfoAsync(new InfoRequest(), deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync);

            Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
            await channel.ShutdownAsync();
        }

        await client.DisposeAsync();
        await WaitAsync(serveTask);
    }

    [Fact]
    public async Task Disposing_the_client_stream_completes_ServeAsync()
    {
        var (server, client) = CreateDuplexPair();
        var serveTask = _transport.ServeAsync(server, TunnelKind.Control);

        Assert.False(serveTask.IsCompleted);

        await client.DisposeAsync();

        await WaitAsync(serveTask);
        Assert.True(serveTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_exception_on_the_tunnel_is_not_rewritten_into_a_JSON_body_by_ErrorMiddleware()
    {
        var (server, client) = CreateDuplexPair();
        var serveTask = _transport.ServeAsync(server, TunnelKind.Control);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(client),
        };
        using var http = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        try
        {
            using var response = await http.GetAsync(new Uri("http://cider-tunnel/_tunnel-test/throw"));
            var body = await response.Content.ReadAsStringAsync();

            Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("\"message\"", body, StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            // Kestrel resetting the stream instead of answering is just as much proof that
            // ErrorMiddleware never got a chance to render its JSON envelope.
        }

        await client.DisposeAsync();
        await WaitAsync(serveTask);
    }

    private static (DuplexStream Server, DuplexStream Client) CreateDuplexPair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        var client = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        return (server, client);
    }

    private static async Task WaitAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        await task;
    }

    /// <summary>
    /// A trivial <see cref="Control.ControlBase"/> override, mapped only to answer <c>Info</c> --
    /// this suite's own stand-in for "some gRPC service gated to one tunnel leg", picked because it
    /// is already vendored and, unlike <c>Grpc.HealthCheck.HealthServiceImpl</c>, is not something
    /// <see cref="DaemonHost.Create"/> itself maps (which would collide with this suite's own
    /// mapping of the same service).
    /// </summary>
    private sealed class TunnelGateMarkerService : Control.ControlBase
    {
        public override Task<InfoResponse> Info(InfoRequest request, ServerCallContext context) =>
            Task.FromResult(new InfoResponse());
    }
}
