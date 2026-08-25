using Cider.Core.Configuration;
using Cider.Core.Runtime;
using Cider.Daemon.Tunnel;
using Grpc.Core;
using Grpc.Net.Client;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Owns the one long-lived link to buildkitd inside Apple's builder VM: dials
/// <c>container exec -i buildkit buildctl dial-stdio</c> through
/// <see cref="IContainerRuntime.DialBuilderAsync"/>, starting the builder first when it is not
/// running, wraps the exec pipe's stdio in an HTTP/2 gRPC channel
/// (<see cref="StreamHttp2Client"/>), and hands out the resulting <see cref="BuilderLink"/> to every
/// caller until it dies, stalls, or is explicitly invalidated — at which point the next
/// <see cref="GetAsync"/> dials a fresh one.
/// <para>
/// Recovery: a link that failed twice within <see cref="RelinkWindow"/>, or was invalidated for a
/// stall, gets <see cref="IContainerRuntime.StartBuilderAsync"/> run again before the next dial —
/// clearing a poisoned exec, per the probe finding that a stalled exec on this CLI keeps failing
/// every later exec until the builder is restarted.
/// </para>
/// </summary>
public sealed class BuilderConnection : IBuilderConnection, IAsyncDisposable
{
    /// <summary>Bounds only bytes buildkitd sends us; what we may send it goes through <see cref="TokenBucketPacer"/>.</summary>
    private const int InitialStreamWindowBytes = 256 * 1024;

