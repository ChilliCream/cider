using System.IO.Pipelines;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moby.Buildkit.V1;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="GrpcForwarder"/> end to end, exactly the shape cider-ger.7's verification section
/// asks for: two in-process tunnels (see <see cref="TunnelTransport"/>) -- a "backend" hosting a
/// real <see cref="Moby.Buildkit.V1.Control.ControlBase"/> implementation, and a "front" that maps
/// ONLY <see cref="GrpcForwarder.MapGrpcForwarder"/> pointed at the backend's invoker -- proving a
/// client that only ever talks to the front gets byte-identical behavior to talking to the backend
/// directly: unary, server-streaming and duplex calls, echoed metadata, a thrown
/// <see cref="RpcException"/>'s status and message, and a method the backend does not implement
/// (<see cref="Control.ControlBase"/>'s generated default throws Unimplemented for any method a
/// subclass leaves unoverridden) reaching the backend through the forwarder rather than being
/// answered locally.
/// </summary>
public sealed class GrpcForwarderTests : IAsyncLifetime
{
    private const string BackendAuthority = "cider-backend";
    private const string FrontAuthority = "cider-tunnel";

    private readonly List<WebApplication> _apps = [];
    private WebApplication _front = null!;
    private HttpMessageInvoker _backendInvoker = null!;
    private SocketsHttpHandler _backendHandler = null!;
    private int _forwardCount;

    public async Task InitializeAsync()
    {
        var backend = await CreateHostAsync(app => app.MapGrpcService<TestControlService>());
        var backendTransport = backend.Services.GetRequiredService<TunnelTransport>();

        var (backendServer, backendClient) = CreateDuplexPair();
        _ = backendTransport.ServeAsync(backendServer, TunnelKind.Control);
        (_, _backendInvoker, _backendHandler) = StreamHttp2Client.Create(backendClient, BackendAuthority);

        _front = await CreateHostAsync(app => app.MapGrpcForwarder(TunnelKind.Control, _ =>
        {
            Interlocked.Increment(ref _forwardCount);
            return ValueTask.FromResult<ForwardTarget?>(new ForwardTarget
            {
                Invoker = _backendInvoker,
                Authority = BackendAuthority,
            });
        }));
    }

    public async Task DisposeAsync()
    {
        _backendInvoker?.Dispose();
        _backendHandler?.Dispose();

        foreach (var app in _apps)
        {
            try
            {
                await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }

            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unary_call_is_forwarded_with_echoed_headers_and_trailers()
    {
        var client = DialFront();

        using var call = client.DiskUsageAsync(new DiskUsageRequest(), headers: EchoMetadata());
        var response = await call.ResponseAsync;
        var headers = await call.ResponseHeadersAsync;
        var trailers = call.GetTrailers();

        Assert.NotNull(response);
        Assert.Equal("hv", GetValue(headers, "x-echo-header"));
        Assert.Equal("tv", GetValue(trailers, "x-echo-trailer"));
        Assert.True(Volatile.Read(ref _forwardCount) > 0);
    }

    [Fact]
    public async Task Server_streaming_call_is_forwarded_with_echoed_headers_and_trailers()
    {
        var client = DialFront();

        using var call = client.Status(new StatusRequest(), headers: EchoMetadata());
        var count = 0;
        await foreach (var _ in call.ResponseStream.ReadAllAsync())
        {
            count++;
        }

        var headers = await call.ResponseHeadersAsync;
        var trailers = call.GetTrailers();

        Assert.Equal(TestControlService.StatusMessageCount, count);
        Assert.Equal("hv", GetValue(headers, "x-echo-header"));
        Assert.Equal("tv", GetValue(trailers, "x-echo-trailer"));
    }

    [Fact]
    public async Task Duplex_call_completes_a_1000_message_interleaved_exchange()
    {
        var client = DialFront();

        using var call = client.Session();

        var readTask = Task.Run(async () =>
        {
            var received = 0;
            await foreach (var message in call.ResponseStream.ReadAllAsync())
            {
                Assert.Equal(received.ToString(), message.Data.ToStringUtf8());
                received++;
            }

            return received;
        });

        for (var i = 0; i < 1000; i++)
        {
            await call.RequestStream.WriteAsync(new BytesMessage { Data = ByteString.CopyFromUtf8(i.ToString()) });

            // Interleave: every so often, give the concurrently-running reader a chance to drain
            // what has been echoed back so far rather than writing all 1000 messages up front.
            if (i % 97 == 0)
            {
                await Task.Yield();
            }
        }

        await call.RequestStream.CompleteAsync();
        var received = await readTask;

        Assert.Equal(1000, received);
    }

    [Fact]
    public async Task A_thrown_RpcException_status_and_message_are_forwarded()
    {
        var client = DialFront();

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.ListWorkersAsync(new ListWorkersRequest()).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal("nope", ex.Status.Detail);
    }

    [Fact]
    public async Task A_method_the_backend_does_not_implement_reaches_the_backend_through_the_forwarder()
    {
        var client = DialFront();
        var before = Volatile.Read(ref _forwardCount);

        // Control.ControlBase's generated code throws Unimplemented for any method a subclass
        // leaves unoverridden (see ControlGrpc.cs); TestControlService never overrides Info. If this
        // came back Unimplemented WITHOUT the forwarder ever running, something on the front (not
        // mapped here at all) answered locally instead of the call reaching the backend.
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.InfoAsync(new InfoRequest()).ResponseAsync);

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
        Assert.True(Volatile.Read(ref _forwardCount) > before);
    }

    [Fact]
    public async Task A_target_that_throws_HttpRequestException_yields_status_14_and_invokes_OnFailure()
    {
        Exception? failure = null;
        var throwingInvoker = new HttpMessageInvoker(new ThrowingHandler());

        var app = await CreateHostAsync(a => a.MapGrpcForwarder(TunnelKind.Control, _ => ValueTask.FromResult<ForwardTarget?>(new ForwardTarget
        {
            Invoker = throwingInvoker,
            Authority = BackendAuthority,
            OnFailure = ex => failure = ex,
        })));

        var transport = app.Services.GetRequiredService<TunnelTransport>();
        var (server, clientStream) = CreateDuplexPair();
        _ = transport.ServeAsync(server, TunnelKind.Control);
        var (channel, invoker, handler) = StreamHttp2Client.Create(clientStream, FrontAuthority);
        try
        {
            var client = new Control.ControlClient(channel);
            var ex = await Assert.ThrowsAsync<RpcException>(() => client.InfoAsync(new InfoRequest()).ResponseAsync);

            Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
            Assert.StartsWith("cider: ", ex.Status.Detail, StringComparison.Ordinal);
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }

        Assert.IsType<HttpRequestException>(failure);
    }

    private Control.ControlClient DialFront()
    {
        var transport = _front.Services.GetRequiredService<TunnelTransport>();
        var (server, client) = CreateDuplexPair();
        _ = transport.ServeAsync(server, TunnelKind.Control);
        var (channel, _, _) = StreamHttp2Client.Create(client, FrontAuthority);
        return new Control.ControlClient(channel);
    }

    private async Task<WebApplication> CreateHostAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<TunnelTransport>();
        builder.Services.AddSingleton<IConnectionListenerFactory>(sp => sp.GetRequiredService<TunnelTransport>());
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = null;
            grpc.MaxSendMessageSize = null;
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(new TunnelEndPoint(), listen => listen.Protocols = HttpProtocols.Http2));

