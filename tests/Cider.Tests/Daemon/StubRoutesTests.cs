using System.Net;
using System.Net.Sockets;
using Cider.Daemon.Hosting;
using Cider.Daemon.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cider.Tests.Daemon;

/// <summary>
/// Per-verb coverage for <see cref="StubRoutes"/>'s swarm-family answers:
/// dockerd doesn't blanket-501 a non-swarm node, so neither should this daemon. Hosted in-process
/// on a temporary unix socket with only <see cref="StubRoutes"/> mapped — no manager/runtime
/// dependencies are needed for any route this file exercises.
/// </summary>
public sealed class StubRoutesTests : IAsyncLifetime
{
    private readonly string _socketPath = $"/tmp/cider-sr-{Guid.NewGuid():N}"[..24] + ".sock";

    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(_socketPath));

        _app = builder.Build();
        _app.UseMiddleware<ErrorMiddleware>();
        _app.MapStubRoutes();

        await _app.StartAsync();

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri("http://cider-tests/") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (IOException)
        {
        }
    }

    private const string NotPartOfSwarm = "This node is not part of a swarm";
    private const string NotSwarmManager = "not a swarm manager";

    [Fact]
    public async Task SwarmLeave_Force_ReturnsNotAcceptable_NotPartOfSwarm()
    {
        // The repro: real dockerd on a non-swarm node answers 406, which is
        // exactly what docker-py's `leave_swarm(force=True)` tolerates without raising.
        var response = await _client!.PostAsync("/swarm/leave?force=true", content: null);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotPartOfSwarm, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwarmLeave_WithoutForce_ReturnsNotAcceptable_NotPartOfSwarm()
    {
        var response = await _client!.PostAsync("/swarm/leave", content: null);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotPartOfSwarm, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwarmUpdate_ReturnsServiceUnavailable_NotSwarmManager()
    {
        // `Cluster.Update` runs through the same manager gate as `GET /swarm` (not the `errNoSwarm`
        // short-circuit `leave` gets), so a non-swarm node 503s here, not 406.
        var response = await _client!.PostAsync("/swarm/update?version=0", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotSwarmManager, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwarmUnlockkey_ReturnsServiceUnavailable_NotSwarmManager()
    {
        var response = await _client!.GetAsync("/swarm/unlockkey");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotSwarmManager, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwarmUnlock_ReturnsServiceUnavailable_NotSwarmManager()
    {
        var response = await _client!.PostAsync("/swarm/unlock", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotSwarmManager, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/swarm/init")]
    [InlineData("/swarm/join")]
    public async Task SwarmInitAndJoin_StayNotImplemented(string path)
    {
        // On a real non-swarm node these *succeed* — there's no "not part of a swarm" error to
        // mirror, and swarm mode itself is a non-goal — so they stay in the generic 501 sweep.
        var response = await _client!.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/services")]
    [InlineData("GET", "/services/some-id")]
    [InlineData("POST", "/services/create")]
    [InlineData("GET", "/tasks")]
    [InlineData("GET", "/nodes")]
    [InlineData("GET", "/nodes/some-id")]
    [InlineData("GET", "/secrets")]
    [InlineData("POST", "/secrets/create")]
    [InlineData("GET", "/configs")]
    [InlineData("POST", "/configs/create")]
    public async Task ManagerOnlyResources_ReturnServiceUnavailable_NotSwarmManager(string method, string path)
    {
        // dockerd routes services/tasks/nodes/secrets/configs through the same manager gate as
        // `/swarm/update`, so a non-swarm node 503s every verb here — not the generic 501.
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotSwarmManager, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugins_StayNotImplemented()
    {
        // Plugins aren't swarm-gated in dockerd at all — a different subsystem, out of this
        // ticket's scope — so the blanket "not supported" 501 is still the right answer.
        var response = await _client!.GetAsync("/plugins");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}
