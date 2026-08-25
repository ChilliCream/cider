using Microsoft.Extensions.Logging;

namespace Cider.AppleContainer.Xpc;

/// <summary>
/// An anonymous XPC listener plus the endpoint that lets a peer connect back to it — the transport
/// primitive apiserver's progress channel is built on
/// (<c>progressUpdateEndpoint</c>, docs/spikes/xpc/02-apiserver-xpc-protocol.md §5, used today only
/// by <c>installKernel</c>). This task only needs the primitive to exist and behave correctly
/// end-to-end (create → hand out an endpoint → receive → cancel cleanly); decoding progress
/// messages into anything the daemon acts on is X9's job, not this one (see the task's non-goals).
///
/// Event-handler-before-activate ordering matters here as much as it does for <see cref="XpcClient"/>:
/// cancelling a connection that was never given an event handler and never activated hangs the
/// process (confirmed live while building this listener) — <see cref="Create"/> always does both
/// before returning. The block itself detaches on the terminal
/// <c>XPC_ERROR_CONNECTION_INVALID</c> event rather than synchronously in <see cref="Dispose"/>,
/// for the same reason <see cref="XpcClient"/>'s does (see <see cref="XpcBlock.Detach"/>'s doc
/// comment: freeing eagerly, from inside the block's own invoke, is undefined behavior — this
/// leaks the block instead).
/// </summary>
internal sealed class XpcListener : IDisposable
{
    private readonly ILogger _logger;
    private readonly nint _connection;
    private bool _disposed;

    private XpcListener(ILogger logger, nint connection, XpcObject endpoint)
    {
        _logger = logger;
        _connection = connection;
        Endpoint = endpoint;
    }

    /// <summary>The endpoint to hand the peer (e.g. as the value of
    /// <c>XPCKeys.progressUpdateEndpoint</c> on a request) so it can connect back and push
    /// messages this listener's <paramref name="onMessage"/> receives.</summary>
    public XpcObject Endpoint { get; }

    /// <summary>
    /// Creates a fresh anonymous connection, wires <paramref name="onMessage"/> as its event
    /// handler, activates it, and wraps it into an <see cref="Endpoint"/> — in that order, per the
    /// type's own doc comment.
    /// </summary>
    /// <param name="onMessage">Invoked on a libdispatch worker thread for every message the peer
    /// sends over the endpoint, and once more with a connection-state sentinel when it disconnects
    /// or this listener is disposed — see <see cref="XpcErrorSentinels"/> to recognise that case.
    /// Never receives the block's own address; that bookkeeping is internal to this type.</param>
    public static XpcListener Create(ILogger logger, Action<nint> onMessage)
    {
        var connection = XpcNative.xpc_connection_create(null, 0);
        if (connection == 0)
        {
            throw XpcException.Invalid("xpc_connection_create(NULL, NULL) returned NULL");
        }

        var block = XpcBlock.CreateEventHandler((self, xpcObject) =>
        {
            onMessage(xpcObject);
            if (xpcObject == XpcErrorSentinels.ConnectionInvalid)
            {
                XpcBlock.Detach(self);
            }
        });

        // Order matters: the handler must be installed before activation (see the type's own doc
        // comment).
        XpcNative.xpc_connection_set_event_handler(connection, block);
        XpcNative.xpc_connection_activate(connection);

        var endpointPtr = XpcNative.xpc_endpoint_create(connection);
        return new XpcListener(logger, connection, new XpcObject(endpointPtr));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Endpoint.Dispose();
        // No retain/release pin needed here (contrast XpcClient.SendSync): this connection never
        // makes a blocking sync send that Dispose could race, only cancel/release.
        XpcNative.xpc_connection_cancel(_connection);
        XpcNative.xpc_release(_connection);
        _logger.LogDebug("xpc anonymous listener cancelled");
    }
}
