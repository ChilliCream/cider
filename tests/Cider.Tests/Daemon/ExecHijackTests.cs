using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cider.AppleContainer;
using Cider.AppleContainer.Cli;
using Cider.AppleContainer.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// The hijacked <c>POST /exec/{id}/start</c> stream, driven over the raw socket the way the docker
/// CLI does it. A tty exec that exits the moment it is done writing must still deliver every byte
/// before the daemon closes the connection.
/// </summary>
public class ExecHijackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_fast_exiting_tty_exec_delivers_every_byte_before_the_connection_closes()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=hijackbox",
            """{"Image":"alpine","Cmd":["sleep","30"],"Tty":true,"OpenStdin":true}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        var payload = new string('x', 4000);
        var expected = $"{payload}\nTAIL-MARKER\n";

        // Repeated: the truncation this guards against only shows up when the process exit races
        // the stdout pump and the channel completion.
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var (_, execBody) = await host.PostJsonAsync(
                $"/containers/{id}/exec",
                $$"""{"AttachStdin":true,"AttachStdout":true,"AttachStderr":true,"Tty":true,"Cmd":["sh","-c","echo {{payload}}; echo TAIL-MARKER"]}""");
            var execId = JsonDocument.Parse(execBody).RootElement.GetProperty("Id").GetString()!;

            var (head, body) = await StartHijackedAsync(host.SocketPath, execId);

            Assert.StartsWith("HTTP/1.1 101 UPGRADED", head, StringComparison.Ordinal);
            Assert.Contains("application/vnd.docker.raw-stream", head, StringComparison.Ordinal);
            Assert.Equal(expected, body);

            var (_, inspectBody) = await host.GetAsync($"/exec/{execId}/json");
            var inspect = JsonDocument.Parse(inspectBody).RootElement;
            Assert.False(inspect.GetProperty("Running").GetBoolean());
            Assert.Equal(0, inspect.GetProperty("ExitCode").GetInt32());
        }
    }

    /// <summary>
    /// The same thing once more, but with a real pty behind the exec instead of the in-memory fake:
    /// a shell that writes a burst and exits immediately must reach the client in full, pty banner
    /// filter, stdio pumps, channel completion and connection teardown included.
    /// </summary>
    [Fact]
    public async Task A_tty_exec_on_a_real_pty_delivers_every_byte()
    {
        var launcher = new ProcessLauncher(
            new ContainerCli(new AppleContainerOptions { CliPath = "/bin/sh" }, NullLogger.Instance),
            NullLogger.Instance);

        await using var host = await DaemonTestHost.StartAsync();
        host.Runtime.ExecFactory = spec => launcher.StartPty(
            [.. spec.Argv.Skip(1)],
            cols: 100,
            rows: 24,
            signalDelegate: null);

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=ptyhijackbox",
            """{"Image":"alpine","Cmd":["sleep","30"],"Tty":true,"OpenStdin":true}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (_, execBody) = await host.PostJsonAsync(
                $"/containers/{id}/exec",
                """{"AttachStdin":true,"AttachStdout":true,"AttachStderr":true,"Tty":true,"Cmd":["sh","-c","i=0; while [ $i -lt 200 ]; do echo line$i; i=$((i+1)); done; echo TAIL-MARKER"]}""");
            var execId = JsonDocument.Parse(execBody).RootElement.GetProperty("Id").GetString()!;

            var (head, body) = await StartHijackedAsync(host.SocketPath, execId);

            Assert.StartsWith("HTTP/1.1 101 UPGRADED", head, StringComparison.Ordinal);
            Assert.Contains("line0", body, StringComparison.Ordinal);
            Assert.Contains("line199", body, StringComparison.Ordinal);
            Assert.Contains("TAIL-MARKER", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The <c>101 UPGRADED</c> head must leave the daemon as its own socket write. docker-py and
    /// every other buffered-HTTP client parse the head with a buffered reader and then switch to
    /// the raw socket: anything that shares the receive which completes the headers is swallowed by
    /// that reader and never reaches the caller. So this asserts the read boundary — the receive
    /// that ends the head carries nothing but the head, and the frames arrive in a later read.
    /// </summary>
    [Fact]
    public async Task The_upgrade_head_arrives_in_a_read_of_its_own()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=boundarybox",
            """{"Image":"alpine","Cmd":["sleep","30"],"OpenStdin":true}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        // Repeated: the coalescing this guards against is a race between the head and the first
        // output chunk, and a single round can get lucky.
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (_, execBody) = await host.PostJsonAsync(
                $"/containers/{id}/exec",
                """{"AttachStdout":true,"AttachStderr":true,"Cmd":["echo","hijackprobe"]}""");
            var execId = JsonDocument.Parse(execBody).RootElement.GetProperty("Id").GetString()!;

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(host.SocketPath)).WaitAsync(Timeout);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await SendHijackRequestAsync(stream, execId, tty: false);

            var buffer = new byte[8192];
            var received = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
                Assert.True(read > 0, "the connection closed before the upgrade head arrived");
                received.Write(buffer, 0, read);

                var text = Encoding.ASCII.GetString(received.ToArray());
                var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headEnd < 0)
                {
                    continue;
                }

                Assert.StartsWith("HTTP/1.1 101 UPGRADED", text, StringComparison.Ordinal);
                Assert.True(
                    headEnd + 4 == received.Length,
                    $"iteration {iteration}: the head shared its read with {received.Length - (headEnd + 4)} " +
                    $"byte(s) of stream payload, which a buffered client loses: {Escape(text[(headEnd + 4)..])}");
                break;
            }

            // The frames must still be there for the raw socket the client switches to.
            var payload = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
                if (read <= 0)
                {
                    break;
                }

                payload.Write(buffer, 0, read);
            }

            Assert.Contains("hijackprobe", Encoding.UTF8.GetString(payload.ToArray()), StringComparison.Ordinal);
        }
    }

    private static string Escape(string text) =>
        string.Concat(text.Select(c => c is >= ' ' and < (char)127 ? c.ToString() : $"\\x{(int)c:x2}"));

    /// <summary>Sends the hijack request and reads the whole upgraded stream to EOF.</summary>
    private static async Task<(string Head, string Body)> StartHijackedAsync(string socketPath, string execId)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).WaitAsync(Timeout);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        await SendHijackRequestAsync(stream, execId, tty: true);

        var collected = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
            if (read <= 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
        }

        var text = Encoding.UTF8.GetString(collected.ToArray());
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator > 0, $"no response head in: {text}");

        return (text[..separator], text[(separator + 4)..]);
    }

    /// <summary>Writes a hijacking <c>POST /exec/{id}/start</c> the way the docker CLI does.</summary>
    private static async Task SendHijackRequestAsync(Stream stream, string execId, bool tty)
    {
        var json = Encoding.ASCII.GetBytes($$"""{"Detach":false,"Tty":{{(tty ? "true" : "false")}}}""");
        var request = Encoding.ASCII.GetBytes(
            $"POST /exec/{execId}/start HTTP/1.1\r\n" +
            "Host: docker\r\n" +
            "Content-Type: application/json\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: tcp\r\n" +
            $"Content-Length: {json.Length}\r\n\r\n");

        await stream.WriteAsync(request).AsTask().WaitAsync(Timeout);
        await stream.WriteAsync(json).AsTask().WaitAsync(Timeout);
        await stream.FlushAsync().WaitAsync(Timeout);
    }
}
