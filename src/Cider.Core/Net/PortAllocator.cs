using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cider.Core.DockerApi;

namespace Cider.Core.Net;

/// <summary>
/// Hands out free host ports for published container ports. A candidate is accepted only when a
/// real bind on the requested host IP succeeds, and stays reserved until <see cref="Release"/>
/// so two concurrent creates never pick the same port. Thread-safe.
/// </summary>
public sealed class PortAllocator
{
    /// <summary>Low end of Docker's ephemeral publish range.</summary>
    public const int EphemeralMin = 32768;

    /// <summary>High end of Docker's ephemeral publish range.</summary>
    public const int EphemeralMax = 60999;

    private const string AnyIPv4 = "0.0.0.0";

    private readonly Lock _gate = new();
    private readonly HashSet<Reservation> _reserved = [];

    /// <summary>
    /// Reserves a host port. When <paramref name="requestedPort"/> is <c>null</c> or 0 a free port from the
    /// ephemeral range is picked; otherwise that exact port is verified and reserved.
    /// </summary>
    /// <exception cref="DockerApiException">500 when the requested port is taken or the range is exhausted.</exception>
    public int Reserve(string proto, string hostIp, int? requestedPort)
    {
        var protocol = NormalizeProto(proto);
        var ip = NormalizeIp(hostIp);
        var address = ParseAddress(ip);

        if (requestedPort is > 0)
        {
            var port = requestedPort.Value;
            if (port is < 1 or > 65535)
            {
                throw DockerErrors.BadParameter($"invalid port specification: \"{port}\"");
            }

            lock (_gate)
            {
                if (IsReservedLocked(protocol, ip, port) || !CanBind(protocol, address, port))
                {
                    throw DockerErrors.PortAlreadyAllocated(ip, port);
                }

                _reserved.Add(new Reservation(protocol, ip, port));
                return port;
            }
        }

        // Random probe start so parallel daemons/tests do not collide on the low end of the range.
        var span = EphemeralMax - EphemeralMin + 1;
        var start = RandomNumberGenerator.GetInt32(span);

        lock (_gate)
        {
            for (var i = 0; i < span; i++)
            {
                var port = EphemeralMin + ((start + i) % span);
                if (IsReservedLocked(protocol, ip, port) || !CanBind(protocol, address, port))
                {
                    continue;
                }

                _reserved.Add(new Reservation(protocol, ip, port));
                return port;
            }
        }

        throw DockerErrors.Internal($"no free host port available in {EphemeralMin}-{EphemeralMax}");
    }

    /// <summary>Releases a previously reserved port. Unknown reservations are ignored.</summary>
    public void Release(string proto, string hostIp, int port)
    {
        var reservation = new Reservation(NormalizeProto(proto), NormalizeIp(hostIp), port);
        lock (_gate)
        {
            _reserved.Remove(reservation);
        }
    }

    /// <summary><c>true</c> when this allocator currently holds the given port.</summary>
    public bool IsReserved(string proto, string hostIp, int port)
    {
        var protocol = NormalizeProto(proto);
        var ip = NormalizeIp(hostIp);
        lock (_gate)
        {
            return IsReservedLocked(protocol, ip, port);
        }
    }

    /// <summary>Number of ports currently held.</summary>
    public int ReservationCount
    {
        get
        {
            lock (_gate)
            {
                return _reserved.Count;
            }
        }
    }

    /// <summary>Drops every reservation (used when the daemon resets its state).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            _reserved.Clear();
        }
    }

    private bool IsReservedLocked(string proto, string ip, int port)
    {
        if (_reserved.Contains(new Reservation(proto, ip, port)))
        {
            return true;
        }

        // A wildcard reservation covers every specific address and vice versa.
        if (!string.Equals(ip, AnyIPv4, StringComparison.Ordinal))
        {
            return _reserved.Contains(new Reservation(proto, AnyIPv4, port));
        }

        foreach (var reservation in _reserved)
        {
            if (reservation.Port == port && string.Equals(reservation.Proto, proto, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanBind(string proto, IPAddress address, int port)
    {
        try
        {
            if (string.Equals(proto, "udp", StringComparison.Ordinal))
            {
                using var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(address, port));
                return true;
            }

            using var tcp = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string NormalizeProto(string? proto)
    {
        if (string.IsNullOrEmpty(proto))
        {
            return "tcp";
        }

        var lowered = proto.ToLowerInvariant();
        return lowered switch
        {
            "tcp" or "udp" or "sctp" => lowered,
            _ => throw DockerErrors.BadParameter($"invalid protocol: {proto}"),
        };
    }

    private static string NormalizeIp(string? hostIp) =>
        string.IsNullOrEmpty(hostIp) ? AnyIPv4 : hostIp;

    private static IPAddress ParseAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            throw DockerErrors.BadParameter($"invalid host ip address: {ip}");
        }

        return address;
    }

    private readonly record struct Reservation(string Proto, string Ip, int Port);
}
