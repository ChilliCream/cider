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

    /// <summary>
    /// Supplies the backend address to a forwarder that was bound without one yet, unblocking whatever
    /// connections are already waiting on it (cider-ede.18). A no-op on a forwarder that already has a
    /// target — which is every <see cref="UdpPortForwarder"/>: it has no accept-and-hold mode and is
    /// never constructed without one to begin with.
    /// </summary>
    void ResolveTarget(IPAddress containerIp);
}

/// <summary>
/// A TCP listener on the host that accepts connections and pumps them, in both directions, to
/// <c>containerIp:containerPort</c>. The listener binds and starts accepting immediately, whether or
/// not <c>containerIp</c> is known yet (cider-ede.18: the daemon no longer waits for the container's
/// VM address before publishing a TCP mapping, so a client racing to connect during the VM boot is
/// accepted and queued instead of refused). A connection accepted before the address is known is held
/// until the guest's own service accepts the dial, bounded by <see cref="TargetWaitTimeout"/> counted
/// from the moment the connection was accepted (covering both the wait for the address and the retried
/// dial once it arrives); past that deadline it is closed without ever having been relayed. A
/// connection accepted after the address was already known — the steady state of a running container —
/// is dialed once, not retried: a dead in-container service fails it fast, exactly as dockerd's
/// userland proxy does. Half-closes are propagated once relaying starts (an EOF on one side shuts the
/// other side's send half down), so protocols that rely on the peer seeing FIN — plain HTTP/1.0
/// responses, <c>nc</c>-style servers — behave the way they would through dockerd's userland proxy.
/// Idle connections are never timed out; only a close on either side or <see cref="Dispose"/> ends
/// them.
/// </summary>
internal sealed class TcpPortForwarder : IPortForwarder
{
    private const int BufferSize = 64 * 1024;
    private const int Backlog = 128;

    /// <summary>
    /// How long an accepted connection waits for <see cref="ResolveTarget"/> before it is closed.
    /// Generous on purpose: it only bounds the pathological case (the address never arrives at all,
    /// e.g. the container's network attachment itself failed) rather than the ordinary VM boot the
    /// daemon is not trying to add its own latency on top of.
    /// </summary>
    internal static readonly TimeSpan TargetWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Socket _listener;
    private readonly int _containerPort;
    private readonly ILogger _logger;
    private readonly TimeSpan _targetWaitTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<IPAddress> _targetTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptLoop;

    private volatile IPAddress? _containerIp;

    /// <summary>
    /// Binds the host endpoint and starts accepting. <paramref name="containerIp"/> may be <c>null</c>
    /// when the container's address is not known yet.
    /// </summary>
    /// <exception cref="SocketException">The host endpoint could not be bound.</exception>
    public TcpPortForwarder(IPEndPoint host, IPAddress? containerIp, int containerPort, ILogger logger)
        : this(host, containerIp, containerPort, logger, TargetWaitTimeout)
    {
    }

    /// <summary>As the public constructor, but with an injectable wait timeout for tests.</summary>
    internal TcpPortForwarder(
        IPEndPoint host, IPAddress? containerIp, int containerPort, ILogger logger, TimeSpan targetWaitTimeout)
    {
        ArgumentNullException.ThrowIfNull(host);

        _containerPort = containerPort;
        _logger = logger;
        _targetWaitTimeout = targetWaitTimeout;
        _listener = Bind(host);
        HostEndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        if (containerIp is not null)
        {
            SetTarget(containerIp);
        }

        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public IPEndPoint HostEndPoint { get; }

    /// <inheritdoc />
    public void ResolveTarget(IPAddress containerIp)
    {
        ArgumentNullException.ThrowIfNull(containerIp);
        SetTarget(containerIp);
    }

    private void SetTarget(IPAddress containerIp)
    {
        _containerIp = containerIp;
        _targetTcs.TrySetResult(containerIp);
    }

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

    /// <summary>
    /// Waits (bounded) for the backend address if it is not known yet, then pumps the connection both
    /// ways. A connection whose wait times out, or that arrives after the forwarder started shutting
    /// down, is closed without ever being relayed.
    /// </summary>
    private async Task RelayAsync(Socket client, CancellationToken ct)
    {
        Socket? upstream = null;
        IPEndPoint? target = null;
        try
        {
            client.NoDelay = true;

            var wasHeld = _containerIp is null;
            var deadline = DateTime.UtcNow + _targetWaitTimeout;

            var containerIp = await AwaitTargetAsync(deadline, ct);
            if (containerIp is null)
            {
                _logger.LogDebug(
                    "closing a connection on published port {Endpoint}: no backend address within {Timeout}",
                    HostEndPoint,
                    _targetWaitTimeout);
                return;
            }

            target = new IPEndPoint(containerIp, _containerPort);
            upstream = await DialAsync(target, wasHeld, deadline, ct);
            if (upstream is null)
            {
                _logger.LogDebug(
                    "closing a connection on published port {Endpoint}: backend at {Target} never accepted "
                    + "within {Timeout}",
                    HostEndPoint,
                    target,
                    _targetWaitTimeout);
                return;
            }

            await Task.WhenAll(
                PumpAsync(client, upstream, ct),
                PumpAsync(upstream, client, ct));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            _logger.LogDebug(ex, "relay {Endpoint} -> {Target} failed", HostEndPoint, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "relay {Endpoint} -> {Target} failed", HostEndPoint, target);
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

    /// <summary>Returns the backend address once known, or <c>null</c> once <paramref name="deadline"/> passes.</summary>
    private async Task<IPAddress?> AwaitTargetAsync(DateTime deadline, CancellationToken ct)
    {
        if (_containerIp is { } known)
        {
            return known;
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await _targetTcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Delay between retried dial attempts against a held connection whose backend refused, was
    /// unreachable, or timed out — the guest's service has not started listening yet.
    /// </summary>
    private static readonly TimeSpan DialRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Dials <paramref name="target"/>. When <paramref name="wasHeld"/> is <c>false</c> — the address
    /// was already known at accept time, the steady state of a running container — this is a single
    /// shot: a dead in-container service fails fast, exactly as dockerd's userland proxy does. When
    /// <paramref name="wasHeld"/> is <c>true</c> — the connection was accepted before the address was
    /// known — a connection-refused/unreachable/timed-out dial is retried until <paramref
    /// name="deadline"/>, because the guest's service may simply not have started listening yet.
    /// Returns <c>null</c> once the deadline passes without a successful dial.
    /// </summary>
    private async Task<Socket?> DialAsync(IPEndPoint target, bool wasHeld, DateTime deadline, CancellationToken ct)
    {
        while (true)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, ct);
                return socket;
            }
            catch (SocketException ex) when (
                wasHeld &&
                ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable or SocketError.TimedOut)
            {
                socket.Dispose();

                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(DialRetryDelay, ct);
            }
            catch
            {
                socket.Dispose();
                throw;
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
