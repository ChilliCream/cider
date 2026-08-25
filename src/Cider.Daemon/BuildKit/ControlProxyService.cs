using Google.Protobuf;
using Grpc.Core;
using Moby.Buildkit.V1;

namespace Cider.Daemon.BuildKit;

/// <summary>
/// Implements the three <c>moby.buildkit.v1.Control</c> methods this proxy actually decodes —
/// <see cref="Solve"/> (moby → docker exporter rewrite, session-attach, tar load), <see cref="ListWorkers"/>
/// (snapshotter label strip), and <see cref="Session"/> (a bake shared local-context session dialed
/// straight over <c>/grpc</c>) — mapped by <see cref="ControlProxyMethodProvider"/>, never through the
/// generated <c>Control.BindService</c>. See that provider's doc comment for why this deliberately
/// does not inherit <see cref="Control.ControlBase"/>.
/// <para>
/// Every other <c>Control</c> method (<c>Status</c>, <c>Info</c>, <c>DiskUsage</c>, <c>Prune</c>,
/// <c>ListenBuildHistory</c>, <c>UpdateBuildHistory</c>) and every other service on the same
/// connection (<c>LLBBridge/*</c>, <c>Content/*</c>, <c>TraceService/Export</c>) falls through to
/// <see cref="GrpcForwarder.MapGrpcForwarder"/> instead, forwarded byte-for-byte — see
/// <c>DaemonHost</c>'s route mapping.
/// </para>
/// </summary>
public sealed class ControlProxyService
{
    /// <summary>How long <see cref="Solve"/> waits for a session named in the request to attach (buildkit's own session manager blocks the same way — session/manager.go:149-191).</summary>
    private static readonly TimeSpan SessionWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="Solve"/> waits for the captured docker-exporter tar after buildkitd's own Solve call returns.</summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(60);

    private readonly IBuilderConnection _builder;
    private readonly CliSessionRegistry _registry;
    private readonly SessionBridge _sessionBridge;
    private readonly ExportLoader _exportLoader;
    private readonly ILogger<ControlProxyService> _logger;

