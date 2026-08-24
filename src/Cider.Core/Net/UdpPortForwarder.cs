using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Net;

/// <summary>
/// A UDP relay: datagrams arriving on the host endpoint are forwarded to <c>containerIp:port</c>
/// from a socket kept per source endpoint, and whatever that socket receives back is sent to the
/// source it belongs to — the connection tracking a stateless protocol needs to look bidirectional.
/// A per-source socket is dropped after <see cref="IdleTimeout"/> without traffic.
/// </summary>
internal sealed class UdpPortForwarder : IPortForwarder
{
    /// <summary>How long a per-source socket is kept alive without traffic.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 64 * 1024;

    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(15);

    private readonly Socket _host;
    private readonly IPEndPoint _target;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, Session> _sessions = new();
    private readonly Task _receiveLoop;
    private readonly Timer _reaper;

    /// <summary>Binds the host endpoint and starts relaying.</summary>
    /// <exception cref="SocketException">The host endpoint could not be bound.</exception>
    public UdpPortForwarder(IPEndPoint host, IPEndPoint target, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _logger = logger;
        _host = Bind(host);
        HostEndPoint = (IPEndPoint)_host.LocalEndPoint!;
        _receiveLoop = Task.Run(() => ReceiveAsync(_cts.Token), CancellationToken.None);
        _reaper = new Timer(_ => Reap(), null, ReapInterval, ReapInterval);
    }

    /// <inheritdoc />
    public IPEndPoint HostEndPoint { get; }

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
        var socket = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (host.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = dualMode;
            }

            socket.Bind(host);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var any = _host.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _host.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "receive on published UDP port {Endpoint} failed", HostEndPoint);
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint source)
            {
                continue;
            }

            if (!TryGetSession(source, out var session))
            {
                continue;
            }

            session.Touch();
            try
            {
                await session.Upstream.SendAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, ct);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "forwarding a datagram to {Target} failed", _target);
                Drop(source);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool TryGetSession(IPEndPoint source, out Session session)
    {
        if (_sessions.TryGetValue(source, out var existing))
        {
            session = existing;
            return true;
        }

        Socket upstream;
        try
        {
            upstream = new Socket(_target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            upstream.Connect(_target);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "opening a UDP session to {Target} failed", _target);
            session = null!;
            return false;
        }

        var created = new Session(upstream, source);
        session = _sessions.GetOrAdd(source, created);
        if (!ReferenceEquals(session, created))
        {
            // Another datagram from the same source won the race.
            created.Dispose();
            return true;
        }

        session.Pump = Task.Run(() => PumpBackAsync(created, _cts.Token), CancellationToken.None);
        return true;
    }

    private async Task PumpBackAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await session.Upstream.ReceiveAsync(buffer, SocketFlags.None, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "receiving a reply from {Target} failed", _target);
                return;
            }

            session.Touch();
            try
            {
                await _host.SendToAsync(buffer.AsMemory(0, read), SocketFlags.None, session.Source, ct);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "relaying a reply to {Source} failed", session.Source);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Reap()
    {
        var cutoff = Environment.TickCount64 - (long)IdleTimeout.TotalMilliseconds;
        foreach (var (source, session) in _sessions)
        {
            if (session.LastActivity < cutoff)
            {
                Drop(source);
            }
        }
    }

    private void Drop(IPEndPoint source)
    {
        if (_sessions.TryRemove(source, out var session))
        {
            session.Dispose();
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

        _reaper.Dispose();
        _host.Dispose();

        foreach (var source in _sessions.Keys)
        {
            Drop(source);
        }

        try
        {
            _receiveLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    /// <summary>One source endpoint's socket into the container, plus its reply pump.</summary>
    private sealed class Session(Socket upstream, IPEndPoint source) : IDisposable
    {
        public Socket Upstream { get; } = upstream;

        public IPEndPoint Source { get; } = source;

        public Task? Pump { get; set; }

        public long LastActivity { get; private set; } = Environment.TickCount64;

        public void Touch() => LastActivity = Environment.TickCount64;

        public void Dispose() => Upstream.Dispose();
    }
}
