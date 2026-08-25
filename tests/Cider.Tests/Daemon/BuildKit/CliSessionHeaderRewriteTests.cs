using System.IO.Pipelines;
using System.Net;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// cider-ger.18 correction: <see cref="CliSession.BeginHeaderRewriteAsync"/>'s gate must release the
/// instant the substitute HEADERS frame is actually written on the wire, not whenever the caller's own
/// <c>SendAsync</c> eventually returns -- otherwise one slow forward through a shared session (a
/// FileSync/DiffCopy in particular, which can run for the whole body transfer) starves every other
/// concurrent forward on the same session for that call's entire duration instead of just its HEADERS
/// write. Drives a real Kestrel HTTP/2 server as the CLI side of the session (the CLI is the H2
/// server on this leg, exactly as in production) so a wrong stream id, flags, or field list in the
/// substituted frame would break the connection or reach the wrong handler outright, rather than being
/// silently wrong -- a stronger end-to-end check of <c>CliSession.ClosableDuplexStream.BuildHeadersFrame</c>
/// than a MemoryStream harness alone, complementing <c>LiteralHeadersRewriteStreamTests</c>' byte-exact
/// coverage of the shared HPACK encoder.
/// </summary>
public sealed class CliSessionHeaderRewriteTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];
    private WebApplication _sessionServer = null!;
    private RewriteProbeHandler _handler = null!;

    public async Task InitializeAsync()
    {
        _handler = new RewriteProbeHandler();
        _sessionServer = await CreateHostAsync(_handler);
    }

    public async Task DisposeAsync()
    {
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

    /// <summary>
    /// Two callers, A and B, share one <see cref="CliSession"/>. A queues its fields and starts a call
    /// that the fake session server holds open (no response yet); B queues its own fields right after.
    /// Before A's HEADERS frame is written, B must still be blocked (the fields-pending assertion); the
    /// instant the fake server observes A's request -- proof A's substitute HEADERS frame reached the
    /// wire -- B's <c>BeginHeaderRewriteAsync</c> must complete, even though A's own <c>SendAsync</c>
    /// is still pending (the server has not yet responded). Finally, the server must have seen exactly
    /// A's fields for A's call and exactly B's fields for B's, never mixed.
    /// </summary>
    [Fact]
    public async Task Second_callers_rewrite_unblocks_once_the_firsts_HEADERS_frame_is_written_not_when_its_call_finishes()
    {
        var (server, client) = CreateDuplexPair();
        _ = _sessionServer.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Session);

        await using var cliSession = new CliSession("cider-test", "shared-key", ["/a/A", "/a/B"], client);

        var fieldsA = LiteralFields("/a/A", "A");
        var scopeA = await cliSession.BeginHeaderRewriteAsync(fieldsA, CancellationToken.None);

        // B queues right behind A, before A has written anything at all -- must stay blocked while
        // A's fields are still the pending ones.
        var beginB = cliSession.BeginHeaderRewriteAsync(LiteralFields("/a/B", "B"), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(beginB.IsCompleted);

        var sendTaskA = cliSession.Invoker.SendAsync(BuildRequest("/a/A"), CancellationToken.None);

        // The fake session server only reaches its handler once it has actually parsed A's HEADERS
        // frame off the wire -- a wrong stream id or a missing END_HEADERS flag in the substitute
        // frame this proves would break Kestrel's HTTP/2 engine before ever getting this far.
        var seenA = await _handler.WaitForRequestAsync("A").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("A", seenA["x-cider-caller"]);

        // A's own call is still in flight -- the fake server has not been told to respond -- so
        // whatever happens to B's gate next happens strictly before A's SendAsync could have returned.
        Assert.False(sendTaskA.IsCompleted);

        var scopeB = await beginB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(scopeB);
        Assert.False(sendTaskA.IsCompleted);

        // Releasing B's gate did not touch A's already-written frame: the fake server still reports
        // exactly A's fields for A's call.
        Assert.Equal("A", _handler.LastSeen("A")["x-cider-caller"]);

        _handler.Release("A");
        using var responseA = await sendTaskA.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        // A's HeaderRewriteScope was never disposed above -- prove GrpcForwarder's finally-dispose
        // fallback disposing it now (after the frame it guarded is long gone) is a harmless no-op.
        await scopeA.DisposeAsync();

        Task<HttpResponseMessage> sendTaskB;
        try
        {
            sendTaskB = cliSession.Invoker.SendAsync(BuildRequest("/a/B"), CancellationToken.None);
        }
        finally
        {
            await scopeB.DisposeAsync();
        }

        var seenB = await _handler.WaitForRequestAsync("B").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("B", seenB["x-cider-caller"]);

        _handler.Release("B");
        using var responseB = await sendTaskB.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
    }

    private static List<(string Name, string Value)> LiteralFields(string path, string caller) =>
    [
        (":method", "POST"),
        (":scheme", "http"),
        (":authority", "session"),
        (":path", path),
        ("x-cider-caller", caller),
    ];

    private static HttpRequestMessage BuildRequest(string path) => new(HttpMethod.Post, "http://session" + path)
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Content = new ByteArrayContent([1, 2, 3]),
    };

    private async Task<WebApplication> CreateHostAsync(RewriteProbeHandler handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<TunnelTransport>();
        builder.Services.AddSingleton<IConnectionListenerFactory>(sp => sp.GetRequiredService<TunnelTransport>());
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(new TunnelEndPoint(), listen => listen.Protocols = HttpProtocols.Http2));

        var app = builder.Build();
        app.Run(handler.HandleAsync);
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

    /// <summary>
    /// The fake session server's only handler: records the request headers it saw for whichever
    /// <c>x-cider-caller</c> value tagged the request (signaling <see cref="WaitForRequestAsync"/>),
    /// then blocks until <see cref="Release"/> names that same caller before responding 200 OK -- so a
    /// test can hold one call open while it drives assertions about a second, concurrent one.
    /// </summary>
    private sealed class RewriteProbeHandler
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, string>>> _seen = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];

        public async Task HandleAsync(HttpContext http)
        {
            var caller = http.Request.Headers["x-cider-caller"].ToString();
            var captured = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            SeenTcs(caller).TrySetResult(captured);
            await ReleaseTcs(caller).Task.ConfigureAwait(false);

            http.Response.StatusCode = StatusCodes.Status200OK;
        }

        public Task<Dictionary<string, string>> WaitForRequestAsync(string caller) => SeenTcs(caller).Task;

        public Dictionary<string, string> LastSeen(string caller) => SeenTcs(caller).Task.Result;

        public void Release(string caller) => ReleaseTcs(caller).TrySetResult();

        private TaskCompletionSource<Dictionary<string, string>> SeenTcs(string caller)
        {
            lock (_lock)
            {
                if (!_seen.TryGetValue(caller, out var tcs))
                {
                    tcs = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _seen[caller] = tcs;
                }

                return tcs;
            }
        }

        private TaskCompletionSource ReleaseTcs(string caller)
        {
            lock (_lock)
            {
                if (!_release.TryGetValue(caller, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _release[caller] = tcs;
                }

                return tcs;
            }
        }
    }
}