    public ControlProxyService(
        IBuilderConnection builder,
        CliSessionRegistry registry,
        SessionBridge sessionBridge,
        ExportLoader exportLoader,
        ILogger<ControlProxyService> logger)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessionBridge = sessionBridge ?? throw new ArgumentNullException(nameof(sessionBridge));
        _exportLoader = exportLoader ?? throw new ArgumentNullException(nameof(exportLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attaches every session the request names (<see cref="SolveRequest.Session"/> plus every
    /// <c>local-sessionid:*</c> in <see cref="SolveRequest.FrontendAttrs"/>) to buildkitd BEFORE
    /// forwarding, rewrites a <c>moby</c> exporter to <c>docker</c> (<see cref="SolveRewriter"/>),
    /// forwards the (possibly rewritten) request, and — if it rewrote one — loads the captured tar
    /// and rewrites the response to report cider's own image id (<see cref="ExportLoader"/>).
    /// An <c>Internal</c> or exporter-less request is forwarded and returned untouched.
    /// </summary>
    public async Task<SolveResponse> Solve(SolveRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var link = await GetLinkAsync(context).ConfigureAwait(false);
        var handles = await AttachSessionsAsync(request, link, context).ConfigureAwait(false);
        try
        {
            var rewrite = SolveRewriter.Rewrite(request);

            SessionBridgeHandle? primaryHandle = null;
            if (rewrite.CaptureExporterIds.Count > 0 &&
                !string.IsNullOrEmpty(request.Session) &&
                handles.TryGetValue(request.Session, out primaryHandle))
            {
                foreach (var exporterId in rewrite.CaptureExporterIds)
                {
                    primaryHandle.CaptureExporterIds.Add(exporterId);
                }
            }

            var upstream = await ForwardSolveAsync(request, link, context).ConfigureAwait(false);

            if (rewrite.Exporters.Count == 0 || primaryHandle is null)
            {
                return upstream;
            }

            return await LoadExportAsync(upstream, rewrite.Exporters[0], primaryHandle, context).ConfigureAwait(false);
        }
        finally
        {
            foreach (var handle in handles.Values)
            {
                handle.Release();
            }
        }
    }

    /// <summary>Forwards to buildkitd and strips <see cref="WorkerLabels.SnapshotterLabel"/> from the reply.</summary>
    public async Task<ListWorkersResponse> ListWorkers(ListWorkersRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var link = await GetLinkAsync(context).ConfigureAwait(false);
        var client = new Control.ControlClient(link.CallInvoker);
        var call = client.ListWorkersAsync(request, BuildHeaders(context), DeadlineOf(context), context.CancellationToken);

        ListWorkersResponse response;
        try
        {
            await PropagateHeadersAsync(call, context).ConfigureAwait(false);
            response = await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            InvalidateIfLinkFailure(link, ex);
            throw;
        }
        finally
        {
            call.Dispose();
        }

        WorkerLabels.Strip(response);
        return response;
    }

    /// <summary>
    /// A <c>Control/Session</c> call dialed straight against <c>/grpc</c> instead of the usual
    /// hijacked <c>/session</c> — the shape buildx bake uses to announce a shared local build
    /// context's session (<c>local-sessionid:&lt;name&gt;=&lt;id&gt;</c> in a later Solve's
    /// <c>FrontendAttrs</c>). The request/response <see cref="BytesMessage"/> streams are wrapped as
    /// a plain duplex stream and registered exactly like a hijacked <c>/session</c> connection would
    /// be, then immediately attached to buildkitd so a later Solve can find it by id; the call stays
    /// open until the far side (buildx) closes it.
    /// </summary>
    public async Task Session(
        IAsyncStreamReader<BytesMessage> requestStream,
        IServerStreamWriter<BytesMessage> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = HeaderValue(context.RequestHeaders, BuildKitMethods.MetadataKeys.SessionUuid);
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "cider: no session id"));
        }

        var sharedKey = HeaderValue(context.RequestHeaders, BuildKitMethods.MetadataKeys.SessionSharedKey);
        var methods = context.RequestHeaders
            .Where(h => string.Equals(h.Key, BuildKitMethods.MetadataKeys.SessionGrpcMethod, StringComparison.Ordinal))
            .Select(h => h.Value);

        var stream = new ServerSessionStream(requestStream, responseStream);
        CliSession cli;
        try
        {
            cli = _registry.RegisterFromStream(sessionId, sharedKey, methods, stream);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }

        var link = await GetLinkAsync(context).ConfigureAwait(false);

        SessionBridgeHandle handle;
        try
        {
            handle = await _sessionBridge.AttachAsync(cli, link, context.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _registry.Unregister(sessionId);
            await cli.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            await cli.Closed.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            handle.Release();
            _registry.Unregister(sessionId);
        }
    }

    // ---- Solve helpers --------------------------------------------------

    /// <summary>
    /// Attaches every session the request names to <paramref name="link"/> BEFORE the request is
    /// forwarded, per the fix direction: buildkitd's exporter and gateway calls can arrive the moment
    /// <c>Solve</c> starts, and its session manager already blocks up to its own timeout waiting for
    /// an attach — attaching first here means it never has to.
    /// </summary>
    private async Task<Dictionary<string, SessionBridgeHandle>> AttachSessionsAsync(
        SolveRequest request, BuilderLink link, ServerCallContext context)
    {
        var sessionIds = new List<string>();
        if (!string.IsNullOrEmpty(request.Session))
        {
            sessionIds.Add(request.Session);
        }

        foreach (var attr in request.FrontendAttrs)
        {
            if (attr.Key.StartsWith(BuildKitMethods.MetadataKeys.LocalSessionIdPrefix, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(attr.Value))
            {
                sessionIds.Add(attr.Value);
            }
        }

        var handles = new Dictionary<string, SessionBridgeHandle>(StringComparer.Ordinal);
        try
        {
            foreach (var id in sessionIds.Distinct(StringComparer.Ordinal))
            {
                CliSession cli;
                try
                {
                    cli = await _registry.WaitAsync(id, SessionWaitTimeout, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                {
                    throw new RpcException(new Status(StatusCode.DeadlineExceeded, $"cider: timed out waiting for session {id}"));
                }

                handles[id] = await _sessionBridge.AttachAsync(cli, link, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var handle in handles.Values)
            {
                handle.Release();
            }

            throw;
        }

        return handles;
    }

    private async Task<SolveResponse> ForwardSolveAsync(SolveRequest request, BuilderLink link, ServerCallContext context)
    {
        var client = new Control.ControlClient(link.CallInvoker);
        var call = client.SolveAsync(request, BuildHeaders(context), DeadlineOf(context), context.CancellationToken);
        try
        {
            await PropagateHeadersAsync(call, context).ConfigureAwait(false);
            return await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            InvalidateIfLinkFailure(link, ex);
            throw;
        }
        finally
        {
            call.Dispose();
        }
    }

    private async Task<SolveResponse> LoadExportAsync(
        SolveResponse upstream, RewrittenExporter exporter, SessionBridgeHandle handle, ServerCallContext context)
    {
        ExportResult export;
        try
        {
            export = await handle.ExportFor(exporter.Index).WaitAsync(ExportTimeout, context.CancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "cider: timed out waiting for the docker exporter output"));
        }

        if (!export.TakeOwnership())
        {
            throw new RpcException(new Status(StatusCode.Internal, "cider: the docker exporter output was already claimed"));
        }

        var loaded = await _exportLoader.LoadAsync(export.TarPath, exporter, context.CancellationToken).ConfigureAwait(false);

        // Everything else buildkitd's own docker exporter would have reported --
        // containerimage.descriptor included -- passes through untouched; only the identity fields a
        // moby-shaped client actually reads (ImageManager.cs:216-222's mobyexporter parity) are
        // replaced with cider's own image id.
        var response = upstream.Clone();
        response.ExporterResponse["containerimage.digest"] = loaded.ImageId;
        response.ExporterResponse["containerimage.config.digest"] = loaded.ImageId;
        if (loaded.Tags.Count > 0)
        {
            response.ExporterResponse["image.name"] = string.Join(',', loaded.Tags);
        }
        else
        {
            // Untagged build: the synthetic tag was applied so the image is dangling-visible, but
            // never shown back to the caller as a name.
            response.ExporterResponse.Remove("image.name");
        }

        return response;
    }

    // ---- shared helpers ---------------------------------------------------

    private async Task<BuilderLink> GetLinkAsync(ServerCallContext context)
    {
        try
        {
            return await _builder.GetAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (BuilderUnavailableException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }
    }

    /// <summary>
    /// Only a transport-shaped failure (matching <c>BuilderConnection</c>'s own notion of a link
    /// failure) invalidates the link — an ordinary application-level Solve error (a bad Dockerfile,
    /// say) says nothing about the link's health.
    /// </summary>
    private void InvalidateIfLinkFailure(BuilderLink link, RpcException ex)
    {
        if (ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal)
        {
            _builder.Invalidate(link, ex);
        }
    }

    private static async Task PropagateHeadersAsync<TResponse>(AsyncUnaryCall<TResponse> call, ServerCallContext context)
    {
        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        await context.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
    }

    /// <summary>Copies the incoming request's metadata for the outgoing call, dropping HTTP/2 pseudo-headers and the gRPC-framing-owned ones a caller must never forward verbatim.</summary>
    private static Metadata BuildHeaders(ServerCallContext context)
    {
        var headers = new Metadata();
        foreach (var entry in context.RequestHeaders)
        {
            if (entry.Key.StartsWith(':') ||
                entry.Key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "content-type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "te", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers.Add(entry);
        }

        return headers;
    }

    private static DateTime? DeadlineOf(ServerCallContext context) =>
        context.Deadline == DateTime.MaxValue ? null : context.Deadline;

    private static string? HeaderValue(Metadata headers, string key)
    {
        foreach (var entry in headers)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps a server-side <c>Control/Session</c> call's two <see cref="BytesMessage"/> stream halves
    /// as a plain duplex <see cref="Stream"/> for <see cref="CliSessionRegistry.RegisterFromStream"/>
    /// to build a <see cref="CliSession"/> over — the mirror of <see cref="BytesMessageStream"/> (used
    /// client-side, by <see cref="SessionBridge"/>, to dial buildkitd) with the two roles reversed:
    /// this reads buildx's own request stream and writes back on the response stream, which — being a
    /// gRPC server stream — has no <c>CompleteAsync</c> of its own; its write side simply ends when
    /// this RPC method returns.
    /// </summary>
    private sealed class ServerSessionStream(
        IAsyncStreamReader<BytesMessage> reader, IServerStreamWriter<BytesMessage> writer) : Stream
    {
        private const int MaxWriteChunk = 32 * 1024;

        private ReadOnlyMemory<byte> _pending;
        private bool _readerDone;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException("cider: a session stream has no length");

        public override long Position
        {
            get => throw new NotSupportedException("cider: a session stream has no position");
            set => throw new NotSupportedException("cider: a session stream has no position");
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

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("cider: a session stream cannot seek");

        public override void SetLength(long value) =>
            throw new NotSupportedException("cider: a session stream cannot be resized");
    }
}
