using System.Net;
using System.Net.Sockets;

namespace Cider.Dns;

/// <summary>Minimal one-shot DNS-over-UDP client, used by tests and internally by <see cref="DnsServer"/>'s upstream forwarder.</summary>
public static class DnsClient
{
    /// <summary>Sends <paramref name="query"/> to <paramref name="server"/> over UDP and parses the response.</summary>
    public static async Task<DnsMessage> QueryAsync(IPEndPoint server, DnsMessage query, CancellationToken ct = default)
    {
        var responseBytes = await QueryRawAsync(server, query.Serialize(), ct).ConfigureAwait(false);
        return DnsMessage.Parse(responseBytes);
    }

    /// <summary>Sends raw wire-format bytes to <paramref name="server"/> over UDP and returns the raw wire-format response.</summary>
    public static async Task<byte[]> QueryRawAsync(IPEndPoint server, byte[] queryBytes, CancellationToken ct = default)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, ct).ConfigureAwait(false);
        await socket.SendAsync(queryBytes, SocketFlags.None, ct).ConfigureAwait(false);

        var buffer = new byte[4096];
        int received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
        return buffer.AsSpan(0, received).ToArray();
    }
}
