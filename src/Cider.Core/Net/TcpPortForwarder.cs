using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Net;

/// <summary>One bound host endpoint that relays into a container. Disposing closes everything.</summary>
internal interface IPortForwarder : IDisposable
{
    /// <summary>The host endpoint actually bound (the port is resolved when it was requested as 0).</summary>
    IPEndPoint HostEndPoint { get; }
}

/// <summary>
/// A TCP listener on the host that accepts connections and pumps them, in both directions, to
/// <c>containerIp:containerPort</c>. Half-closes are propagated (an EOF on one side shuts the other
/// side's send half down), so protocols that rely on the peer seeing FIN — plain HTTP/1.0 responses,
/// <c>nc</c>-style servers — behave the way they would through dockerd's userland proxy. Idle
/// connections are never timed out; only a close on either side or <see cref="Dispose"/> ends them.
/// </summary>
internal sealed class TcpPortForwarder : IPortForwarder
{
    private const int BufferSize = 64 * 1024;
    private const int Backlog = 128;

    private readonly Socket _listener;
    private readonly IPEndPoint _target;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>Binds the host endpoint and starts accepting.</summary>
    /// <exception cref="SocketException">The host endpoint could not be bound.</exception>
    public TcpPortForwarder(IPEndPoint host, IPEndPoint target, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _logger = logger;
        _listener = Bind(host);
        HostEndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public IPEndPoint HostEndPoint { get; }

    /// <summary>
    /// Binds a listener. <c>::</c> is dual-mode so one listener serves both families, except when the
    /// IPv4 wildcard is already bound (which is exactly what the caller does for Docker's default
    /// "publish on 0.0.0.0 and ::"): then it retries IPv6-only so the two can coexist.
    /// </summary>
    private static Socket Bind(IPEndPoint host)
    {
        var wildcardV6 = host.AddressFamily == AddressFamily.InterNetworkV6 && host.Address.Equals(IPAddress.IPv6Any);
        if (!wildcardV6)
        {
            return BindCore(host, dualMode: false);
        }

        try
        {
            return BindCore(host, dualMode: true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse)
        {
            return BindCore(host, dualMode: false);
        }
    }

    private static Socket BindCore(IPEndPoint host, bool dualMode)
    {
        var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (host.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = dualMode;
            }

            socket.Bind(host);
            socket.Listen(Backlog);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "accept on published port {Endpoint} failed", HostEndPoint);
                continue;
            }

            _ = Task.Run(() => RelayAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task RelayAsync(Socket client, CancellationToken ct)
    {
        Socket? upstream = null;
        try
        {
            client.NoDelay = true;
            upstream = new Socket(_target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await upstream.ConnectAsync(_target, ct);

            await Task.WhenAll(
                PumpAsync(client, upstream, ct),
                PumpAsync(upstream, client, ct));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            _logger.LogDebug(ex, "relay {Endpoint} -> {Target} failed", HostEndPoint, _target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "relay {Endpoint} -> {Target} failed", HostEndPoint, _target);
        }
        finally
        {
            Close(client);
            if (upstream is not null)
            {
                Close(upstream);
            }
        }
    }

    /// <summary>Copies until EOF, then half-closes the destination so the peer sees the FIN.</summary>
    private static async Task PumpAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await from.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }

                if (read <= 0)
                {
                    return;
                }

                var sent = 0;
                while (sent < read)
                {
                    int wrote;
                    try
                    {
                        wrote = await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, ct);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        return;
                    }

                    if (wrote <= 0)
                    {
                        return;
                    }

                    sent += wrote;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                to.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // The other side is already gone; nothing to half-close.
            }
        }
    }

    private static void Close(Socket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch (SocketException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Close(_listener);

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
