using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Cider.Core.Configuration;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Json;
using Cider.Core.DockerApi.Models;
using Cider.Core.Services;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Tunnel;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Cider.Daemon.Hosting;

/// <summary>
/// Connection-level middleware that answers Docker's two hijacking endpoints itself:
/// <c>POST /exec/{id}/start</c> and <c>POST /containers/{id}/attach</c>.
/// <para>
/// Kestrel cannot serve either one: it refuses to upgrade a request that carries a body (exec start
/// always sends JSON), and it tears the connection down the moment the client half-closes its write
/// side — which <c>docker run</c> does right after attaching, while the hijacked stream still has to
/// deliver the container's output. So the first request head of every connection is peeked before
/// Kestrel's HTTP layer sees it and, when it is a hijack carrying <c>Upgrade: tcp</c>, this
/// middleware owns the socket from there on. Every other connection is replayed into a fresh pipe
/// and forwarded to Kestrel untouched.
/// </para>
/// </summary>
public static class HijackInterceptor
{
    private const int MaxHeadBytes = 64 * 1024;

    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long the upgraded stream waits after the head before it starts sending frames.</summary>
    private static readonly TimeSpan HeadBoundary = TimeSpan.FromMilliseconds(2);

    /// <summary>Peeks the first request of <paramref name="context"/> and either hijacks it or forwards it.</summary>
    public static async Task HandleAsync(ConnectionContext context, ConnectionDelegate next, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cider.Daemon.Hijack");
        var input = context.Transport.Input;

        ReadOnlySequence<byte> buffer = default;
        HijackRequestHead? hijack = null;

        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed, deadline.Token);

        try
        {
            var timing = false;
            while (true)
            {
                var result = await input.ReadAsync(linked.Token);
                buffer = result.Buffer;

                if (!timing && !buffer.IsEmpty)
                {
                    // Once a request has begun, it has to finish arriving in reasonable time.
                    timing = true;
                    deadline.CancelAfter(HeadTimeout);
                }

                if (TryFindHeadEnd(buffer, out var headEnd))
                {
                    var headText = Encoding.ASCII.GetString(buffer.Slice(0, headEnd).ToArray());
                    var parsed = HijackRequestHead.TryParse(headText);
                    if (parsed is { Upgrade: true })
                    {
                        input.AdvanceTo(buffer.GetPosition(headEnd));
                        hijack = parsed;
                    }

                    break;
                }

                if (result.IsCompleted || buffer.Length > MaxHeadBytes)
                {
                    break;
                }

                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
            logger.LogDebug("connection closed before a request head arrived");
            return;
        }

        if (hijack is not null)
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            byte[] body;
            try
            {
                body = await ReadExactAsync(input, hijack.ContentLength, context.ConnectionClosed);
            }
            catch (Exception ex) when (StdioPump.IsDisconnect(ex))
            {
                return;
            }

            await HandleHijackAsync(context, services, logger, hijack, body);
            return;
        }

        // Not a hijack: replay what was peeked and let Kestrel own the connection from here.
        var pipe = new Pipe();
        try
        {
            foreach (var segment in buffer)
            {
                await pipe.Writer.WriteAsync(segment, context.ConnectionClosed);
            }

            input.AdvanceTo(buffer.End);
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
            return;
        }