        var app = builder.Build();
        map(app);
        await app.StartAsync();
        _apps.Add(app);
        return app;
    }

    private static (DuplexStream Server, DuplexStream Client) CreateDuplexPair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        var client = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        return (server, client);
    }

    private static Metadata EchoMetadata(string headerValue = "hv", string trailerValue = "tv") => new()
    {
        { "x-echo-header", headerValue },
        { "x-echo-trailer", trailerValue },
    };

    private static string? GetValue(Metadata metadata, string key) =>
        metadata.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Always fails the send with an <see cref="HttpRequestException"/>, for the OnFailure/status-14 test.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("cider-test: upstream unreachable");
    }

    /// <summary>
    /// A hand-rolled <see cref="Moby.Buildkit.V1.Control.ControlBase"/>: implements one unary, one
    /// server-streaming and one duplex method (each echoing <c>x-echo-header</c>/<c>x-echo-trailer</c>
    /// request metadata into the response), one unary method that always throws
    /// <see cref="RpcException"/>(<see cref="StatusCode.NotFound"/>), and leaves every other method
    /// (including <see cref="Info"/>) unoverridden so calling it exercises the generated
    /// Unimplemented default -- exactly what cider-ger.7's verification section asks for.
    /// </summary>
    private sealed class TestControlService : Control.ControlBase
    {
        public const int StatusMessageCount = 3;

        public override async Task<DiskUsageResponse> DiskUsage(DiskUsageRequest request, ServerCallContext context)
        {
            await EchoAsync(context);
            return new DiskUsageResponse();
        }

        public override async Task Status(StatusRequest request, IServerStreamWriter<StatusResponse> responseStream, ServerCallContext context)
        {
            await EchoAsync(context);
            for (var i = 0; i < StatusMessageCount; i++)
            {
                await responseStream.WriteAsync(new StatusResponse());
            }
        }

        public override async Task Session(
            IAsyncStreamReader<BytesMessage> requestStream,
            IServerStreamWriter<BytesMessage> responseStream,
            ServerCallContext context)
        {
            await EchoAsync(context);
            await foreach (var message in requestStream.ReadAllAsync())
            {
                await responseStream.WriteAsync(new BytesMessage { Data = message.Data });
            }
        }

        public override Task<ListWorkersResponse> ListWorkers(ListWorkersRequest request, ServerCallContext context) =>
            throw new RpcException(new Status(StatusCode.NotFound, "nope"));

        private static async Task EchoAsync(ServerCallContext context)
        {
            var header = context.RequestHeaders.FirstOrDefault(entry => entry.Key == "x-echo-header")?.Value;
            if (header is not null)
            {
                await context.WriteResponseHeadersAsync(new Metadata { { "x-echo-header", header } });
            }

            var trailer = context.RequestHeaders.FirstOrDefault(entry => entry.Key == "x-echo-trailer")?.Value;
            if (trailer is not null)
            {
                context.ResponseTrailers.Add("x-echo-trailer", trailer);
            }
        }
    }
}