    private static readonly TimeSpan LivenessProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Planner ruling (cider-ger.8, comment #10): ship this conservative default and tune from later measurements.</summary>
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultWatchdogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Two failures of the same link inside this window force a builder restart before the next dial.</summary>
    private static readonly TimeSpan RelinkWindow = TimeSpan.FromSeconds(30);

    private readonly IContainerRuntime _runtime;
    private readonly CiderOptions _options;
    private readonly ILogger<BuilderConnection> _logger;
    private readonly TimeSpan _stallThreshold;
    private readonly SemaphoreSlim _dialGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _watchdogCts = new();
    private readonly Task _watchdogTask;

    private BuilderLink? _current;
    private DateTime? _lastFailureUtc;
    private bool _forceRestartNextDial;
    private int _disposed;

    public BuilderConnection(IContainerRuntime runtime, CiderOptions options, ILogger<BuilderConnection> logger)
        : this(runtime, options, logger, DefaultStallThreshold, DefaultWatchdogInterval)
    {
    }

    /// <summary>Test seam: a shortened stall threshold/watchdog poll interval so a stall test does not need to wait 60 s.</summary>
    internal BuilderConnection(
        IContainerRuntime runtime,
        CiderOptions options,
        ILogger<BuilderConnection> logger,
        TimeSpan stallThreshold,
        TimeSpan watchdogInterval)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runtime = runtime;
        _options = options;
        _logger = logger;
        _stallThreshold = stallThreshold;
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(watchdogInterval, _watchdogCts.Token));
    }

    /// <inheritdoc />
    public async ValueTask<BuilderLink> GetAsync(CancellationToken cancellationToken)
    {
        if (!_options.BuildKitEnabled)
        {
            throw new BuilderUnavailableException("cider: BuildKit is disabled (builder.enabled=false)");
        }

        await _dialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = ReadCurrent();
            if (existing is not null && IsUsable(existing))
            {
                return existing;
            }

            if (existing is not null)
            {
                ClearCurrentIfSame(existing);
                await DisposeQuietlyAsync(existing).ConfigureAwait(false);
            }

            var link = await DialNewLinkAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                _current = link;
            }

            return link;
        }
        finally
        {
            _dialGate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(BuilderLink link, Exception? reason) => InvalidateCore(link, reason, forceRestart: false);

    /// <summary>Disposes the current link (if any) and stops the stall watchdog. The builder VM itself keeps running.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _watchdogCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _watchdogTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _watchdogCts.Dispose();
        _dialGate.Dispose();

        BuilderLink? current;
        lock (_stateLock)
        {
            current = _current;
            _current = null;
        }

        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private BuilderLink? ReadCurrent()
    {
        lock (_stateLock)
        {
            return _current;
        }
    }

    private void ClearCurrentIfSame(BuilderLink link)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_current, link))
            {
                _current = null;
            }
        }
    }

    private bool IsUsable(BuilderLink link) => !link.Exited.IsCompleted && !link.Tracker.IsStalled(_stallThreshold);

    private async Task<BuilderLink> DialNewLinkAsync(CancellationToken cancellationToken)
    {
        bool forceRestart;
        lock (_stateLock)
        {
            forceRestart = _forceRestartNextDial;
            _forceRestartNextDial = false;
        }

        var status = await _runtime.GetBuilderStatusAsync(cancellationToken).ConfigureAwait(false);
        if (forceRestart || status is not { Running: true })
        {
            _logger.LogInformation("starting the Apple builder VM");
            await _runtime.StartBuilderAsync(_options.BuilderCpus, _options.BuilderMemoryBytes, cancellationToken).ConfigureAwait(false);
        }

        IContainerProcess process;
        try
        {
            process = await _runtime.DialBuilderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            throw new BuilderUnavailableException($"cider: cannot dial buildctl in the Apple builder: {ex.Message}", ex);
        }

        try
        {
            return await EstablishLinkAsync(process, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<BuilderLink> EstablishLinkAsync(IContainerProcess process, CancellationToken cancellationToken)
    {
        if (process.Stdin is not { } stdin)
        {
            throw new BuilderUnavailableException("cider: the builder dial did not open stdin");
        }

        _ = DrainStderrAsync(process.Stderr, _logger);

        var duplex = new DuplexStream(process.Stdout, stdin);
        var (channel, invoker, handler) = StreamHttp2Client.Create(duplex, "buildkit", InitialStreamWindowBytes);

        var tracker = new BuilderLinkTracker();
        var pacer = new TokenBucketPacer(tracker: tracker);
        var trackingCallInvoker = new ActivityTrackingCallInvoker(channel.CreateCallInvoker(), tracker);

        BuilderLink? linkBox = null;
        var target = new ForwardTarget
        {
            Invoker = invoker,
            Authority = "buildkit",
            Pacer = pacer,
            OnFailure = ex =>
            {
                // Not every forwarding failure is the link's fault (a client cancellation or an
                // ordinary application-level RpcException from a real build error is not); only the
                // transport-shaped ones here mean the exec pipe itself is bad. forceRestart is
                // always false from this path -- InvalidateCore's own repeat-within-window check
                // decides that; only a stall (the watchdog, below) forces it unconditionally.
                if (linkBox is { } failed && IsLinkFailure(ex))
                {
                    InvalidateCore(failed, ex, forceRestart: false);
                }
            },
        };

        var link = new BuilderLink(channel, invoker, trackingCallInvoker, target, tracker, process, handler);
        linkBox = link;

        try
        {
            var probe = new Control.ControlClient(trackingCallInvoker);
            await probe.InfoAsync(
                new InfoRequest(),
                deadline: DateTime.UtcNow.Add(LivenessProbeTimeout),
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException or OperationCanceledException)
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw new BuilderUnavailableException($"cider: cannot reach buildkitd in the Apple builder: {ex.Message}", ex);
        }

        return link;
    }

    private void InvalidateCore(BuilderLink link, Exception? reason, bool forceRestart)
    {
        ArgumentNullException.ThrowIfNull(link);

        bool wasCurrent;
        lock (_stateLock)
        {
            wasCurrent = ReferenceEquals(_current, link);
            if (wasCurrent)
            {
                _current = null;

                var now = DateTime.UtcNow;
                if (forceRestart || (_lastFailureUtc is { } previous && now - previous <= RelinkWindow))
                {
                    _forceRestartNextDial = true;
                }

                _lastFailureUtc = now;
            }
        }

        if (!wasCurrent)
        {
            return;
        }

        if (reason is null)
        {
            _logger.LogWarning("invalidating the builder link: no progress for {Threshold}", _stallThreshold);
        }
        else
        {
            _logger.LogWarning(reason, "invalidating the builder link");
        }

        _ = DisposeQuietlyAsync(link);
    }

    private async Task WatchdogLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                var current = ReadCurrent();
                if (current is not null && current.Tracker.IsStalled(_stallThreshold))
                {
                    InvalidateCore(current, reason: null, forceRestart: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> means the exec pipe itself is bad -- worth tearing the link
    /// down for -- as opposed to an ordinary application-level failure (ex.: a real build error
    /// surfaced through the forwarder as an RpcException with some other status) or a client-side
    /// cancellation, neither of which say anything about the link's health.
    /// </summary>
    private static bool IsLinkFailure(Exception ex) => ex switch
    {
        RpcException { StatusCode: StatusCode.Unavailable or StatusCode.Internal } => true,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };

    private static async Task DrainStderrAsync(Stream? stderr, ILogger logger)
    {
        if (stderr is null)
        {
            return;
        }

        var reader = new StreamReader(stderr);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                logger.LogDebug("buildctl dial-stdio: {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task DisposeQuietlyAsync(BuilderLink link)
    {
        try
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
