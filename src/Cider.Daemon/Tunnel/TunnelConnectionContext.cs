using System.IO.Pipelines;
using Cider.Daemon.Hosting;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Cider.Daemon.Tunnel;

/// <summary>
/// A Kestrel <see cref="ConnectionContext"/> for one leg of the BuildKit tunnel. Built either from
/// an already-duplex <see cref="Stream"/> (a child process's stdio for <c>buildctl dial-stdio</c>,
/// or a synthetic pipe pair in tests) or by re-tagging an existing <see cref="ConnectionContext"/>
/// (the daemon's own <c>/grpc</c>/<c>/session</c> hijack, whose transport is already an
/// <see cref="IDuplexPipe"/> — reused as-is rather than wrapped a second time).
/// <para>
/// Implements its own <see cref="IConnectionIdFeature"/>, <see cref="IConnectionTransportFeature"/>,
/// <see cref="IConnectionItemsFeature"/>, <see cref="IConnectionLifetimeFeature"/> and
/// <see cref="ITunnelFeature"/> on a private <see cref="FeatureCollection"/>, so Kestrel's HTTP/2
/// engine — and <see cref="TunnelRoutes.RequireTunnel{TBuilder}"/> downstream — see a connection
/// indistinguishable from a real one, except <see cref="ConnectionContext.LocalEndPoint"/> and
/// <see cref="ConnectionContext.RemoteEndPoint"/>, which stay <see langword="null"/>: there is no
/// socket behind either leg, so <c>HttpContext.Connection.LocalIpAddress</c> is never populated on a
/// tunnel request — nothing downstream may rely on it.
/// </para>
/// </summary>
public sealed class TunnelConnectionContext : ConnectionContext,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IConnectionItemsFeature,
    IConnectionLifetimeFeature,
    ITunnelFeature
{
    private readonly Stream? _ownedStream;
    private readonly ConnectionContext? _inner;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken _connectionClosedToken;
    private int _disposed;

    /// <summary>Wraps a raw duplex <paramref name="stream"/> as a brand-new tunnel connection.</summary>
    public TunnelConnectionContext(Stream stream, TunnelKind kind, string? sessionId, IDictionary<string, string[]>? meta)
        : this(kind, sessionId, meta)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _ownedStream = stream;
        Transport = new DuplexPipe(
            PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)),
            PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
    }

    /// <summary>
    /// Re-tags an already-hijacked <paramref name="connection"/> as a tunnel connection, reusing its
    /// transport.
    /// <para>
    /// Two things the plain field copy below would otherwise drop, both needed the moment the
    /// hijacking connection (a real Kestrel socket connection) closes out from under this wrapper
    /// rather than through it — e.g. the OS tearing down the underlying Unix socket: the inner
    /// connection's own <see cref="ConnectionClosed"/> is chained into this wrapper's so it aborts
    /// too instead of dangling forever with pending reads that never unstick, and any feature the
    /// inner connection carries (e.g. <see cref="IConnectionSocketFeature"/>) is copied over so
    /// downstream code sees the same connection it would have without the re-tag — never
    /// overwriting a feature this type sets on itself above.
    /// </para>
    /// </summary>
    public TunnelConnectionContext(ConnectionContext connection, TunnelKind kind, string? sessionId, IDictionary<string, string[]>? meta)
        : this(kind, sessionId, meta)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _inner = connection;
        ConnectionId = connection.ConnectionId;
        Transport = connection.Transport;

        foreach (var feature in connection.Features)
        {
            if (Features[feature.Key] is null)
            {
                Features[feature.Key] = feature.Value;
            }
        }

        if (connection.ConnectionClosed.CanBeCanceled)
        {
            connection.ConnectionClosed.Register(static state => ((TunnelConnectionContext)state!).Abort(), this);
        }
    }

    private TunnelConnectionContext(TunnelKind kind, string? sessionId, IDictionary<string, string[]>? meta)
    {
        ConnectionId = Guid.NewGuid().ToString("n");
        Kind = kind;
        SessionId = sessionId;
        Meta = meta ?? new Dictionary<string, string[]>();
        _connectionClosedToken = _closedCts.Token;

        Features.Set<IConnectionIdFeature>(this);
        Features.Set<IConnectionTransportFeature>(this);
        Features.Set<IConnectionItemsFeature>(this);
        Features.Set<IConnectionLifetimeFeature>(this);
        Features.Set<ITunnelFeature>(this);
    }

    /// <inheritdoc />
    public override string ConnectionId { get; set; }

    /// <inheritdoc />
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc />
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    /// <inheritdoc />
    public override IDuplexPipe Transport { get; set; } = null!;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed
    {
        get => _connectionClosedToken;
        set => _connectionClosedToken = value;
    }

    /// <inheritdoc />
    public TunnelKind Kind { get; }

    /// <inheritdoc />
    public string? SessionId { get; }

    /// <inheritdoc />
    public IDictionary<string, string[]> Meta { get; }

    /// <summary>Completes once <see cref="DisposeAsync"/> has finished tearing this connection down.</summary>
    public Task Closed => _closedTcs.Task;

    /// <summary>
    /// Cancels <see cref="ConnectionClosed"/> and unsticks any pending read/flush on the transport.
    /// Also satisfies <see cref="IConnectionLifetimeFeature.Abort()"/> — <see cref="ConnectionContext"/>'s
    /// own <c>Abort(ConnectionAbortedException)</c> forwards here via that feature, so this must not
    /// call back into it.
    /// </summary>
    public override void Abort()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _closedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Transport.Input.CancelPendingRead();
        Transport.Output.CancelPendingFlush();
        _inner?.Abort();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await Closed.ConfigureAwait(false);
            return;
        }

        await TryCompleteAsync(Transport.Input).ConfigureAwait(false);
        await TryCompleteAsync(Transport.Output).ConfigureAwait(false);

        if (_ownedStream is not null)
        {
            try
            {
                await _ownedStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            _closedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _closedCts.Dispose();
        _closedTcs.TrySetResult();
    }

    private static async ValueTask TryCompleteAsync(PipeReader reader)
    {
        try
        {
            await reader.CompleteAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }

    private static async ValueTask TryCompleteAsync(PipeWriter writer)
    {
        try
        {
            await writer.CompleteAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }
}
