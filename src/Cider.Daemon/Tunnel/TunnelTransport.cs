using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;

namespace Cider.Daemon.Tunnel;

/// <summary>
/// Kestrel connection listener for the in-process tunnel: BuildKit's control-plane and session
/// connections never arrive over a socket — they are a hijacked HTTP connection or a child
/// process's stdio (<c>buildctl dial-stdio</c>) that this hands straight to Kestrel's HTTP/2 engine
/// as a new <see cref="ConnectionContext"/>, via <see cref="ServeAsync(Stream, TunnelKind, string?, IDictionary{string, string[]}?, CancellationToken)"/>
/// or its <see cref="ConnectionContext"/> overload.
/// <para>
/// Registered in DI as both <see cref="TunnelTransport"/> (so callers can serve connections) and as
/// <see cref="IConnectionListenerFactory"/> (so Kestrel can pull them off the pending channel).
/// Implementing <see cref="IConnectionListenerFactorySelector"/> is mandatory, not optional: without
/// it Kestrel treats a registered <see cref="IConnectionListenerFactory"/> as able to bind ANY
/// endpoint, and factories are tried last-registered-first — this one would silently steal the
/// daemon's real Unix-socket listener out from under <see cref="Hosting.DaemonHost"/>.
/// </para>
/// </summary>
public sealed class TunnelTransport : IConnectionListenerFactory, IConnectionListenerFactorySelector, IConnectionListener
{
    private readonly TunnelEndPoint _endPoint = new();

    private readonly Channel<TunnelConnectionContext> _pending = Channel.CreateUnbounded<TunnelConnectionContext>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <inheritdoc />
    EndPoint IConnectionListener.EndPoint => _endPoint;

    /// <inheritdoc />
    public bool CanBind(EndPoint endpoint) => endpoint is TunnelEndPoint;

    /// <inheritdoc />
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IConnectionListener>(this);

    /// <inheritdoc />
    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _pending.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands an already-upgraded duplex <paramref name="stream"/> to Kestrel's HTTP/2 engine as a
    /// new tunnel connection of the given <paramref name="kind"/>. The returned task completes once
    /// that connection has been fully torn down (see <see cref="TunnelConnectionContext.Closed"/>).
    /// </summary>
    public Task ServeAsync(
        Stream stream,
        TunnelKind kind,
        string? sessionId = null,
        IDictionary<string, string[]>? meta = null,
        CancellationToken cancellationToken = default) =>
        ServeAsync(new TunnelConnectionContext(stream, kind, sessionId, meta), cancellationToken);

    /// <summary>
    /// Re-tags an already-hijacked <paramref name="connection"/> as a tunnel connection and hands it
    /// to Kestrel's HTTP/2 engine. The returned task completes once the connection has been fully
    /// torn down (see <see cref="TunnelConnectionContext.Closed"/>).
    /// </summary>
    public Task ServeAsync(
        ConnectionContext connection,
        TunnelKind kind,
        string? sessionId = null,
        IDictionary<string, string[]>? meta = null,
        CancellationToken cancellationToken = default) =>
        ServeAsync(new TunnelConnectionContext(connection, kind, sessionId, meta), cancellationToken);

    private async Task ServeAsync(TunnelConnectionContext context, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state => ((TunnelConnectionContext)state!).Abort(), context)
            : default;

        if (!_pending.Writer.TryWrite(context))
        {
            await context.DisposeAsync();
            throw new InvalidOperationException("cider: the tunnel transport is no longer accepting connections");
        }

        await context.Closed;
    }
}
