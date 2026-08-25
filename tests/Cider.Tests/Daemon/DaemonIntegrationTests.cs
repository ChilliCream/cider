using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>The daemon driven over its real unix socket with raw HTTP, exactly like a Docker client.</summary>
public sealed class DaemonIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Ping_answers_with_dockers_negotiation_headers()
    {
        await using var host = await DaemonTestHost.StartAsync();

        using var response = await host.Client.GetAsync(new Uri("/_ping", UriKind.Relative));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("OK", await response.Content.ReadAsStringAsync());
        Assert.Equal("1.47", Assert.Single(response.Headers.GetValues("API-Version")));
        Assert.Equal("2", Assert.Single(response.Headers.GetValues("Builder-Version")));
        Assert.Equal("false", Assert.Single(response.Headers.GetValues("Docker-Experimental")));
        Assert.Equal("linux", Assert.Single(response.Headers.GetValues("Ostype")));
        Assert.Equal("inactive", Assert.Single(response.Headers.GetValues("Swarm")));
    }

    [Fact]
    public async Task Ping_reports_BuilderVersion_1_when_BuildKit_is_disabled()
    {
        await using var host = await DaemonTestHost.StartAsync(options => options.BuildKitEnabled = false);

        using var response = await host.Client.GetAsync(new Uri("/_ping", UriKind.Relative));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("Builder-Version")));
    }

    [Fact]
    public async Task Ping_answers_HEAD_too()
    {
        await using var host = await DaemonTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("/_ping", UriKind.Relative));
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("1.47", Assert.Single(response.Headers.GetValues("API-Version")));
    }

    [Fact]
    public async Task Version_reports_a_parsable_engine_version()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (status, body) = await host.GetAsync("/v1.47/version");
        using var json = JsonDocument.Parse(body);

        Assert.Equal(200, status);
        Assert.Equal("29.0.0", json.RootElement.GetProperty("Version").GetString());
        Assert.Equal("1.47", json.RootElement.GetProperty("ApiVersion").GetString());
        Assert.Equal("1.24", json.RootElement.GetProperty("MinAPIVersion").GetString());
        Assert.Equal("linux", json.RootElement.GetProperty("Os").GetString());
        Assert.Equal("arm64", json.RootElement.GetProperty("Arch").GetString());
        Assert.Equal("Engine", json.RootElement.GetProperty("Components")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Info_reports_an_inactive_swarm()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (status, body) = await host.GetAsync("/info");
        using var json = JsonDocument.Parse(body);

        Assert.Equal(200, status);
        Assert.Equal("inactive", json.RootElement.GetProperty("Swarm").GetProperty("LocalNodeState").GetString());
        Assert.False(json.RootElement.GetProperty("Swarm").GetProperty("ControlAvailable").GetBoolean());
        Assert.Equal("apple-container", json.RootElement.GetProperty("Driver").GetString());
        Assert.Equal("linux", json.RootElement.GetProperty("OSType").GetString());

        // Must stay empty: a [["driver-type","io.containerd.snapshotter.v1"]] entry would make
        // buildx rewrite `docker build --load` to the oci exporter instead of loading the image.
        Assert.Empty(json.RootElement.GetProperty("DriverStatus").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_paths_answer_dockers_page_not_found()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (status, body) = await host.GetAsync("/v1.47/nonsense");

        Assert.Equal(404, status);
        Assert.Contains("page not found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_start_wait_logs_and_delete_a_container()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (createStatus, createBody) = await host.PostJsonAsync(
            "/v1.47/containers/create?name=t1",
            """{"Image":"alpine","Cmd":["sh","-c","echo hi"],"Tty":false}""");

        Assert.Equal(201, createStatus);
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        Assert.Equal(64, id.Length);

        var (startStatus, _) = await host.PostJsonAsync($"/containers/{id}/start");
        Assert.Equal(204, startStatus);

        var (waitStatus, waitBody) = await host.PostJsonAsync($"/containers/{id}/wait");
        Assert.Equal(200, waitStatus);
        Assert.Equal(0, JsonDocument.Parse(waitBody).RootElement.GetProperty("StatusCode").GetInt32());

        using var logs = await host.Client.GetAsync(new Uri($"/containers/{id}/logs?stdout=1&stderr=1", UriKind.Relative));
        Assert.Equal(200, (int)logs.StatusCode);
        Assert.Equal("application/vnd.docker.multiplexed-stream", logs.Content.Headers.ContentType?.MediaType);

        var frames = await logs.Content.ReadAsByteArrayAsync();
        Assert.True(frames.Length > 8);
        Assert.Equal(1, frames[0]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(frames.AsSpan(4, 4));
        Assert.Equal("hi\n", Encoding.UTF8.GetString(frames, 8, (int)length));

        var (listStatus, listBody) = await host.GetAsync("/containers/json?all=1");
        Assert.Equal(200, listStatus);
        Assert.Contains("/t1", listBody, StringComparison.Ordinal);

        Assert.Equal(204, await host.DeleteAsync($"/containers/{id}"));

        var (goneStatus, goneBody) = await host.GetAsync($"/containers/{id}/json");
        Assert.Equal(404, goneStatus);
        Assert.Contains("No such container", goneBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_of_a_running_container_is_not_modified()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=sleeper",
            """{"Image":"alpine","Cmd":["sleep","30"]}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;

        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);
        Assert.Equal(304, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/stop")).Status);
        Assert.Equal(204, await host.DeleteAsync($"/containers/{id}"));
    }

    [Fact]
    public async Task Exec_can_be_created_and_inspected()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=execbox",
            """{"Image":"alpine","Cmd":["sleep","30"],"OpenStdin":true}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        Assert.Equal(204, (await host.PostJsonAsync($"/containers/{id}/start")).Status);

        var (execStatus, execBody) = await host.PostJsonAsync(
            $"/containers/{id}/exec",
            """{"AttachStdout":true,"AttachStderr":true,"Cmd":["sh","-c","echo hi"]}""");

        Assert.Equal(201, execStatus);
        var execId = JsonDocument.Parse(execBody).RootElement.GetProperty("Id").GetString()!;

        var (inspectStatus, inspectBody) = await host.GetAsync($"/exec/{execId}/json");
        using var json = JsonDocument.Parse(inspectBody);

        Assert.Equal(200, inspectStatus);
        Assert.Equal(execId, json.RootElement.GetProperty("ID").GetString());
        Assert.False(json.RootElement.GetProperty("Running").GetBoolean());
        Assert.Equal(id, json.RootElement.GetProperty("ContainerID").GetString());

        Assert.Equal(404, (await host.GetAsync("/exec/0123456789abcdef/json")).Status);
    }

    [Fact]
    public async Task Exec_start_without_upgrade_streams_the_output()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=execstream",
            """{"Image":"alpine","Cmd":["sleep","30"]}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        await host.PostJsonAsync($"/containers/{id}/start");

        var (_, execBody) = await host.PostJsonAsync(
            $"/containers/{id}/exec",
            """{"AttachStdout":true,"AttachStderr":true,"Cmd":["sh","-c","echo out; echo err 1>&2; exit 3"]}""");
        var execId = JsonDocument.Parse(execBody).RootElement.GetProperty("Id").GetString()!;

        using var content = new StringContent("""{"Detach":false,"Tty":false}""", Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync(new Uri($"/exec/{execId}/start", UriKind.Relative), content);

        Assert.Equal(200, (int)response.StatusCode);
        var payload = await response.Content.ReadAsByteArrayAsync();
        var text = Decode(payload);
        Assert.Contains("out", text, StringComparison.Ordinal);
        Assert.Contains("err", text, StringComparison.Ordinal);

        var (_, inspectBody) = await host.GetAsync($"/exec/{execId}/json");
        Assert.Equal(3, JsonDocument.Parse(inspectBody).RootElement.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task Events_stream_reports_create_and_start()
    {
        await using var host = await DaemonTestHost.StartAsync();

        using var cts = new CancellationTokenSource(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/events", UriKind.Relative));
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(200, (int)response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create?name=evented",
            """{"Image":"alpine","Cmd":["sleep","30"]}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;
        await host.PostJsonAsync($"/containers/{id}/start");

        var actions = new List<string>();
        while (actions.Count < 2 && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            using var message = JsonDocument.Parse(line);
            if (message.RootElement.GetProperty("Actor").GetProperty("ID").GetString() == id)
            {
                actions.Add(message.RootElement.GetProperty("Action").GetString()!);
            }
        }

        Assert.Contains("create", actions, StringComparer.Ordinal);
        Assert.Contains("start", actions, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Pause_is_reported_as_unsupported()
    {
        await using var host = await DaemonTestHost.StartAsync();

        var (_, createBody) = await host.PostJsonAsync(
            "/containers/create",
            """{"Image":"alpine","Cmd":["sleep","30"]}""");
        var id = JsonDocument.Parse(createBody).RootElement.GetProperty("Id").GetString()!;

        var (status, body) = await host.PostJsonAsync($"/containers/{id}/pause");

        Assert.Equal(501, status);
        Assert.Contains("cider: pause is not supported", body, StringComparison.Ordinal);
    }

    /// <summary>Strips Docker's stdcopy framing so tests can assert on the text.</summary>
    private static string Decode(byte[] framed)
    {
        var text = new StringBuilder();
        var offset = 0;
        while (offset + 8 <= framed.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(offset + 4, 4));
            offset += 8;
            if (offset + length > framed.Length)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(framed, offset, length));
            offset += length;
        }

        return text.ToString();
    }
}
