using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using Cider.Core.Configuration;
using Cider.Core.Runtime;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moby.Buildkit.V1;
using Xunit;

namespace Cider.Tests.Daemon.BuildKit;

/// <summary>
/// <see cref="BuilderConnection"/> end to end, exactly cider-ger.8's verification section: a fake
/// <see cref="IContainerRuntime"/> whose <c>DialBuilderAsync</c> hands back an in-memory duplex
/// process wired to a real <see cref="Moby.Buildkit.V1.Control.ControlBase"/> served over
/// <see cref="TunnelTransport"/> (the same "T1 scaffolding" <see cref="GrpcForwarderTests"/> uses) --
/// proving <c>GetAsync</c> starts the builder, dials it, probes it with <c>Control/Info</c>, redials
/// after the dial process dies, and recovers from a stalled call by invalidating the link and
/// restarting the builder before the next dial.
/// </summary>
public sealed class BuilderConnectionTests : IAsyncLifetime
{
    private WebApplication _backend = null!;
    private TunnelTransport _backendTransport = null!;

    public async Task InitializeAsync()
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

        _backend = builder.Build();
        _backend.MapGrpcService<TestControlService>();
        await _backend.StartAsync();
        _backendTransport = _backend.Services.GetRequiredService<TunnelTransport>();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _backend.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        await _backend.DisposeAsync();
    }

    [Fact]
    public async Task GetAsync_starts_the_builder_when_stopped_and_dials_with_the_configured_resources()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true, BuilderCpus = 4, BuilderMemoryBytes = 6L * 1024 * 1024 * 1024 };
        await using var connection = new BuilderConnection(runtime, options, NullLogger<BuilderConnection>.Instance);

        Assert.Null(runtime.Status); // nothing running yet

        var link = await connection.GetAsync(CancellationToken.None);

        Assert.Equal(1, runtime.StartBuilderCalls);
        Assert.Equal((4, 6L * 1024 * 1024 * 1024), runtime.LastStartBuilderArgs);
        Assert.Equal(1, runtime.DialCount);
        Assert.NotNull(link);
    }

    [Fact]
    public async Task The_links_CallInvoker_completes_a_unary_call()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true };
        await using var connection = new BuilderConnection(runtime, options, NullLogger<BuilderConnection>.Instance);

        var link = await connection.GetAsync(CancellationToken.None);
        var client = new Control.ControlClient(link.CallInvoker);

        var response = await client.InfoAsync(new InfoRequest(), deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync;

        Assert.NotNull(response);
    }

    [Fact]
    public async Task A_dead_dial_process_causes_the_next_GetAsync_to_redial()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true };
        await using var connection = new BuilderConnection(runtime, options, NullLogger<BuilderConnection>.Instance);

        var first = await connection.GetAsync(CancellationToken.None);
        Assert.Equal(1, runtime.DialCount);

        runtime.Dials[0].SimulateExit(0);
        await first.Exited.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await connection.GetAsync(CancellationToken.None);

        Assert.Equal(2, runtime.DialCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task A_stalled_call_invalidates_the_link_and_forces_a_builder_restart_on_the_next_dial()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true };
        await using var connection = new BuilderConnection(
            runtime,
            options,
            NullLogger<BuilderConnection>.Instance,
            stallThreshold: TimeSpan.FromMilliseconds(150),
            watchdogInterval: TimeSpan.FromMilliseconds(30));

        var link = await connection.GetAsync(CancellationToken.None);
        Assert.Equal(1, runtime.StartBuilderCalls);

        // Opens a call that the backend never answers (see TestControlService.DiskUsage) so the
        // link's activity tracker sees an open call with no further progress -- exactly the "fake
        // stream stops responding while a call is open" shape the watchdog exists for.
        var hangingClient = new Control.ControlClient(link.CallInvoker);
        var hangingCall = hangingClient.DiskUsageAsync(new DiskUsageRequest());
        _ = Task.Run(async () =>
        {
            try
            {
                await hangingCall.ResponseAsync;
            }
            catch (Exception)
            {
                // Expected once the stalled link is disposed out from under this call; the test
                // only cares that GetAsync redials afterwards.
            }
        });

        // Long enough for the watchdog (30 ms interval, 150 ms threshold) to notice and invalidate.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var relinked = await connection.GetAsync(CancellationToken.None);

        Assert.Equal(2, runtime.DialCount);
        Assert.Equal(2, runtime.StartBuilderCalls);
        Assert.NotSame(link, relinked);
    }

    /// <summary>
    /// The same stall shape as
    /// <see cref="A_stalled_call_invalidates_the_link_and_forces_a_builder_restart_on_the_next_dial"/>,
    /// but driven straight through <see cref="BuilderLink.Target"/>'s
    /// <see cref="ForwardTarget.Invoker"/> -- what <see cref="GrpcForwarder"/>'s raw byte forwarding
    /// actually calls -- rather than <see cref="BuilderLink.CallInvoker"/>. Proves the tracking
    /// wrapper installed on <see cref="ForwardTarget.Invoker"/> (not just
    /// <c>ActivityTrackingCallInvoker</c> on <see cref="BuilderLink.CallInvoker"/>) reports open calls
    /// into <see cref="BuilderLink.Tracker"/>, so a forwarded call the backend never answers (see
    /// <see cref="TestControlService.DiskUsage"/>) is just as visible to the stall watchdog.
    /// </summary>
    [Fact]
    public async Task A_stalled_call_through_Target_Invoker_invalidates_the_link_and_forces_a_builder_restart_on_the_next_dial()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true };
        await using var connection = new BuilderConnection(
            runtime,
            options,
            NullLogger<BuilderConnection>.Instance,
            stallThreshold: TimeSpan.FromMilliseconds(150),
            watchdogInterval: TimeSpan.FromMilliseconds(30));

        var link = await connection.GetAsync(CancellationToken.None);
        Assert.Equal(1, runtime.StartBuilderCalls);

        // DiskUsage (TestControlService, below) never completes, so the backend never even gets to
        // send response headers -- unlike a forwarded call whose target answers normally, this one
        // makes SendAsync itself hang until something else (here: the watchdog invalidating the
        // link out from under it) ends it. BeginCall runs synchronously at the top of the tracking
        // handler, before any of that network waiting starts, so the send is kicked off in the
        // background and its effect on the tracker observed independently of when/how it ends --
        // exactly the "fake stream stops responding while a call is open" shape the watchdog exists
        // for, this time on the path GrpcForwarder's raw byte forwarding actually uses
        // (Target.Invoker) rather than link.CallInvoker.
        using var request = BuildGrpcRequest(link.Target.Authority, "DiskUsage", new DiskUsageRequest());
        var sendTask = link.Target.Invoker.SendAsync(request, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (link.Tracker.ActiveCalls != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(1, link.Tracker.ActiveCalls);

        // Long enough for the watchdog (30 ms interval, 150 ms threshold) to notice and invalidate.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var relinked = await connection.GetAsync(CancellationToken.None);

        Assert.Equal(2, runtime.DialCount);
        Assert.Equal(2, runtime.StartBuilderCalls);
        Assert.NotSame(link, relinked);

        try
        {
            using var response = await sendTask;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // Expected once the stalled link is disposed out from under this call; the test only
            // cares that GetAsync redialed and restarted the builder.
        }
    }

    /// <summary>
    /// Reads through <see cref="BuilderLink.Target"/>'s <see cref="ForwardTarget.Invoker"/> the same
    /// way <see cref="GrpcForwarder"/>'s <c>CopyResponseBodyAsync</c> does, proving each successful
    /// read of the response body bumps <see cref="BuilderLinkTracker.LastProgress"/> -- the other half
    /// of what makes forwarded traffic (not just <see cref="BuilderLink.CallInvoker"/> traffic)
    /// visible to the stall watchdog as still making progress.
    /// </summary>
    [Fact]
    public async Task Downstream_reads_through_Target_Invoker_advance_the_trackers_LastProgress()
    {
        var runtime = new FakeBuilderRuntime(_backendTransport);
        var options = new CiderOptions { BuildKitEnabled = true };
        await using var connection = new BuilderConnection(runtime, options, NullLogger<BuilderConnection>.Instance);

        var link = await connection.GetAsync(CancellationToken.None);

        using var request = BuildGrpcRequest(link.Target.Authority, "Info", new InfoRequest());
        using var response = await link.Target.Invoker.SendAsync(request, CancellationToken.None);

        var beforeRead = link.Tracker.LastProgress;

        // Give the clock room to move before reading, so a later LastProgress can only be explained
        // by the read itself, not by BeginCall's own initial bump (BuilderLinkTracker.BeginCall,
        // called before the request was even sent).
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        var buffer = new byte[256];
        int read;
        do
        {
            read = await stream.ReadAsync(buffer.AsMemory());
        }
        while (read > 0);

        Assert.True(link.Tracker.LastProgress > beforeRead);
    }

    /// <summary>
    /// Builds a raw HTTP/2 gRPC request for <c>moby.buildkit.v1.Control/{method}</c> -- the same
    /// shape <see cref="GrpcForwarder"/>'s private <c>BuildRequest</c> constructs -- so a test can
    /// drive a call straight through <see cref="BuilderLink.Target"/>'s
    /// <see cref="ForwardTarget.Invoker"/> without needing a whole forwarding HTTP context.
    /// </summary>
    private static HttpRequestMessage BuildGrpcRequest(string authority, string method, IMessage request)
    {
        var payload = request.ToByteArray();
        var frame = new byte[5 + payload.Length];
        frame[0] = 0; // uncompressed
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));

        var message = new HttpRequestMessage(HttpMethod.Post, $"http://{authority}/moby.buildkit.v1.Control/{method}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(frame),
        };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
        message.Headers.TryAddWithoutValidation("te", "trailers");
        return message;
    }

    /// <summary>
    /// A minimal <see cref="IContainerRuntime"/> covering only the builder seam
    /// <see cref="BuilderConnection"/> calls: everything else throws, since nothing under test
    /// touches it. Each <see cref="DialBuilderAsync"/> hands back a fresh in-memory duplex process
    /// whose server half is served by the fixture's <see cref="TunnelTransport"/> against
    /// <see cref="TestControlService"/>, mirroring what <c>Cider.Tests.Fakes.FakeContainerRuntime</c>
    /// (T4) exposes for the builder VM without needing its shell-interpreting <c>FakeProcess</c>,
    /// which cannot pass raw HTTP/2 bytes through <c>dial-stdio</c>.
    /// </summary>
    private sealed class FakeBuilderRuntime(TunnelTransport transport) : IContainerRuntime
    {
        public BuilderStatus? Status { get; private set; }

        public int StartBuilderCalls { get; private set; }

        public (int? Cpus, long? MemoryBytes)? LastStartBuilderArgs { get; private set; }

        public int DialCount { get; private set; }

        public List<FakeDialProcess> Dials { get; } = [];

        public Task<BuilderStatus?> GetBuilderStatusAsync(CancellationToken ct) => Task.FromResult(Status);

        public Task StartBuilderAsync(int? cpus, long? memoryBytes, CancellationToken ct)
        {
            StartBuilderCalls++;
            LastStartBuilderArgs = (cpus, memoryBytes);
            Status = new BuilderStatus { Name = "buildkit", Running = true, Cpus = cpus, MemoryBytes = memoryBytes };
            return Task.CompletedTask;
        }

        public Task<IContainerProcess> DialBuilderAsync(CancellationToken ct)
        {
            DialCount++;

            var toServer = new Pipe();
            var toClient = new Pipe();

            var process = new FakeDialProcess(toServer.Writer.AsStream(), toClient.Reader.AsStream());
            Dials.Add(process);

            var serverSide = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
            _ = transport.ServeAsync(serverSide, TunnelKind.Control);

            return Task.FromResult<IContainerProcess>(process);
        }

        public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task EnsureReadyAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task CreateContainerAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task<IContainerProcess> StartContainerAsync(string runtimeId, StartOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task StopContainerAsync(string runtimeId, int? timeoutSeconds, string? signal, CancellationToken ct) => throw new NotSupportedException();

        public Task KillContainerAsync(string runtimeId, string signal, CancellationToken ct) => throw new NotSupportedException();

        public Task RemoveContainerAsync(string runtimeId, bool force, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeContainer?> InspectContainerAsync(string runtimeId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IContainerProcess> ExecAsync(string runtimeId, ExecSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> OpenLogsAsync(string runtimeId, bool follow, int? tail, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeStats?> GetStatsAsync(string runtimeId, CancellationToken ct) => throw new NotSupportedException();

        public Task CopyFromContainerAsync(string runtimeId, string containerPath, string localDestinationDir, CancellationToken ct) => throw new NotSupportedException();

        public Task CopyToContainerAsync(string runtimeId, string localSourcePath, string containerPath, CancellationToken ct) => throw new NotSupportedException();

        public Task ExportContainerAsync(string runtimeId, Stream tarOutput, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeImage>> ListImagesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeImageDetail?> InspectImageAsync(string reference, CancellationToken ct) => throw new NotSupportedException();

        public Task PullImageAsync(string reference, string? platform, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct) => throw new NotSupportedException();

        public Task PushImageAsync(string reference, RegistryAuth? auth, IProgress<ProgressEvent> progress, CancellationToken ct) => throw new NotSupportedException();

        public Task TagImageAsync(string sourceReference, string targetReference, CancellationToken ct) => throw new NotSupportedException();

        public Task RemoveImageAsync(string reference, bool force, CancellationToken ct) => throw new NotSupportedException();

        public Task SaveImagesAsync(IReadOnlyList<string> references, Stream tarOutput, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> LoadImagesAsync(Stream tarInput, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> BuildImageAsync(BuildSpec spec, IProgress<ProgressEvent> progress, CancellationToken ct) => throw new NotSupportedException();

        public Task LoginAsync(RegistryAuth auth, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeNetwork>> ListNetworksAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeNetwork?> InspectNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateNetworkAsync(NetworkSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeVolume>> ListVolumesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeVolume?> InspectVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateVolumeAsync(VolumeSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task RemoveVolumeAsync(string name, bool force, CancellationToken ct) => throw new NotSupportedException();

        public Task<RuntimeDiskUsage> GetDiskUsageAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// A raw duplex-passthrough <see cref="IContainerProcess"/>: stdin/stdout are plain
    /// <see cref="Pipe"/>-backed streams (no shell interpreter in the way, unlike
    /// <c>Cider.Tests.Fakes.FakeProcess</c>), stderr is already at EOF, and <see cref="Exited"/> is
    /// only ever completed by <see cref="SimulateExit"/> or disposal -- exactly what
    /// <c>buildctl dial-stdio</c> looks like from the daemon's side.
    /// </summary>
    private sealed class FakeDialProcess(Stream stdin, Stream stdout) : IContainerProcess
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public int? Pid => 4242;

        public bool HasTty => false;

        public Stream? Stdin { get; } = stdin;

        public Stream Stdout { get; } = stdout;

        public Stream? Stderr { get; } = new MemoryStream();

        public Task<int> Exited => _exited.Task;

        public void SimulateExit(int exitCode) => _exited.TrySetResult(exitCode);

        public Task CloseStdinAsync() => Task.CompletedTask;

        public Task ResizeAsync(int cols, int rows, CancellationToken ct) => Task.CompletedTask;

        public Task KillAsync(string signal, CancellationToken ct)
        {
            _exited.TrySetResult(137);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _exited.TrySetResult(-1);
            await Stdin!.DisposeAsync();
            await Stdout.DisposeAsync();
        }
    }

    /// <summary>
    /// A hand-rolled <see cref="Control.ControlBase"/> answering exactly what
    /// <see cref="BuilderConnection"/> needs: <see cref="Info"/> for the liveness probe, and
    /// <see cref="DiskUsage"/> that hangs until the call is cancelled -- the "fake stream stops
    /// responding while a call is open" shape the stall test drives.
    /// </summary>
    private sealed class TestControlService : Control.ControlBase
    {
        public override Task<InfoResponse> Info(InfoRequest request, ServerCallContext context) =>
            Task.FromResult(new InfoResponse());

        public override async Task<DiskUsageResponse> DiskUsage(DiskUsageRequest request, ServerCallContext context)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return new DiskUsageResponse();
        }
    }
}
