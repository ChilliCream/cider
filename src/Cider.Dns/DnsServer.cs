using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cider.Dns;

/// <summary>
/// A small DNS server: serves UDP and TCP on the same endpoint, asks an <see cref="IDnsResolver"/>
/// for each incoming question, and forwards to upstream resolvers over UDP when the resolver
/// declines (returns null). Malformed packets are logged at Debug and dropped rather than crashing
/// the server.
/// </summary>
public sealed class DnsServer : IAsyncDisposable
{
    private const int DefaultUdpPayloadSize = 512;
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(2);

    private readonly IPEndPoint _listenEndpoint;
    private readonly IDnsResolver _resolver;
    private readonly IReadOnlyList<IPEndPoint> _upstreams;
    private readonly ILogger _logger;

    private Socket? _udpSocket;
    private Socket? _tcpSocket;
    private CancellationTokenSource? _cts;
    private Task? _udpLoopTask;
    private Task? _tcpLoopTask;

    public DnsServer(IPEndPoint listen, IDnsResolver resolver, IReadOnlyList<IPEndPoint> upstreams, ILogger? logger = null)
    {
        _listenEndpoint = listen ?? throw new ArgumentNullException(nameof(listen));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _upstreams = upstreams ?? throw new ArgumentNullException(nameof(upstreams));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The bound local endpoint (with the actual port, if 0 was requested), valid after <see cref="StartAsync"/> returns.</summary>
    public IPEndPoint LocalEndPoint { get; private set; } = new(IPAddress.Any, 0);

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Bind UDP first (letting the OS pick a port when _listenEndpoint.Port == 0), then bind TCP
        // to that same numeric port. TCP and UDP port namespaces are independent, so the OS's ephemeral
        // picker for the UDP bind has no way to know that number is already held by an unrelated TCP
        // listener; when that collision happens (observed under concurrent test runs), retry with a
        // fresh ephemeral port. Only worth retrying when the caller asked for an OS-assigned port.
        const int maxBindAttempts = 20;
        Exception? lastBindError = null;

        for (int attempt = 0; attempt < maxBindAttempts; attempt++)
        {
            var udpSocket = new Socket(_listenEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Socket? tcpSocket = null;
            try
            {
                udpSocket.Bind(_listenEndpoint);
                var boundPort = ((IPEndPoint)udpSocket.LocalEndPoint!).Port;
                var localEndpoint = new IPEndPoint(_listenEndpoint.Address, boundPort);

                tcpSocket = new Socket(_listenEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                tcpSocket.Bind(localEndpoint);
                tcpSocket.Listen(64);

                _udpSocket = udpSocket;
                _tcpSocket = tcpSocket;
                LocalEndPoint = localEndpoint;
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && _listenEndpoint.Port == 0)
            {
                lastBindError = ex;
                udpSocket.Dispose();
                tcpSocket?.Dispose();
            }
            catch
            {
                udpSocket.Dispose();
                tcpSocket?.Dispose();
                throw;
            }
        }

        if (_udpSocket is null || _tcpSocket is null)
        {
            throw new IOException($"Could not find a port free on both UDP and TCP after {maxBindAttempts} attempts.", lastBindError);
        }

        var token = _cts.Token;
        _udpLoopTask = Task.Run(() => RunUdpLoopAsync(token), CancellationToken.None);
        _tcpLoopTask = Task.Run(() => RunTcpLoopAsync(token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        try { _udpSocket?.Close(); } catch { /* best effort */ }
        try { _tcpSocket?.Close(); } catch { /* best effort */ }

        if (_udpLoopTask is not null)
        {
            try { await _udpLoopTask.ConfigureAwait(false); } catch { /* loop observes its own cancellation */ }
        }
        if (_tcpLoopTask is not null)
        {
            try { await _tcpLoopTask.ConfigureAwait(false); } catch { /* loop observes its own cancellation */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _udpSocket?.Dispose();
        _tcpSocket?.Dispose();
    }

    // ---------------------------------------------------------------- UDP

    private async Task RunUdpLoopAsync(CancellationToken ct)
    {
        var any = new IPEndPoint(_listenEndpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            byte[] buffer = new byte[4096];
            SocketReceiveFromResult result;
            try
            {
                result = await _udpSocket!.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "UDP receive failed.");
                continue;
            }

            var datagram = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            var remote = (IPEndPoint)result.RemoteEndPoint;
            _ = HandleUdpDatagramAsync(datagram, remote, ct);
        }
    }

    private async Task HandleUdpDatagramAsync(byte[] datagram, IPEndPoint remote, CancellationToken ct)
    {
        try
        {
            var response = await BuildResponseBytesAsync(datagram, remote, isTcp: false, ct).ConfigureAwait(false);
            if (response is null) return; // malformed / unanswerable, already logged
            await _udpSocket!.SendToAsync(response, SocketFlags.None, remote, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // server stopping
        }
        catch (ObjectDisposedException)
        {
            // server stopping
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle UDP DNS datagram from {Remote}.", remote);
        }
    }

    // ---------------------------------------------------------------- TCP

    private async Task RunTcpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _tcpSocket!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "TCP accept failed.");
                continue;
            }

            _ = HandleTcpClientAsync(client, ct);
        }
    }

    private async Task HandleTcpClientAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            IPEndPoint remote;
            try { remote = (IPEndPoint)(client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0)); }
            catch { remote = new IPEndPoint(IPAddress.None, 0); }

            try
            {
                var lengthBuffer = new byte[2];
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(client, lengthBuffer, 2, ct).ConfigureAwait(false)) return;
                    int length = (lengthBuffer[0] << 8) | lengthBuffer[1];
                    if (length == 0) return;

                    var messageBuffer = new byte[length];
                    if (!await ReadExactAsync(client, messageBuffer, length, ct).ConfigureAwait(false)) return;

                    var response = await BuildResponseBytesAsync(messageBuffer, remote, isTcp: true, ct).ConfigureAwait(false);
                    if (response is null) return; // malformed request: can't reliably resync the stream, close

                    var outLength = new byte[2];
                    outLength[0] = (byte)(response.Length >> 8);
                    outLength[1] = (byte)response.Length;
                    await client.SendAsync(outLength, SocketFlags.None, ct).ConfigureAwait(false);
                    await client.SendAsync(response, SocketFlags.None, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // server stopping or client idle-timeout equivalent
            }
            catch (ObjectDisposedException)
            {
                // server stopping
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TCP DNS connection from {Remote} failed.", remote);
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset, count - offset), SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0) return false; // peer closed
            offset += read;
        }
        return true;
    }

    // ---------------------------------------------------------------- shared request handling

    private async Task<byte[]?> BuildResponseBytesAsync(byte[] queryBytes, IPEndPoint client, bool isTcp, CancellationToken ct)
    {
        DnsMessage query;
        try
        {
            query = DnsMessage.Parse(queryBytes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dropping malformed DNS packet from {Client}.", client);
            return null;
        }

        int? maxLength = isTcp ? null : (query.Edns is not null ? EdnsOptions.DefaultResponderPayloadSize : DefaultUdpPayloadSize);

        if (query.Questions.Count == 0)
        {
            return BuildResponse(query, new DnsAnswer(Array.Empty<DnsRecord>(), false, DnsRcode.FormErr)).Serialize(maxLength);
        }

        var question = query.Questions[0];

        DnsAnswer? answer;
        try
        {
            answer = await _resolver.ResolveAsync(question, client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolver threw for {Name} {Type} from {Client}.", question.Name, question.Type, client);
            return BuildResponse(query, new DnsAnswer(Array.Empty<DnsRecord>(), false, DnsRcode.ServFail)).Serialize(maxLength);
        }

        if (answer is not null)
        {
            return BuildResponse(query, answer).Serialize(maxLength);
        }

        // Resolver declined ("not mine"): forward the raw query to upstream and relay unchanged.
        var forwarded = await ForwardToUpstreamAsync(queryBytes, query.Id, ct).ConfigureAwait(false);
        if (forwarded is not null) return forwarded;

        _logger.LogDebug("All upstreams failed for {Name} {Type} from {Client}; answering SERVFAIL.", question.Name, question.Type, client);
        return BuildResponse(query, new DnsAnswer(Array.Empty<DnsRecord>(), false, DnsRcode.ServFail)).Serialize(maxLength);
    }

    private static DnsMessage BuildResponse(DnsMessage query, DnsAnswer answer)
    {
        var response = new DnsMessage
        {
            Id = query.Id,
            IsResponse = true,
            Opcode = query.Opcode,
            Authoritative = answer.Authoritative,
            RecursionDesired = query.RecursionDesired,
            RecursionAvailable = true,
            Rcode = answer.Rcode,
        };

        if (query.Questions.Count > 0) response.Questions.Add(query.Questions[0]);
        response.Answers.AddRange(answer.Answers);

        // Echo a plain OPT (no extended rcode/flags, larger UDP payload size) so EDNS0-aware
        // resolvers (Go, musl) know they can use bigger UDP responses; keeps them from choking.
        if (query.Edns is not null)
        {
            response.Edns = new EdnsOptions(EdnsOptions.DefaultResponderPayloadSize);
        }

        return response;
    }

    private async Task<byte[]?> ForwardToUpstreamAsync(byte[] queryBytes, ushort originalId, CancellationToken ct)
    {
        foreach (var upstream in _upstreams)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(UpstreamTimeout);

            try
            {
                var response = await DnsClient.QueryRawAsync(upstream, queryBytes, timeoutCts.Token).ConfigureAwait(false);
                if (response.Length >= 2)
                {
                    response[0] = (byte)(originalId >> 8);
                    response[1] = (byte)originalId;
                }
                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Upstream {Upstream} timed out.", upstream);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Upstream {Upstream} failed.", upstream);
            }
        }

        return null;
    }
}
