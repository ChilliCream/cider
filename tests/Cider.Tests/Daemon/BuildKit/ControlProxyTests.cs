using System.IO.Pipelines;
using Cider.Core.Configuration;
using Cider.Core.Events;
using Cider.Core.Services;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Cider.Tests.Fakes;
using Google.Protobuf;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moby.Buildkit.V1;
using Xunit;
using FsBytesMessage = Moby.Filesync.V1.BytesMessage;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="ControlProxyService"/> end to end against a fake buildkitd (<see cref="TestControlService"/>,
/// served over its own <see cref="TunnelTransport"/>, exactly cider-ger.9's <c>SessionBridgeTests</c>
/// pattern) and a real <see cref="ImageManager"/> backed by <see cref="FakeContainerRuntime"/> — cider-ger.10's
/// verification section:
/// (a) a <c>Solve</c> naming a session issued before that session registers still succeeds (proving
/// <see cref="ControlProxyService"/> attaches sessions BEFORE forwarding, blocking on
/// <see cref="CliSessionRegistry.WaitAsync"/> rather than failing outright), the fake buildkitd
/// receives <c>Type=docker</c>, drives a captured <c>FileSend</c> for exporter id 0, and the response's
/// <c>containerimage.digest</c> is the id <see cref="FakeContainerRuntime"/> assigned on load;
/// (b) a <c>Solve</c> naming <c>local-sessionid:ctx</c> reaches buildkitd only after the proxy's own
/// <c>Session</c> RPC attached that id; (c) an unmapped method (<c>Info</c>) reaches the fallback
/// forwarder untouched.
/// </summary>
public sealed class ControlProxyTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];
    private string _tmpDir = null!;

    private TestControlService _controlService = null!;
    private WebApplication _buildkitd = null!;
    private WebApplication _cli = null!;
    private WebApplication _daemon = null!;

    private FakeContainerRuntime _runtime = null!;
    private ImageManager _images = null!;
    private CliSessionRegistry _registry = null!;
    private BuilderLink _link = null!;

    public async Task InitializeAsync()
    {
        _tmpDir = Directory.CreateTempSubdirectory("cider-controlproxy-tests-").FullName;
        var options = new CiderOptions { DataDir = _tmpDir };
        options.EnsureDirectories();

        _controlService = new TestControlService();
        _buildkitd = await CreateHostAsync(
            services => services.AddSingleton(_controlService),
            app => app.MapGrpcService<TestControlService>());

        // Nothing is mapped on the CLI side: every exporter capture in these tests is claimed by
        // SessionBridge before it could ever reach a CLI session, so no CLI-side service is needed.
        _cli = await CreateHostAsync(_ => { }, _ => { });

        _runtime = new FakeContainerRuntime();
        var events = new EventBus();
        _images = new ImageManager(_runtime, events, options, NullLoggerFactory.Instance.CreateLogger<ImageManager>());

        var (buildkitdServer, buildkitdClient) = CreateDuplexPair();
        _ = _buildkitd.Services.GetRequiredService<TunnelTransport>().ServeAsync(buildkitdServer, TunnelKind.Control);
        var (channel, invoker, handler) = StreamHttp2Client.Create(buildkitdClient, "buildkitd");
        var tracker = new BuilderLinkTracker();
        var target = new ForwardTarget
        {
            Invoker = ActivityTrackingHttpInvoker.Wrap(invoker, tracker),
            Authority = "buildkit",
            Pacer = new TokenBucketPacer(tracker: tracker),
        };
        _link = new BuilderLink(
            channel,
            invoker,
            new ActivityTrackingCallInvoker(channel.CreateCallInvoker(), tracker),
            target,
            tracker,
            new NoopProcess(),
            handler);

        var builderConnection = new FakeBuilderConnection(_link);

        _daemon = await CreateHostAsync(
            services =>
            {
                services.AddSingleton(options);
                services.AddSingleton(_images);
                services.AddSingleton(events);
                services.AddSingleton<CliSessionRegistry>();
                services.AddSingleton<IRawSessionDialer>(new FakeRawSessionDialer(_buildkitd));
                services.AddSingleton<SessionBridge>();
                services.AddSingleton<ExportLoader>();
                services.AddSingleton<ControlProxyService>();
                services.AddSingleton<IServiceMethodProvider<ControlProxyService>, ControlProxyMethodProvider>();
                services.AddSingleton<IBuilderConnection>(builderConnection);
            },
            app =>
            {
                app.MapGrpcService<ControlProxyService>().RequireTunnel(TunnelKind.Control);
                var connection = app.Services.GetRequiredService<IBuilderConnection>();
                var sessionBridge = app.Services.GetRequiredService<SessionBridge>();

                // A single combined fallback for both tunnel legs -- see DaemonHost.Create's own
                // route mapping for why two separate GrpcForwarder.MapGrpcForwarder calls (each
                // mapping "/{service}/{method}") are ambiguous the moment both legs exist on one app.
                var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ControlProxyTests.Forwarder");
                app.MapFallback("/{service}/{method}", async (HttpContext http) =>
                {
                    var kind = http.Features.Get<ITunnelFeature>()?.Kind;
                    var target = kind switch
                    {
                        TunnelKind.Session => await sessionBridge.SelectForwardTarget(http).ConfigureAwait(false),
                        TunnelKind.Control => await SelectControlTargetAsync(http, connection).ConfigureAwait(false),
                        _ => null,
                    };

                    if (target is null)
                    {
                        http.Response.StatusCode = StatusCodes.Status200OK;
                        http.Response.ContentType = "application/grpc";
                        http.Response.Headers["grpc-status"] = "12";
                        http.Response.Headers["grpc-message"] = "cider: not available on this tunnel";
                        return;
                    }

                    await GrpcForwarder.ForwardAsync(http, target, log).ConfigureAwait(false);
                });
            });

        _registry = _daemon.Services.GetRequiredService<CliSessionRegistry>();
    }

    public async Task DisposeAsync()
    {
        await _link.DisposeAsync();

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

        try
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// (a) A <c>Solve</c> naming session <c>S</c> is issued before <c>S</c> registers in
    /// <see cref="CliSessionRegistry"/>; the call still succeeds once it does, proving the proxy
    /// blocks on the attach rather than failing. The fake buildkitd receives the rewritten
    /// <c>docker</c> exporter, drives a captured <c>FileSend/DiffCopy</c> for exporter id 0, and the
    /// response reports cider's own image id.
    /// </summary>
    [Fact]
    public async Task Solve_attaches_a_not_yet_registered_session_then_rewrites_and_loads_the_export()
    {
        var (server, client) = CreateDuplexPair();
        _ = _daemon.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Control);
        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "grpc-tunnel-a");
        try
        {
            var control = new Control.ControlClient(channel);

            var request = new SolveRequest { Session = "S" };
            var exporter = new Exporter { Type = "moby" };
            exporter.Attrs["name"] = "app:1";
            request.Exporters.Add(exporter);

            var solveTask = control.SolveAsync(request).ResponseAsync;

            // The Solve call is now blocked inside AttachSessionsAsync's WaitAsync("S", ...) --
            // register the CLI session only now, proving the block resolves once it appears rather
            // than the call having already failed.
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            var (cliServer, cliClient) = CreateDuplexPair();
            _ = _cli.Services.GetRequiredService<TunnelTransport>().ServeAsync(cliServer, TunnelKind.Control);
            await using var cliSession = new CliSession("S", "shared-key", [], cliClient);
            _registry.Register(cliSession);

            var response = await solveTask.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("docker", _controlService.LastSolveExporterType);
            Assert.Equal("true", _controlService.LastSolveTarAttr);
            await _controlService.DriveCompletedTask.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(_controlService.DriveError);

            // cider-ger.16 regression coverage: mirrors SessionBridgeTests' header assertion --
            // this fake buildkitd's Session call also arrives through SessionBridge.OpenAsync's
            // LiteralHeadersRewriteStream dial, decoded by Kestrel's real HPACK decoder. The
            // method-count/no-comma checks read the raw HttpContext.Request.Headers StringValues
            // rather than ServerCallContext.RequestHeaders (Metadata) -- see the matching comment on
            // SessionBridgeTests.Bridges_captures_and_tears_down_idempotently for why: Grpc.AspNetCore
            // .Server's own Metadata construction re-joins repeated header lines with "," before this
            // test could ever see them separately, which real buildkitd (grpc-go) does not do.
            var expectedMethods = new HashSet<string>(cliSession.Methods, StringComparer.Ordinal)
            {
                BuildKitMethods.FileSend.DiffCopy.ToLowerInvariant(),
                BuildKitMethods.Health.Check.ToLowerInvariant(),
            };
            var rawHeaders = _controlService.LastRequestHeaders!;
            var methodValues = rawHeaders[BuildKitMethods.MetadataKeys.SessionGrpcMethod];
            Assert.Equal(expectedMethods.Count, methodValues.Count);
            Assert.Equal(expectedMethods, new HashSet<string>(methodValues!, StringComparer.Ordinal));
            foreach (var (_, values) in rawHeaders)
            {
                Assert.All(values, v => Assert.DoesNotContain(',', v!));
            }

            var headers = _controlService.LastHeaders!;
            Assert.Equal(cliSession.Id, headers.First(h => h.Key == BuildKitMethods.MetadataKeys.SessionUuid).Value);
            Assert.Equal(cliSession.SharedKey, headers.First(h => h.Key == BuildKitMethods.MetadataKeys.SessionSharedKey).Value);

            var expected = await _images.InspectAsync("docker.io/library/app:1", CancellationToken.None);
            Assert.Equal(expected.Id, response.ExporterResponse["containerimage.digest"]);
            Assert.Equal(expected.Id, response.ExporterResponse["containerimage.config.digest"]);
            Assert.Equal("docker.io/library/app:1", response.ExporterResponse["image.name"]);

            _registry.Unregister("S");
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }
    }

    /// <summary>
    /// (b) A <c>Solve</c> naming <c>FrontendAttrs["local-sessionid:ctx"]</c> only reaches buildkitd
    /// after the proxy's own <c>Session</c> RPC (dialed straight over <c>/grpc</c>, the bake shared
    /// local-context shape) has already attached that session id — never before.
    /// </summary>
    [Fact]
    public async Task Solve_attaches_a_local_sessionid_registered_through_the_proxys_own_Session_rpc()
    {
        _controlService.DriveExporterOnSession = false;

        var (server, client) = CreateDuplexPair();
        _ = _daemon.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Control);
        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "grpc-tunnel-b");
        try
        {
            var control = new Control.ControlClient(channel);

            var sessionHeaders = new Metadata
            {
                { BuildKitMethods.MetadataKeys.SessionUuid, "S2" },
                { BuildKitMethods.MetadataKeys.SessionSharedKey, "shared-key-2" },
            };
            using var sessionCall = control.Session(sessionHeaders);

            // Blocks until the proxy's Session handler has attached S2 to the fake buildkitd -- i.e.
            // buildkitd saw Control/Session for S2 -- before any Solve is issued.
            await _controlService.SessionSeenForS2.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var request = new SolveRequest();
            request.FrontendAttrs[BuildKitMethods.MetadataKeys.LocalSessionIdPrefix + "ctx"] = "S2";

            var response = await control.SolveAsync(request).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(response);
            Assert.True(_controlService.SessionSeenForS2.Task.IsCompletedSuccessfully);

            await sessionCall.RequestStream.CompleteAsync();
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }
    }

    /// <summary>(c) <c>Control/Info</c> is not mapped by <see cref="ControlProxyMethodProvider"/>, so it falls through to the plain forwarder and reaches the fake buildkitd untouched.</summary>
    [Fact]
    public async Task Info_falls_through_to_the_forwarder()
    {
        var (server, client) = CreateDuplexPair();
        _ = _daemon.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Control);
        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "grpc-tunnel-c");
        try
        {
            var control = new Control.ControlClient(channel);
            var response = await control.InfoAsync(new InfoRequest()).ResponseAsync.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotNull(response);
            Assert.True(_controlService.InfoCalled);
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }
    }

    private static async ValueTask<ForwardTarget?> SelectControlTargetAsync(HttpContext http, IBuilderConnection builder)
    {
        try
        {
            var link = await builder.GetAsync(http.RequestAborted).ConfigureAwait(false);
            return link.Target;
        }
        catch (BuilderUnavailableException)
        {
            return null;
        }
    }

    private async Task<WebApplication> CreateHostAsync(Action<IServiceCollection> configureServices, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<TunnelTransport>();
        builder.Services.AddSingleton<IConnectionListenerFactory>(sp => sp.GetRequiredService<TunnelTransport>());
        builder.Services.AddGrpc(grpc =>
        {
            grpc.IgnoreUnknownServices = true;
            grpc.MaxReceiveMessageSize = null;
            grpc.MaxSendMessageSize = null;
        });
        configureServices(builder.Services);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Listen(new TunnelEndPoint(), listen => listen.Protocols = HttpProtocols.Http2);
        });

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

    /// <summary>
    /// <see cref="IRawSessionDialer"/> for tests: hands <see cref="SessionBridge"/> a fresh in-memory
    /// duplex pair into the same fake buildkitd host a real dial would reach through
    /// <c>buildctl dial-stdio</c>, instead of touching <see cref="Cider.Core.Runtime.IContainerRuntime"/> at all.
    /// </summary>
    private sealed class FakeRawSessionDialer(WebApplication buildkitd) : IRawSessionDialer
    {
        public Task<(Stream Duplex, IAsyncDisposable Owner)> DialAsync(CancellationToken cancellationToken)
        {
            var (server, client) = CreateDuplexPair();
            _ = buildkitd.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Control);
            return Task.FromResult<(Stream, IAsyncDisposable)>((client, new NoopProcess()));
        }
    }

    /// <summary>A minimal <see cref="Cider.Core.Runtime.IContainerProcess"/> good enough to back a directly-constructed <see cref="BuilderLink"/> in tests.</summary>
    private sealed class NoopProcess : Cider.Core.Runtime.IContainerProcess
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? Pid => null;

        public bool HasTty => false;

        public Stream? Stdin => null;

        public Stream Stdout => Stream.Null;

        public Stream? Stderr => null;

        public Task<int> Exited => _exited.Task;

        public Task CloseStdinAsync() => Task.CompletedTask;

        public Task ResizeAsync(int cols, int rows, CancellationToken ct) => Task.CompletedTask;

        public Task KillAsync(string signal, CancellationToken ct)
        {
            _exited.TrySetResult(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Hands out a single, pre-built <see cref="BuilderLink"/> and counts <see cref="Invalidate"/> calls; never redials.</summary>
    private sealed class FakeBuilderConnection(BuilderLink link) : IBuilderConnection
    {
        public int InvalidateCount;

        public ValueTask<BuilderLink> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(link);

        public void Invalidate(BuilderLink invalidated, Exception? reason) => Interlocked.Increment(ref InvalidateCount);
    }

    /// <summary>
    /// The fake buildkitd: <c>Solve</c> records the (rewritten) exporter and signals
    /// <c>Session</c>'s driver that it may proceed; <c>Session</c> treats the bidi <see cref="BytesMessage"/>
    /// stream as an embedded connection it dials as the client (mirroring cider-ger.9's
    /// <c>SessionBridgeTests.TestControlService</c>) and, when <see cref="DriveExporterOnSession"/>,
    /// drives a captured <c>FileSend/DiffCopy</c> for exporter id 0 once <c>Solve</c> has run.
    /// </summary>
    private sealed class TestControlService : Control.ControlBase
    {
        private readonly TaskCompletionSource _readyToDrive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _driveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DriveExporterOnSession { get; set; } = true;

        public string? LastSolveExporterType { get; private set; }

        public string? LastSolveTarAttr { get; private set; }

        public bool InfoCalled { get; private set; }

        public TaskCompletionSource SessionSeenForS2 { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? DriveError { get; private set; }

        public Task DriveCompletedTask => _driveCompleted.Task;

        /// <summary>
        /// The request headers the most recent <c>Session</c> call arrived with -- mirrors
        /// <c>SessionBridgeTests.TestControlService.LastHeaders</c> (cider-ger.16 regression
        /// coverage), decoded by Kestrel's real HPACK decoder since <see cref="SessionBridge"/>
        /// dials this fake buildkitd the same way it dials the real one.
        /// </summary>
        public Metadata? LastHeaders { get; private set; }

        /// <summary>
        /// A snapshot of the raw ASP.NET Core request headers for the most recent <c>Session</c>
        /// call -- needed alongside <see cref="LastHeaders"/> because <see cref="Grpc.AspNetCore.Server"/>'s
        /// own <c>Metadata</c> construction comma-joins repeated header lines (see the usage site).
        /// Deliberately materialized into a plain dictionary the instant <see cref="Session"/> is
        /// entered rather than kept as a live <see cref="HttpContext"/> reference: this <c>Session</c>
        /// call's <see cref="SessionBridgeHandle"/> tears down the instant <c>Solve</c> releases its
        /// last reference (<see cref="SessionBridge.OpenAsync"/>/<c>Release</c>'s fire-and-forget
        /// <c>DisposeAsync</c>), which recycles Kestrel's pooled <see cref="HttpContext"/> for this
        /// request -- reading <c>HttpContext.Request.Headers</c> off a stashed context afterwards
        /// throws <see cref="ObjectDisposedException"/> ("IFeatureCollection has been disposed") the
        /// instant that teardown outraces the assertion, which is exactly what parallel test runs
        /// make likelier by delaying this class's own continuation past Solve's return.
        /// </summary>
        public IReadOnlyDictionary<string, StringValues>? LastRequestHeaders { get; private set; }

        public override Task<InfoResponse> Info(InfoRequest request, ServerCallContext context)
        {
            InfoCalled = true;
            return Task.FromResult(new InfoResponse());
        }

        public override Task<SolveResponse> Solve(SolveRequest request, ServerCallContext context)
        {
            if (request.Exporters.Count > 0)
            {
                LastSolveExporterType = request.Exporters[0].Type;
                LastSolveTarAttr = request.Exporters[0].Attrs.TryGetValue("tar", out var tar) ? tar : null;
            }

            _readyToDrive.TrySetResult();
            return Task.FromResult(new SolveResponse());
        }

        public override async Task Session(
            IAsyncStreamReader<BytesMessage> requestStream, IServerStreamWriter<BytesMessage> responseStream, ServerCallContext context)
        {
            LastHeaders = context.RequestHeaders;

            // Copy every header's StringValues out right now, synchronously, while the request is
            // still guaranteed live -- see LastRequestHeaders' doc comment for why holding the
            // HttpContext itself instead is unsafe.
            LastRequestHeaders = context.GetHttpContext().Request.Headers
                .ToDictionary(static h => h.Key, static h => h.Value, StringComparer.Ordinal);
            var sessionId = context.RequestHeaders.FirstOrDefault(h => h.Key == BuildKitMethods.MetadataKeys.SessionUuid)?.Value;
            if (sessionId == "S2")
            {
                SessionSeenForS2.TrySetResult();
            }

            var stream = new ServerBytesMessageStream(requestStream, responseStream);
            var (channel, invoker, handler) = StreamHttp2Client.Create(stream, $"embedded-{Guid.NewGuid():n}");
            try
            {
                if (DriveExporterOnSession)
                {
                    await _readyToDrive.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    await DriveFileSendAsync(channel).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DriveError = ex;
            }
            finally
            {
                _driveCompleted.TrySetResult();
            }

            try
            {
                await stream.Eof.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                invoker.Dispose();
                handler.Dispose();
            }
        }

        private static async Task DriveFileSendAsync(Grpc.Net.Client.GrpcChannel channel)
        {
            var fileSend = new Moby.Filesync.V1.FileSend.FileSendClient(channel);
            var headers = new Metadata { { BuildKitMethods.MetadataKeys.AttachableExporterId, "0" } };
            using var call = fileSend.DiffCopy(headers);

            // A minimal-but-valid empty tar (two zeroed 512-byte blocks = the POSIX EOF marker) --
            // FakeContainerRuntime.LoadImagesAsync only special-cases a real OCI layout; anything
            // else (this included) registers under its own fixed fallback reference.
            await call.RequestStream.WriteAsync(new FsBytesMessage { Data = ByteString.CopyFrom(new byte[1024]) });
            await call.RequestStream.CompleteAsync();

            var hasMore = await call.ResponseStream.MoveNext(CancellationToken.None);
            if (hasMore || call.GetStatus().StatusCode != StatusCode.OK)
            {
                throw new InvalidOperationException($"cider: fake FileSend/DiffCopy failed: {call.GetStatus()}");
            }
        }
    }

    /// <summary>
    /// The buildkitd-side mirror of <see cref="Cider.Daemon.BuildKit.BytesMessageStream"/> (see
    /// cider-ger.9's <c>SessionBridgeTests</c> for the client-side original this duplicates): wraps a
    /// gRPC server's own bidi <see cref="BytesMessage"/> stream as a plain duplex <see cref="Stream"/>
    /// for <see cref="StreamHttp2Client"/> to dial as the embedded connection's h2 client.
    /// </summary>
    private sealed class ServerBytesMessageStream(
        IAsyncStreamReader<BytesMessage> reader, IServerStreamWriter<BytesMessage> writer) : Stream
    {
        private const int MaxWriteChunk = 32 * 1024;

        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _pending;
        private bool _readerDone;

        public Task Eof => _eof.Task;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                if (_readerDone)
                {
                    return 0;
                }

                if (!await reader.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    _readerDone = true;
                    _eof.TrySetResult();
                    return 0;
                }

                _pending = reader.Current.Data.Memory;
                if (_pending.IsEmpty)
                {
                    return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
            }

            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.Span[..n].CopyTo(buffer.Span);
            _pending = _pending[n..];
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var offset = 0;
            do
            {
                var length = Math.Min(MaxWriteChunk, buffer.Length - offset);
                await writer.WriteAsync(new BytesMessage { Data = ByteString.CopyFrom(buffer.Slice(offset, length).Span) }).ConfigureAwait(false);
                offset += length;
            }
            while (offset < buffer.Length);
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
