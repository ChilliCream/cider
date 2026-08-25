using Grpc.Net.Client;

namespace Cider.Daemon.Tunnel;

/// <summary>
/// Builds a gRPC client that speaks HTTP/2 over an arbitrary already-connected duplex
/// <see cref="Stream"/> instead of a socket — the CLI's <c>/session</c> leg (a <see cref="DuplexStream"/>
/// over the hijacked HTTP connection) and <c>buildctl dial-stdio</c> (a <see cref="DuplexStream"/>
/// over a child process's stdio) both need exactly this.
/// </summary>
public static class StreamHttp2Client
{
    private const int DefaultInitialStreamWindowSize = 64 * 1024;

    /// <summary>
    /// One <see cref="SocketsHttpHandler"/> whose <see cref="SocketsHttpHandler.ConnectCallback"/>
    /// hands out <paramref name="duplex"/> exactly once — HTTP/2 multiplexes every call over that
    /// one connection, and a second dial attempt (the pool retrying after the peer resets) fails
    /// loudly instead of silently opening a second, unrelated stream.
    /// <para>
    /// The returned <see cref="HttpMessageInvoker"/> shares the same handler for raw request
    /// forwarding (the generic gRPC-over-HTTP/2 forwarder); neither it nor the channel disposes the
    /// handler (<see cref="GrpcChannelOptions.DisposeHttpClient"/> is <see langword="false"/>) — the
    /// caller owns the returned <see cref="SocketsHttpHandler"/> and disposes it exactly once, which
    /// tears down both the channel and the invoker.
    /// </para>
    /// <para>
    /// <paramref name="initialStreamWindow"/> bounds only bytes the peer sends TO us — our own
    /// receive window. What we may send to the peer is governed by the peer's receive window, which
    /// this cannot set.
    /// </para>
    /// </summary>
    public static (GrpcChannel Channel, HttpMessageInvoker Invoker, SocketsHttpHandler Handler) Create(
        Stream duplex, string authority, int? initialStreamWindow = null)
    {
        ArgumentNullException.ThrowIfNull(duplex);
        ArgumentException.ThrowIfNullOrEmpty(authority);

        var dialed = 0;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, _) =>
            {
                if (Interlocked.Exchange(ref dialed, 1) != 0)
                {
                    throw new HttpRequestException("cider: this tunnel stream has already been consumed by one HTTP/2 connection");
                }

                return ValueTask.FromResult(duplex);
            },
            EnableMultipleHttp2Connections = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            InitialHttp2StreamWindowSize = initialStreamWindow ?? DefaultInitialStreamWindowSize,
        };

        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        var channel = GrpcChannel.ForAddress("http://" + authority, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = false,
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
        });

        return (channel, invoker, handler);
    }
}
