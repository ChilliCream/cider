using System.Collections.Concurrent;
using System.Net.Http;
using Cider.Core.Configuration;
using Cider.Daemon.Tunnel;
using Grpc.Core;
using Grpc.Net.Client;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Owns the daemon's own <c>Control/Session</c> connection into buildkitd — one per
/// <see cref="CliSession"/>, dialed through <see cref="BuilderLink.CallInvoker"/> and re-served to
/// Kestrel's HTTP/2 engine over <see cref="TunnelTransport"/> as <see cref="TunnelKind.Session"/>, so
/// buildkitd's own callbacks (filesync, auth, secrets, ssh-forward, upload, the health check, and —
/// via <see cref="FileSendCapture"/> — the moby exporter's output) land somewhere this proxy answers
/// or relays, exactly what cider-ger.9 exists for. See that task's problem statement for the wire
/// protocol this bridges.
/// <para>
/// <see cref="AttachAsync"/> is idempotent per <see cref="CliSession.Id"/>: whichever caller (a
/// Bake's Control/Session handler, a Solve handler — both future, T7) asks first dials buildkitd;
/// every later caller for the same id gets the same <see cref="SessionBridgeHandle"/>, ref-counted,
/// and buildkitd never sees a second <c>Session</c> call for a session it already has one open for.
/// </para>
/// </summary>
public sealed class SessionBridge
{
    private readonly TunnelTransport _tunnel;
    private readonly CiderOptions _options;
    private readonly IRawSessionDialer _rawDialer;
    private readonly ILogger<SessionBridge> _logger;
    private readonly SemaphoreSlim _attachGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SessionBridgeHandle> _handles = new(StringComparer.Ordinal);