        // The forwarding pump lives as long as the connection does; it ends when either side closes.
        _ = ForwardAsync(input, pipe.Writer, logger);
        context.Transport = new DuplexPipe(pipe.Reader, context.Transport.Output);
        await next(context);
    }

    /// <summary>
    /// Finds the end of the request head: the first empty line, whatever line terminators the
    /// client mixes. Docker.DotNet (and therefore Testcontainers, Aspire and every Docker.DotNet
    /// app) terminates the request line with CRLF but each header line with a bare LF, so its head
    /// ends with <c>"\n\r\n"</c> — neither <c>"\r\n\r\n"</c> nor <c>"\n\n"</c>. Matching only those
    /// two made the interceptor wait for more bytes that never came, and every such client hung
    /// until the 30 s head timeout closed the connection with no response at all.
    /// </summary>
    private static bool TryFindHeadEnd(ReadOnlySequence<byte> buffer, out long end)
    {
        var reader = new SequenceReader<byte>(buffer);
        while (reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)'\n', advancePastDelimiter: true))
        {
            var afterLine = reader.Consumed;
            if (!reader.TryPeek(out var next))
            {
                break;
            }

            if (next == (byte)'\n')
            {
                end = afterLine + 1;
                return true;
            }

            if (next == (byte)'\r')
            {
                if (!reader.TryPeek(1, out var second))
                {
                    break;
                }

                if (second == (byte)'\n')
                {
                    end = afterLine + 2;
                    return true;
                }
            }
        }

        end = 0;
        return false;
    }

    private static async Task<byte[]> ReadExactAsync(PipeReader input, long count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        while (true)
        {
            var result = await input.ReadAsync(ct);
            var buffer = result.Buffer;

            if (buffer.Length >= count)
            {
                var data = buffer.Slice(0, count).ToArray();
                input.AdvanceTo(buffer.GetPosition(count));
                return data;
            }

            if (result.IsCompleted)
            {
                var data = buffer.ToArray();
                input.AdvanceTo(buffer.End);
                return data;
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static async Task ForwardAsync(PipeReader from, PipeWriter to, ILogger logger)
    {
        try
        {
            while (true)
            {
                var result = await from.ReadAsync();
                var buffer = result.Buffer;

                foreach (var segment in buffer)
                {
                    var flush = await to.WriteAsync(segment);
                    if (flush.IsCompleted)
                    {
                        from.AdvanceTo(buffer.End);
                        await to.CompleteAsync();
                        return;
                    }
                }

                from.AdvanceTo(buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }

            await to.CompleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "forwarding a connection to Kestrel ended");
            await to.CompleteAsync(ex);
        }
    }

    private static async Task HandleHijackAsync(
        ConnectionContext context,
        IServiceProvider services,
        ILogger logger,
        HijackRequestHead head,
        byte[] body)
    {
        using var scope = services.CreateScope();
        try
        {
            if (head.Kind is HijackKind.Grpc or HijackKind.Session)
            {
                var options = scope.ServiceProvider.GetRequiredService<CiderOptions>();
                if (!options.BuildKitEnabled)
                {
                    // Exactly the answer StubRoutes gives a plain (non-hijacked) POST /grpc or
                    // /session: buildx's docker driver treats any non-101 as "unsupported" and
                    // reports the default builder unsupported, leaving DOCKER_BUILDKIT=0 as the
                    // user's path — the signal we want when the builder is turned off.
                    logger.LogWarning("{Kind} rejected: BuildKit is disabled", head.Kind);
                    await WriteHttpErrorAsync(context.Transport.Output, 404, "page not found");
                    return;
                }
            }

            switch (head.Kind)
            {
                case HijackKind.ExecStart:
                    await ExecStartAsync(context, scope.ServiceProvider, logger, head.Id, body);
                    break;
                case HijackKind.ContainerAttach:
                    await AttachAsync(context, scope.ServiceProvider, logger, head);
                    break;
                case HijackKind.Grpc:
                    await GrpcAsync(context, scope.ServiceProvider, logger);
                    break;
                case HijackKind.Session:
                    await SessionAsync(context, scope.ServiceProvider, logger, head);
                    break;
                default:
                    throw new InvalidOperationException($"cider: unhandled hijack kind {head.Kind}");
            }
        }
        finally
        {
            // For /grpc, TunnelTransport re-tags this same ConnectionContext and hands it to
            // Kestrel's HTTP/2 engine, which disposes it (and so this shared Transport) once the h2
            // connection tears down — before control returns here. Completing an
            // already-torn-down transport is a no-op everywhere else; this only guards the one path
            // that can get there first.
            try
            {
                await context.Transport.Output.CompleteAsync();
                await context.Transport.Input.CompleteAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// <c>POST /grpc</c>: BuildKit's control-plane connection. BuildKit is the gRPC *client* here,
    /// so the upgraded connection is handed straight to Kestrel's HTTP/2 engine over
    /// <see cref="TunnelTransport"/> as <see cref="TunnelKind.Control"/> — the daemon serves it.
    /// Dialed repeatedly and concurrently (buildx opens a new connection per <c>Client()</c> call:
    /// Features, HistoryAPISupported probe, resolver boot, build), so this returns as soon as this
    /// one connection's HTTP/2 traffic ends; it does not own the socket's lifetime beyond that.
    /// </summary>
    private static async Task GrpcAsync(ConnectionContext context, IServiceProvider services, ILogger logger)
    {
        var tunnel = services.GetRequiredService<TunnelTransport>();

        await WriteUpgradeAsync(context, "Switching Protocols", "h2c", contentType: null);
        logger.LogDebug("/grpc tunnel opened");

        await tunnel.ServeAsync(context, TunnelKind.Control, sessionId: null, meta: null, CancellationToken.None);
    }

    /// <summary>
    /// <c>POST /session</c>: a CLI session connection. Unlike <c>/grpc</c>, the CLI runs the gRPC
    /// *server* here (session/grpc.go:24-31) and the daemon must be the client — so this never hands
    /// the connection to Kestrel's HTTP/2 engine; instead it builds a <see cref="CliSession"/> (which
    /// dials out over the connection itself via <see cref="Tunnel.StreamHttp2Client"/>) and parks it
    /// in the <see cref="CliSessionRegistry"/> until the connection dies.
    /// </summary>
    private static async Task SessionAsync(
        ConnectionContext context, IServiceProvider services, ILogger logger, HijackRequestHead head)
    {
        var output = context.Transport.Output;

        if (string.IsNullOrEmpty(head.SessionId))
        {
            logger.LogWarning("/session rejected: no session id");
            await WriteHttpErrorAsync(output, 400, "cider: no session id");
            return;
        }

        if (!head.Upgrade)
        {
            logger.LogWarning("/session rejected: protocol h2c not supported");
            await WriteHttpErrorAsync(output, 400, "cider: protocol h2c not supported");
            return;
        }

        var registry = services.GetRequiredService<CliSessionRegistry>();

        // Reject a known duplicate before ever touching the wire, per buildkit's own
        // "session %s already exists" (session/manager.go) — a client that hijacked once already
        // must not get a second 101 for the same id.
        if (registry.TryGet(head.SessionId, out _))
        {
            logger.LogWarning("session {Session} rejected: already registered", head.SessionId);
            await WriteHttpErrorAsync(output, 409, $"cider: session {head.SessionId} already exists");
            return;
        }

        // Building the CliSession only wires up a SocketsHttpHandler over the transport; nothing is
        // written to the wire until the first RPC, so this is still safe to do before the 101.
        var stream = new DuplexStream(
            context.Transport.Input.AsStream(leaveOpen: true),
            context.Transport.Output.AsStream(leaveOpen: true));
        var session = new CliSession(head.SessionId, head.SessionSharedKey, head.SessionMethods ?? [], stream);

        try
        {
            registry.Register(session);
        }
        catch (InvalidOperationException)
        {
            await session.DisposeAsync();
            logger.LogWarning("session {Session} rejected: registered concurrently", head.SessionId);
            await WriteHttpErrorAsync(output, 409, $"cider: session {head.SessionId} already exists");
            return;
        }

        // CliSession's own read/write-fault detection only fires once something actually uses the
        // gRPC channel over this stream — but "the daemon need not speak first; the CLI session
        // server idles until called" means that may never happen at all for a session nothing ends
        // up needing. Kestrel's own receive loop for this connection runs regardless of whether the
        // application ever consumes Transport.Input, so ConnectionClosed is the signal that actually
        // fires when the client goes away from an otherwise-idle session.
        using var registration = context.ConnectionClosed.Register(static state => ((CliSession)state!).Close(), session);

        try
        {
            await WriteUpgradeAsync(context, "Switching Protocols", "h2c", contentType: null);
            logger.LogDebug("session {Session} registered ({Count} methods)", session.Id, session.Methods.Count);

            await session.Closed;
        }
        finally
        {
            registry.Unregister(session.Id);
            await session.DisposeAsync();
        }
    }

    private static async Task ExecStartAsync(
        ConnectionContext context,
        IServiceProvider services,
        ILogger logger,
        string execId,
        byte[] body)
    {
        var output = context.Transport.Output;
        ExecStartRequest request;
        try
        {
            request = (body.Length == 0 ? null : DockerJson.Deserialize<ExecStartRequest>(body)) ?? new ExecStartRequest();
        }
        catch (System.Text.Json.JsonException ex)
        {
            await WriteHttpErrorAsync(output, 400, $"cider: invalid exec start body: {ex.Message}");
            return;
        }

        var execs = services.GetRequiredService<ExecManager>();
        var ct = context.ConnectionClosed;

        if (request.Detach)
        {
            try
            {
                await execs.StartDetachedAsync(execId, ct);
                await WriteRawAsync(output, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            }
            catch (DockerApiException ex)
            {
                await WriteHttpErrorAsync(output, ex.StatusCode, ex.Message);
            }
            catch (Exception ex) when (!StdioPump.IsDisconnect(ex))
            {
                logger.LogError(ex, "detached exec {Exec} failed to start", execId);
                await WriteHttpErrorAsync(output, 500, $"cider: {ex.Message}");
            }

            return;
        }

        ExecSession session;
        try
        {
            session = await execs.StartAsync(execId, request.Tty, request.ConsoleSize?.ToArray(), ct);
        }
        catch (DockerApiException ex)
        {
            await WriteHttpErrorAsync(output, ex.StatusCode, ex.Message);
            return;
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "exec {Exec} failed to start", execId);
            await WriteHttpErrorAsync(output, 500, $"cider: {ex.Message}");
            return;
        }

        logger.LogDebug("hijacked exec {Exec} (tty={Tty})", execId, session.Tty);

        try
        {
            await WriteUpgradeAsync(context, "UPGRADED", "tcp", DockerResults.StreamContentType(session.Tty));
            await PumpAsync(
                context,
                session.OpenStdin,
                session.Tty,
                session.Output,
                session.WriteStdinAsync,
                session.CloseStdinAsync,
                logger);

            logger.LogDebug("hijacked exec {Exec} pump finished", execId);

            // The exit code has to be recorded before the client asks /exec/{id}/json for it.
            await session.Exited.WaitAsync(ExitGrace, CancellationToken.None);
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex) || ex is TimeoutException)
        {
            // Client gone, or the process outlived our patience.
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static async Task AttachAsync(
        ConnectionContext context,
        IServiceProvider services,
        ILogger logger,
        HijackRequestHead head)
    {
        var output = context.Transport.Output;
        var containers = services.GetRequiredService<ContainerManager>();
        var query = QueryHelpers.ParseQuery(string.IsNullOrEmpty(head.Query) ? "" : "?" + head.Query);

        var options = new AttachOptions
        {
            Stdin = Flag(query, "stdin"),
            Stdout = Flag(query, "stdout"),
            Stderr = Flag(query, "stderr"),
            Logs = Flag(query, "logs"),
            Stream = Flag(query, "stream", fallback: true),
            DetachKeys = query.TryGetValue("detachKeys", out var keys) ? keys.ToString() : null,
        };

        if (!options.Stdout && !options.Stderr && !options.Stdin)
        {
            options = options with { Stdout = true, Stderr = true };
        }

        ContainerAttachment attachment;
        try
        {
            attachment = await containers.AttachAsync(head.Id, options, context.ConnectionClosed);
        }
        catch (DockerApiException ex)
        {
            await WriteHttpErrorAsync(output, ex.StatusCode, ex.Message);
            return;
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "attach to {Container} failed", head.Id);
            await WriteHttpErrorAsync(output, 500, $"cider: {ex.Message}");
            return;
        }

        logger.LogDebug("hijacked attach to {Container} (tty={Tty})", head.Id, attachment.Tty);

        try
        {
            await WriteUpgradeAsync(context, "UPGRADED", "tcp", DockerResults.StreamContentType(attachment.Tty));
            await PumpAsync(
                context,
                options.Stdin,
                attachment.Tty,
                attachment.Output,
                attachment.WriteStdinAsync,
                attachment.CloseStdinAsync,
                logger);
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
        }
        finally
        {
            await attachment.DisposeAsync();
        }
    }

    private static async Task PumpAsync(
        ConnectionContext context,
        bool stdin,
        bool tty,
        ChannelReader<OutputChunk> output,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeStdin,
        Func<Task> closeStdin,
        ILogger logger)
    {
        await using var clientOutput = context.Transport.Output.AsStream(leaveOpen: true);
        await using var clientInput = context.Transport.Input.AsStream(leaveOpen: true);

        // No cancellation token: the pump ends when the process exits (the channel completes) or
        // when writing to the client fails. A client half-close only ends stdin, never the output.
        await StdioPump.RunAsync(
            stdin ? clientInput : null,
            clientOutput,
            tty,
            output,
            stdin ? writeStdin : null,
            stdin ? closeStdin : null,
            logger,
            CancellationToken.None);
    }

    private static bool Flag(IDictionary<string, StringValues> query, string name, bool fallback = false)
    {
        if (!query.TryGetValue(name, out var values))
        {
            return fallback;
        }

        var value = values.ToString();
        return !string.IsNullOrEmpty(value)
            && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the <c>101</c> upgrade head so that it leaves as its own socket write.
    /// <para>
    /// A <see cref="PipeWriter.FlushAsync"/> is not a socket boundary: it only hands the bytes to
    /// Kestrel's send loop, which can pick up the first stream frames in the very same <c>send()</c>.
    /// Clients that parse the head with a buffered HTTP reader (docker-py and most SDKs that are not
    /// the docker CLI) then swallow those frames along with the headers and lose them for good, so
    /// the head is written straight to the connection socket instead. Nothing has
    /// been written to the transport on this connection yet, so no output can be reordered by it.
    /// </para>
    /// <para>
    /// Two <c>send()</c> calls can still land in one <c>recv()</c> — a stream socket has no message
    /// boundaries, so a client that has not posted its read yet sees head and frames together
    /// anyway. Hence the short beat before the stream starts: it leaves the client's header read a
    /// socket carrying nothing but the head. dockerd gets that gap for free, because a container's
    /// first output never arrives this instantly.
    /// </para>
    /// <para>
    /// Parameterised for the two shapes this middleware upgrades: exec/attach's
    /// <c>101 UPGRADED</c> + <c>Upgrade: tcp</c> + a stream <paramref name="contentType"/>, and
    /// <c>/grpc</c>/<c>/session</c>'s <c>101 Switching Protocols</c> + <c>Upgrade: h2c</c> with no
    /// <c>Content-Type</c> at all (moby writes the same head: daemon/server/router/grpc/grpc_routes.go:11-45,
    /// session/manager.go:45-91).
    /// </para>
    /// </summary>
    private static async Task WriteUpgradeAsync(ConnectionContext context, string reasonPhrase, string upgradeToken, string? contentType)
    {
        var contentTypeLine = contentType is null ? "" : $"Content-Type: {contentType}\r\n";
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 {reasonPhrase}\r\n" +
            contentTypeLine +
            "Connection: Upgrade\r\n" +
            $"Upgrade: {upgradeToken}\r\n\r\n");

        if (context.Features.Get<IConnectionSocketFeature>()?.Socket is { } socket)
        {
            try
            {
                var sent = 0;
                while (sent < head.Length)
                {
                    sent += await socket.SendAsync(head.AsMemory(sent), SocketFlags.None);
                }
            }
            catch (SocketException ex)
            {
                throw new IOException("cider: writing the upgrade head failed", ex);
            }
        }
        else
        {
            // No raw socket behind this connection (in-memory transports): the flush is the best
            // boundary available.
            await context.Transport.Output.WriteAsync(head);
            await context.Transport.Output.FlushAsync();
        }

        await Task.Delay(HeadBoundary);
    }

    private static async Task WriteHttpErrorAsync(PipeWriter output, int statusCode, string message)
    {
        var payload = DockerJson.SerializeToUtf8Bytes(new ErrorResponse { Message = message });
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n");

        try
        {
            await output.WriteAsync(head);
            await output.WriteAsync(payload);
            await output.FlushAsync();
        }
        catch (Exception ex) when (StdioPump.IsDisconnect(ex))
        {
        }
    }

    private static async Task WriteRawAsync(PipeWriter output, string text)
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes(text));
        await output.FlushAsync();
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        409 => "Conflict",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "Error",
    };
}
