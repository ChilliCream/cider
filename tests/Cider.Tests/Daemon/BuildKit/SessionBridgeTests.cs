using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using Cider.Core.Configuration;
using Cider.Core.Runtime;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.HealthCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moby.Buildkit.V1;
using Xunit;
using FsBytesMessage = Moby.Filesync.V1.BytesMessage;
using FsPacket = Fsutil.Types.Packet;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="SessionBridge"/> end to end, exactly cider-ger.9's verification section: a fake
/// buildkitd (<see cref="TestControlService"/>, served over its own <see cref="TunnelTransport"/>)
/// dials the daemon back through <see cref="SessionBridge.AttachAsync"/>'s <c>Control/Session</c>
/// bridge and, as the embedded connection's h2 client, drives <c>FileSend/DiffCopy</c> for a
/// captured exporter id, <c>FileSend/DiffCopy</c> for a forwarded one, <c>Health/Check</c>, and
/// <c>FileSync/DiffCopy</c> -- while a fake CLI session (<see cref="TestCliFileSendService"/>,
/// <see cref="TestCliFileSyncService"/>) answers whatever the bridge forwards to it. Proves: the
/// captured export lands on disk byte-for-byte with its <c>exporter-md-*</c> metadata; the
/// non-captured exporter id and FileSync both reach the CLI with headers intact; the health check
/// answers SERVING; a second <see cref="SessionBridge.AttachAsync"/> for the same session id is a
/// no-op that never dials buildkitd twice; and releasing every reference tears the whole bridge down,
/// observed as buildkitd's own <c>Session</c> call finally completing.
/// </summary>
public sealed class SessionBridgeTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = [];
    private string _tmpDir = null!;
    private byte[] _exporter0Payload = null!;
    private string _expectedHash = null!;

    private TestControlService _controlService = null!;
    private TestCliFileSyncService _cliFileSync = null!;
    private TestCliFileSendService _cliFileSend = null!;

    private WebApplication _buildkitd = null!;
    private WebApplication _cli = null!;
    private WebApplication _daemon = null!;

    private CliSession _cliSession = null!;
    private BuilderLink _link = null!;
    private SessionBridge _sessionBridge = null!;

    public async Task InitializeAsync()
    {
        _tmpDir = Directory.CreateTempSubdirectory("cider-sessionbridge-tests-").FullName;
        var options = new CiderOptions { DataDir = _tmpDir };
        options.EnsureDirectories();

        _exporter0Payload = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(_exporter0Payload);
        _expectedHash = Convert.ToHexString(SHA256.HashData(_exporter0Payload));

        _controlService = new TestControlService(_exporter0Payload);
        _buildkitd = await CreateHostAsync(
            services => services.AddSingleton(_controlService),
            app => app.MapGrpcService<TestControlService>());

        _cliFileSync = new TestCliFileSyncService();
        _cliFileSend = new TestCliFileSendService();
        _cli = await CreateHostAsync(
            services =>
            {
                services.AddSingleton(_cliFileSync);
                services.AddSingleton(_cliFileSend);
            },
            app =>
            {
                app.MapGrpcService<TestCliFileSyncService>();
                app.MapGrpcService<TestCliFileSendService>();
            });

        _daemon = await CreateHostAsync(
            services =>
            {
                services.AddSingleton(options);
                services.AddSingleton<IRawSessionDialer>(new FakeRawSessionDialer(_buildkitd));
                services.AddSingleton<SessionBridge>();
                services.AddSingleton(_ =>
                {
                    var health = new HealthServiceImpl();
                    health.SetStatus(string.Empty, HealthCheckResponse.Types.ServingStatus.Serving);
                    return health;
                });
            },
            app =>
            {
                app.MapGrpcService<HealthServiceImpl>().RequireTunnel(TunnelKind.Session);
                var bridge = app.Services.GetRequiredService<SessionBridge>();
                app.MapGrpcForwarder(TunnelKind.Session, http => bridge.SelectForwardTarget(http));
            });
        _sessionBridge = _daemon.Services.GetRequiredService<SessionBridge>();

        var (cliServer, cliClient) = CreateDuplexPair();
        _ = _cli.Services.GetRequiredService<TunnelTransport>().ServeAsync(cliServer, TunnelKind.Control);
        _cliSession = new CliSession(
            "cli-session-1",
            "shared-key",
            [BuildKitMethods.FileSync.DiffCopy, BuildKitMethods.FileSend.DiffCopy],
            cliClient);

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
    }

    public async Task DisposeAsync()
    {
        await _link.DisposeAsync();
        await _cliSession.DisposeAsync();

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
    /// Drives the bridge exactly as buildkitd would: attaches twice (proving idempotency), captures
    /// an 8 MiB exporter-0 FileSend to disk with its metadata intact, answers Health/Check locally,
    /// and tears the whole thing down (proving both refs must release, and that release actually
    /// completes buildkitd's own Session call). FileSend/FileSync forwarding to the CLI session is
    /// covered separately by <see cref="SelectForwardTarget_dispatches_capture_and_forward_correctly"/> --
    /// splitting it out here sidesteps a harness-only timing hazard: the fake CLI fixture's
    /// synthetic (non-socket) connection tends to wind itself down the moment its one call
    /// completes, which a real hijacked <c>/session</c> TCP connection never does, and racing that
    /// against the extra hop of relaying a forwarded response back out through the embedded
    /// connection is a test-harness artifact, not something this assertion needs to risk.
    /// </summary>
    [Fact]
    public async Task Bridges_captures_and_tears_down_idempotently()
    {
        var handle1 = await _sessionBridge.AttachAsync(_cliSession, _link, CancellationToken.None);
        var handle2 = await _sessionBridge.AttachAsync(_cliSession, _link, CancellationToken.None);
        Assert.Same(handle1, handle2);

        _controlService.SkipCliCalls = true;
        handle1.CaptureExporterIds.Add(0);
        var exportTask = handle1.ExportFor(0);

        _controlService.SignalReady();
        await _controlService.DriveCompletedTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(_controlService.DriveError);
        Assert.Equal(1, Volatile.Read(ref _controlService.SessionCallCount));

        // cider-ger.16 regression coverage: the dial's hand-rolled header block, as buildkitd's own
        // real HPACK decoder actually saw it -- one line per advertised method (not one comma-joined
        // line), no comma inside any single value, and the session identity intact.
        //
        // The method-count/no-comma checks below read the raw Kestrel HttpContext.Request.Headers
        // StringValues, not ServerCallContext.RequestHeaders (Metadata): Grpc.AspNetCore.Server's own
        // Metadata construction does `header.Value.ToString()` over that StringValues, which silently
        // re-joins repeated header lines with "," -- exactly the bug this fix removes, but reintroduced
        // one layer up, in the .NET server framework rather than the .NET client one, in a way that
        // would make a real regression (a revert of the LiteralHeadersRewriteStream fix) and a healthy
        // build produce an IDENTICAL single Metadata entry. Real buildkitd (grpc-go) does not do this
        // -- metadata.MD.Append preserves each HPACK-decoded header line as its own slice entry -- so
        // the raw StringValues Kestrel's own HPACK decoder produced (before grpc-dotnet's join) is what
        // is actually faithful to what buildkitd sees, and is asserted here instead.
        var expectedMethods = new HashSet<string>(_cliSession.Methods, StringComparer.Ordinal)
        {
            BuildKitMethods.FileSend.DiffCopy.ToLowerInvariant(),
            BuildKitMethods.Health.Check.ToLowerInvariant(),
        };
        var rawHeaders = _controlService.LastHttpContext!.Request.Headers;
        var methodValues = rawHeaders[BuildKitMethods.MetadataKeys.SessionGrpcMethod];
        Assert.Equal(expectedMethods.Count, methodValues.Count);
        Assert.Equal(expectedMethods, new HashSet<string>(methodValues!, StringComparer.Ordinal));
        foreach (var (_, values) in rawHeaders)
        {
            Assert.All(values, v => Assert.DoesNotContain(',', v!));
        }

        var headers = _controlService.LastHeaders!;
        Assert.Equal(_cliSession.Id, headers.First(h => h.Key == BuildKitMethods.MetadataKeys.SessionUuid).Value);
        Assert.Equal(_cliSession.SharedKey, headers.First(h => h.Key == BuildKitMethods.MetadataKeys.SessionSharedKey).Value);

        // FileSend exporter 0: captured to disk, never reached the CLI.
        var exportResult = await exportTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("a:1", exportResult.Metadata["image.name"]);
        var written = await File.ReadAllBytesAsync(exportResult.TarPath);
        Assert.Equal(_exporter0Payload.Length, written.Length);
        Assert.Equal(_expectedHash, Convert.ToHexString(SHA256.HashData(written)));
        Assert.True(_controlService.Exporter0Ok);
        Assert.False(_cliFileSend.Called);

        // Health/Check answers SERVING.
        Assert.True(_controlService.HealthServing);

        // Releasing one of two references keeps the bridge (and buildkitd's Session call) alive.
        handle1.Release();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(_controlService.SessionEndedTask.IsCompleted);

        // Releasing the last reference tears it down, observed as buildkitd's Session call ending.
        handle2.Release();
        await _controlService.SessionEndedTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, _controlService.SessionCallCount);
    }

    /// <summary>
    /// <see cref="SessionBridge.SelectForwardTarget"/> in isolation, driven straight against
    /// <see cref="_daemon"/>'s session tunnel (no embedded-inside-buildkitd hop, so this is immune to
    /// the CLI-fixture timing hazard noted on <see cref="Bridges_captures_and_tears_down_idempotently"/>):
    /// a captured exporter id is answered locally by <see cref="FileSendCapture"/>; a non-captured
    /// one forwards to <see cref="_cliSession"/>; a method the CLI never advertised answers
    /// Unimplemented. FileSync's own forward-with-metadata-intact is
    /// <see cref="SelectForwardTarget_forwards_FileSync_with_metadata_intact"/>, kept separate for
    /// the same reason the CLI-touching assertions above are consolidated onto one call each: the
    /// fake CLI fixture's synthetic (non-socket) connection only reliably outlives ONE gRPC call.
    /// </summary>
    [Fact]
    public async Task SelectForwardTarget_dispatches_capture_and_forward_correctly()
    {
        var handle = await _sessionBridge.AttachAsync(_cliSession, _link, CancellationToken.None);
        handle.CaptureExporterIds.Add(0);

        var (server, client) = CreateDuplexPair();
        _ = _daemon.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Session, _cliSession.Id);
        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "session-tunnel-test");
        try
        {
            // Captured exporter id 0: answered locally, never reaches the CLI.
            var fileSend = new Moby.Filesync.V1.FileSend.FileSendClient(channel);
            var captureHeaders = new Metadata
            {
                { BuildKitMethods.MetadataKeys.AttachableExporterId, "0" },
                { BuildKitMethods.MetadataKeys.ExporterMetadataPrefix + "image.name", "b:2" },
            };
            using (var captureCall = fileSend.DiffCopy(captureHeaders))
            {
                await captureCall.RequestStream.WriteAsync(new FsBytesMessage { Data = ByteString.CopyFromUtf8("captured-bytes") });
                await captureCall.RequestStream.CompleteAsync();
                var hasMore = await captureCall.ResponseStream.MoveNext(CancellationToken.None);
                Assert.False(hasMore);
                Assert.Equal(StatusCode.OK, captureCall.GetStatus().StatusCode);
            }

            var exportResult = await handle.ExportFor(0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("b:2", exportResult.Metadata["image.name"]);
            Assert.Equal("captured-bytes", await File.ReadAllTextAsync(exportResult.TarPath));
            Assert.False(_cliFileSend.Called);

            // Exporter id 1 is not captured: forwards to the fake CLI session.
            var forwardHeaders = new Metadata { { BuildKitMethods.MetadataKeys.AttachableExporterId, "1" } };
            using (var forwardCall = fileSend.DiffCopy(forwardHeaders))
            {
                await forwardCall.RequestStream.WriteAsync(new FsBytesMessage { Data = ByteString.CopyFromUtf8("hello-1") });
                await forwardCall.RequestStream.CompleteAsync();
                while (await forwardCall.ResponseStream.MoveNext(CancellationToken.None))
                {
                }

                Assert.Equal(StatusCode.OK, forwardCall.GetStatus().StatusCode);
            }

            Assert.True(_cliFileSend.Called);
            Assert.Equal(1, _cliFileSend.LastExporterId);

            // A method the CLI never advertised (Upload/Pull) is Unimplemented, not forwarded.
            var invoker2 = new HttpMessageInvoker(handler, disposeHandler: false);
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "http://session-tunnel-test/moby.upload.v1.Upload/Pull")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent([0, 0, 0, 0, 0]),
            };
            uploadRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
            using var uploadResponse = await invoker2.SendAsync(uploadRequest, CancellationToken.None);
            var status = uploadResponse.Headers.TryGetValues("grpc-status", out var values) ? values.First() : null;
            Assert.Equal("12", status); // StatusCode.Unimplemented
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }

        // Buildkitd's own Session handler (TestControlService.Session) never actually needs to run
        // for this test -- SignalReady is never called, so it just sits blocked on _readyToDrive --
        // this test only cares about SelectForwardTarget's own dispatch decisions on the session
        // tunnel, driven directly rather than through that embedded connection.
        handle.Release();
    }

    /// <summary>
    /// <c>moby.filesync.v1.FileSync/DiffCopy</c> is never a candidate for capture (only FileSend is)
    /// -- it always forwards to <see cref="_cliSession"/>, and its <c>dir-name</c> metadata must
    /// survive the trip intact. Kept separate from
    /// <see cref="SelectForwardTarget_dispatches_capture_and_forward_correctly"/> for the same
    /// one-call-per-fake-CLI-connection reason documented there.
    /// </summary>
    [Fact]
    public async Task SelectForwardTarget_forwards_FileSync_with_metadata_intact()
    {
        var handle = await _sessionBridge.AttachAsync(_cliSession, _link, CancellationToken.None);

        var (server, client) = CreateDuplexPair();
        _ = _daemon.Services.GetRequiredService<TunnelTransport>().ServeAsync(server, TunnelKind.Session, _cliSession.Id);
        var (channel, invoker, handler) = StreamHttp2Client.Create(client, "session-tunnel-filesync-test");
        try
        {
            var fileSync = new Moby.Filesync.V1.FileSync.FileSyncClient(channel);
            var syncHeaders = new Metadata { { "dir-name", "context" } };
            using var syncCall = fileSync.DiffCopy(syncHeaders);
            await syncCall.RequestStream.WriteAsync(new FsPacket { Data = ByteString.CopyFromUtf8("ctx") });
            await syncCall.RequestStream.CompleteAsync();

            var echoed = 0;
            while (await syncCall.ResponseStream.MoveNext(CancellationToken.None))
            {
                echoed++;
            }

            Assert.Equal(1, echoed);
            Assert.Equal(StatusCode.OK, syncCall.GetStatus().StatusCode);
        }
        finally
        {
            invoker.Dispose();
            handler.Dispose();
        }

        Assert.True(_cliFileSync.Called);
        Assert.Contains(_cliFileSync.LastHeaders!, h => h.Key == "dir-name" && h.Value == "context");

        handle.Release();
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
            // See DaemonHost.ConfigureServices: without this, grpc-dotnet maps its own catch-all
            // "service unimplemented" endpoint at routing Order 0 the moment any MapGrpcService is
            // registered on the app -- beating MapGrpcForwarder's Fallback for every service that
            // was not explicitly mapped, exactly the FileSend/FileSync methods this test forwards.
            grpc.IgnoreUnknownServices = true;
            grpc.MaxReceiveMessageSize = null;
            grpc.MaxSendMessageSize = null;
        });
        configureServices(builder.Services);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Mirrors DaemonHost.ConfigureKestrel: a TunnelConnectionContext has no real socket
            // behind it (LocalEndPoint/RemoteEndPoint stay null), so Kestrel's throughput-based
            // timeouts have nothing meaningful to measure on this transport.
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
    /// <c>buildctl dial-stdio</c>, instead of touching <see cref="IContainerRuntime"/> at all.
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

    /// <summary>A minimal <see cref="IContainerProcess"/> good enough to back a directly-constructed <see cref="BuilderLink"/> in tests.</summary>
    private sealed class NoopProcess : IContainerProcess
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

    /// <summary>
    /// The fake CLI's FileSync echo: records the request headers and whether it was ever called, so
    /// the test can assert <c>dir-name: context</c> reached it intact through the bridge's forward
    /// path.
    /// </summary>
    private sealed class TestCliFileSyncService : Moby.Filesync.V1.FileSync.FileSyncBase
    {
        public bool Called { get; private set; }

        public Metadata? LastHeaders { get; private set; }

        public override async Task DiffCopy(
            IAsyncStreamReader<FsPacket> requestStream, IServerStreamWriter<FsPacket> responseStream, ServerCallContext context)
        {
            Called = true;
            LastHeaders = context.RequestHeaders;

            await foreach (var packet in requestStream.ReadAllAsync())
            {
                await responseStream.WriteAsync(packet);
            }
        }
    }

    /// <summary>
    /// The fake CLI's FileSend echo: only ever reached for the exporter id the bridge does NOT
    /// capture (id 1 in this test) -- id 0 is answered locally by <see cref="FileSendCapture"/> and
    /// must never arrive here.
    /// </summary>
    private sealed class TestCliFileSendService : Moby.Filesync.V1.FileSend.FileSendBase
    {
        public bool Called { get; private set; }

        public int? LastExporterId { get; private set; }

        public override async Task DiffCopy(
            IAsyncStreamReader<FsBytesMessage> requestStream, IServerStreamWriter<FsBytesMessage> responseStream, ServerCallContext context)
        {
            Called = true;
            var header = context.RequestHeaders.FirstOrDefault(h => h.Key == BuildKitMethods.MetadataKeys.AttachableExporterId);
            LastExporterId = header is not null ? int.Parse(header.Value) : 0;

            await foreach (var message in requestStream.ReadAllAsync())
            {
                await responseStream.WriteAsync(message);
            }
        }
    }

    /// <summary>
    /// The fake buildkitd: implements only <c>Session</c>, which -- exactly like the real thing --
    /// treats the bidi <see cref="BytesMessage"/> stream as an embedded HTTP/2 connection it dials as
    /// the *client* (<see cref="StreamHttp2Client"/>), then drives the calls this test cares about.
    /// Blocks on <see cref="_readyToDrive"/> first so the test can arm
    /// <see cref="SessionBridgeHandle.CaptureExporterIds"/> before the exporter-0 call can possibly
    /// arrive, and on <see cref="ServerBytesMessageStream.Eof"/> last, so the RPC itself only
    /// completes once the daemon side tears the bridge down -- letting the test observe that
    /// teardown as this call finally ending.
    /// </summary>
    private sealed class TestControlService(byte[] exporter0Payload) : Control.ControlBase
    {
        private const int WriteChunkBytes = 3 * 1024 * 1024;

        private readonly TaskCompletionSource _readyToDrive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _driveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SessionCallCount;

        public bool Exporter0Ok { get; private set; }

        public bool Exporter1Ok { get; private set; }

        public bool HealthServing { get; private set; }

        public bool FileSyncOk { get; private set; }

        public Exception? DriveError { get; private set; }

        public Task DriveCompletedTask => _driveCompleted.Task;

        public Task SessionEndedTask => _sessionEnded.Task;

        /// <summary>
        /// The request headers this <c>Session</c> call arrived with, as
        /// <c>ServerCallContext.RequestHeaders</c> exposes them -- fine for single-valued entries
        /// (<c>SessionUuid</c>, <c>SessionSharedKey</c>), but NOT where a reverted cider-ger.16 fix
        /// would show up: <see cref="Grpc.AspNetCore.Server"/>'s own <c>Metadata</c> construction
        /// comma-joins repeated header lines before this property could ever see them separately, so
        /// a genuine N-separate-lines advertisement and the old comma-joined-by-the-client bug look
        /// identical here. See <see cref="LastHttpContext"/> for the property that actually
        /// distinguishes them.
        /// </summary>
        public Metadata? LastHeaders { get; private set; }

        /// <summary>
        /// The raw ASP.NET Core <see cref="HttpContext"/> for this <c>Session</c> call -- i.e. exactly
        /// what <see cref="SessionBridge.OpenAsync"/>'s <see cref="LiteralHeadersRewriteStream"/> put
        /// on the wire, decoded by Kestrel's real HPACK decoder (cider-ger.16 regression coverage: a
        /// reverted-to-<c>Metadata</c> advertisement would comma-join every
        /// <c>x-docker-expose-session-grpc-method</c> value into one useless line instead of N
        /// separate ones -- <c>Request.Headers[...]</c>'s raw <c>StringValues</c>, read before
        /// grpc-dotnet's own <see cref="LastHeaders"/> conversion collapses that distinction, is where
        /// that actually shows up).
        /// </summary>
        public HttpContext? LastHttpContext { get; private set; }

        public void SignalReady() => _readyToDrive.TrySetResult();

        public override async Task Session(
            IAsyncStreamReader<BytesMessage> requestStream, IServerStreamWriter<BytesMessage> responseStream, ServerCallContext context)
        {
            Interlocked.Increment(ref SessionCallCount);
            LastHeaders = context.RequestHeaders;
            LastHttpContext = context.GetHttpContext();

            var stream = new ServerBytesMessageStream(requestStream, responseStream);
            var (channel, invoker, handler) = StreamHttp2Client.Create(stream, $"embedded-{Guid.NewGuid():n}");
            try
            {
                // Tied to context.CancellationToken so a test that never calls SignalReady (this
                // handler simply is not needed for what it wants to prove) does not leave this call
                // hanging until DisposeAsync's own StopAsync grace period times out -- it unblocks
                // (with OperationCanceledException, ignored below) the moment the app starts stopping.
                await _readyToDrive.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await DriveAsync(channel).ConfigureAwait(false);
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
                _sessionEnded.TrySetResult();
            }
        }

        /// <summary>
        /// When set, skips the FileSend exporter-1 forward and the FileSync forward -- both drive
        /// calls through the fake CLI session, which <see cref="Bridges_captures_and_tears_down_idempotently"/>
        /// deliberately avoids (see that test's own doc comment for why).
        /// </summary>
        public bool SkipCliCalls { get; set; }

        private async Task DriveAsync(Grpc.Net.Client.GrpcChannel channel)
        {
            var fileSend = new Moby.Filesync.V1.FileSend.FileSendClient(channel);

            var headers0 = new Metadata
            {
                { BuildKitMethods.MetadataKeys.AttachableExporterId, "0" },
                { BuildKitMethods.MetadataKeys.ExporterMetadataPrefix + "image.name", "a:1" },
            };
            using (var call0 = fileSend.DiffCopy(headers0))
            {
                for (var offset = 0; offset < exporter0Payload.Length; offset += WriteChunkBytes)
                {
                    var length = Math.Min(WriteChunkBytes, exporter0Payload.Length - offset);
                    await call0.RequestStream.WriteAsync(
                        new FsBytesMessage { Data = ByteString.CopyFrom(exporter0Payload, offset, length) });
                }

                await call0.RequestStream.CompleteAsync();
                var hasMore0 = await call0.ResponseStream.MoveNext(CancellationToken.None);
                Exporter0Ok = !hasMore0 && call0.GetStatus().StatusCode == StatusCode.OK;
            }

            if (!SkipCliCalls)
            {
                var headers1 = new Metadata { { BuildKitMethods.MetadataKeys.AttachableExporterId, "1" } };
                using (var call1 = fileSend.DiffCopy(headers1))
                {
                    await call1.RequestStream.WriteAsync(new FsBytesMessage { Data = ByteString.CopyFromUtf8("hello-1") });
                    await call1.RequestStream.CompleteAsync();
                    while (await call1.ResponseStream.MoveNext(CancellationToken.None))
                    {
                    }

                    Exporter1Ok = call1.GetStatus().StatusCode == StatusCode.OK;
                }

                var fileSync = new Moby.Filesync.V1.FileSync.FileSyncClient(channel);
                var syncHeaders = new Metadata { { "dir-name", "context" } };
                using var syncCall = fileSync.DiffCopy(syncHeaders);
                await syncCall.RequestStream.WriteAsync(new FsPacket { Data = ByteString.CopyFromUtf8("ctx") });
                await syncCall.RequestStream.CompleteAsync();
                FileSyncOk = await syncCall.ResponseStream.MoveNext(CancellationToken.None);
            }

            var health = new Health.HealthClient(channel);
            var healthResponse = await health.CheckAsync(new HealthCheckRequest()).ResponseAsync;
            HealthServing = healthResponse.Status == HealthCheckResponse.Types.ServingStatus.Serving;
        }
    }

    /// <summary>
    /// The buildkitd-side mirror of <see cref="Cider.Daemon.BuildKit.BytesMessageStream"/>: wraps a
    /// gRPC server's own bidi <see cref="BytesMessage"/> stream (reader = what the daemon wrote,
    /// writer = what buildkitd writes back) as a plain duplex <see cref="Stream"/> for
    /// <see cref="StreamHttp2Client"/> to dial as the embedded connection's h2 client, and exposes
    /// <see cref="Eof"/> so the test can tell when the daemon completed its side (the signal
    /// <see cref="TestControlService.Session"/> waits on before letting the RPC itself end).
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