    public SessionBridge(TunnelTransport tunnel, CiderOptions options, IRawSessionDialer rawDialer, ILogger<SessionBridge> logger)
    {
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _rawDialer = rawDialer ?? throw new ArgumentNullException(nameof(rawDialer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the <see cref="SessionBridgeHandle"/> for <paramref name="cli"/>, dialing buildkitd
    /// through <paramref name="link"/> only if one is not already open. A second call for the same
    /// <see cref="CliSession.Id"/> — concurrent or later — returns the existing handle with its ref
    /// count bumped instead of opening a second <c>Control/Session</c> call; pair every call with
    /// <see cref="SessionBridgeHandle.Release"/>.
    /// </summary>
    public async ValueTask<SessionBridgeHandle> AttachAsync(CliSession cli, BuilderLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(link);

        await _attachGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_handles.TryGetValue(cli.Id, out var existing))
            {
                existing.AddRef();
                return existing;
            }

            var handle = await OpenAsync(cli, link, cancellationToken).ConfigureAwait(false);
            _handles[cli.Id] = handle;
            return handle;
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <summary>
    /// Resolves the <see cref="ForwardTarget"/> for one call arriving on the session tunnel — the
    /// selector wired to <see cref="GrpcForwarder.MapGrpcForwarder"/> in <c>DaemonHost</c>. No
    /// session id on the connection, or no bridge open for it, answers <c>Unimplemented</c> (a
    /// <see langword="null"/> target). <c>FileSend/DiffCopy</c> for an exporter id the Solve marked
    /// capturable is answered locally by <see cref="FileSendCapture"/> instead of being forwarded;
    /// every other method reaches <see cref="CliSession.Invoker"/> only if the CLI actually
    /// advertised it (<see cref="CliSession.Methods"/>), matching what a real BuildKit session
    /// manager itself would allow.
    /// </summary>
    public ValueTask<ForwardTarget?> SelectForwardTarget(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var sessionId = http.Features.Get<ITunnelFeature>()?.SessionId;
        if (sessionId is null || !_handles.TryGetValue(sessionId, out var handle))
        {
            return ValueTask.FromResult<ForwardTarget?>(null);
        }

        var method = http.Request.Path.Value ?? string.Empty;

        if (string.Equals(method, BuildKitMethods.FileSend.DiffCopy, StringComparison.OrdinalIgnoreCase))
        {
            var exporterId = ReadExporterId(http.Request.Headers);
            if (handle.CaptureExporterIds.Contains(exporterId))
            {
                return ValueTask.FromResult<ForwardTarget?>(BuildCaptureTarget(handle, exporterId));
            }
        }

        var lowered = method.ToLowerInvariant();
        if (!handle.Cli.Methods.Contains(lowered))
        {
            return ValueTask.FromResult<ForwardTarget?>(null);
        }

        return ValueTask.FromResult<ForwardTarget?>(new ForwardTarget
        {
            Invoker = handle.Cli.Invoker,
            Authority = "session",
        });
    }

    /// <summary>Removes a torn-down handle so a later <see cref="AttachAsync"/> for the same id dials afresh.</summary>
    internal void OnHandleClosed(string sessionId, SessionBridgeHandle handle) =>
        _handles.TryRemove(new KeyValuePair<string, SessionBridgeHandle>(sessionId, handle));

    private ForwardTarget BuildCaptureTarget(SessionBridgeHandle handle, int exporterId)
    {
        var tarPath = Path.Combine(_options.TmpDir, $"export-{handle.Cli.Id}-{exporterId}.tar");
        var capture = new FileSendCapture(tarPath, handle, exporterId, _logger);
        return new ForwardTarget
        {
            Invoker = new HttpMessageInvoker(capture, disposeHandler: true),
            Authority = "session-capture",
        };
    }

    private async Task<SessionBridgeHandle> OpenAsync(CliSession cli, BuilderLink link, CancellationToken cancellationToken)
    {
        var methods = new HashSet<string>(cli.Methods, StringComparer.Ordinal)
        {
            BuildKitMethods.FileSend.DiffCopy.ToLowerInvariant(),
            BuildKitMethods.Health.Check.ToLowerInvariant(),
        };

        // The complete HEADERS block for this dial's one and only request, pseudo-headers first
        // (RFC 7540 §8.1.2.1) -- built by hand and sent through LiteralHeadersRewriteStream rather
        // than as Grpc.Core.Metadata, because System.Net.Http silently comma-joins every value added
        // under one header name before it reaches the wire, and buildkitd never splits that back
        // apart (see LiteralHeadersRewriteStream's own doc comment -- cider-ger.16). A Metadata with
        // a genuine duplicate key here would go right back to that same bug.
        var fields = new List<(string Name, string Value)>
        {
            (":method", "POST"),
            (":scheme", "http"),
            (":authority", "buildkit"),
            (":path", BuildKitMethods.Control.Session),
            ("content-type", "application/grpc"),
            ("te", "trailers"),
            (BuildKitMethods.MetadataKeys.SessionUuid, cli.Id),
        };

        if (!string.IsNullOrEmpty(cli.SharedKey))
        {
            fields.Add((BuildKitMethods.MetadataKeys.SessionSharedKey, cli.SharedKey));
        }

        foreach (var method in methods)
        {
            fields.Add((BuildKitMethods.MetadataKeys.SessionGrpcMethod, method));
        }

        Stream? duplex = null;
        IAsyncDisposable? owner = null;
        SocketsHttpHandler? handler = null;
        GrpcChannel? channel = null;
        HttpMessageInvoker? invoker = null;
        AsyncDuplexStreamingCall<BytesMessage, BytesMessage>? call = null;
        try
        {
            // A dedicated connection, not link.CallInvoker's shared one -- see IRawSessionDialer's
            // doc comment for why LiteralHeadersRewriteStream needs that isolation.
            (duplex, owner) = await _rawDialer.DialAsync(cancellationToken).ConfigureAwait(false);
            var rewritten = new LiteralHeadersRewriteStream(duplex, fields);
            (channel, invoker, handler) = StreamHttp2Client.Create(rewritten, "buildkit");

            // Deliberately CancellationToken.None: this call outlives whatever request triggered
            // AttachAsync (a Bake's CLI stream, a Solve) -- its own lifetime is governed by
            // SessionBridgeHandle.Release/teardown, not by the caller's token, which only bounds how
            // long AttachAsync itself may wait to dial. The Metadata passed here carries nothing --
            // every real header for this call already lives in `fields`, applied by the rewrite
            // stream itself.
            var control = new Control.ControlClient(channel.CreateCallInvoker());
            call = control.Session(new Metadata());

            var bytesStream = new BytesMessageStream(call.ResponseStream, call.RequestStream, link.Target.Pacer);
            var tunnelCts = new CancellationTokenSource();
            var serveTask = _tunnel.ServeAsync(bytesStream, TunnelKind.Session, cli.Id, cancellationToken: tunnelCts.Token);

            var handle = new SessionBridgeHandle(this, cli, call, bytesStream, tunnelCts, serveTask, channel, invoker, handler, owner, _logger);
            _logger.LogDebug("attached session bridge {SessionId} ({MethodCount} methods)", cli.Id, methods.Count);
            return handle;
        }
        catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "buildkitd rejected the session bridge for {SessionId}", cli.Id);
            call?.Dispose();
            invoker?.Dispose();
            if (channel is not null)
            {
                try
                {
                    await channel.ShutdownAsync().ConfigureAwait(false);
                }
                catch (Exception shutdownEx) when (shutdownEx is InvalidOperationException or ObjectDisposedException)
                {
                }
            }

            handler?.Dispose();
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static int ReadExporterId(IHeaderDictionary headers)
    {
        var value = headers[BuildKitMethods.MetadataKeys.AttachableExporterId].FirstOrDefault();
        return value is not null && int.TryParse(value, out var id) ? id : 0;
    }
}
