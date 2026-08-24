using System.Net.Sockets;

namespace Cider.Daemon.Hosting;

/// <summary>A minimal HTTP client for talking to a running cider over its unix socket.</summary>
public static class DaemonClient
{
    /// <summary>Creates an <see cref="HttpClient"/> whose connections all go to <paramref name="socketPath"/>.</summary>
    public static HttpClient Create(string socketPath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
    }
}
