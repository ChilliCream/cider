using System.Net.Http.Headers;
using Cider.Core.Configuration;
using Cider.Core.Net;
using Cider.Core.Runtime;
using Cider.Core.State;
using Cider.Daemon.Hosting;
using Cider.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cider.Tests.Daemon;

/// <summary>
/// The real daemon, in process: the very same <see cref="DaemonHost"/> the <c>serve</c> verb builds,
/// but on a throwaway unix socket with the fake engine, in-memory stores and no DNS.
/// </summary>
public sealed class DaemonTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DaemonTestHost(WebApplication app, CiderOptions options, FakeContainerRuntime runtime)
    {
        _app = app;
        Options = options;
        Runtime = runtime;
        Client = DaemonClient.Create(options.SocketPath, TimeSpan.FromSeconds(30));
    }

    /// <summary>The daemon's configuration (temporary data dir, temporary socket).</summary>
    public CiderOptions Options { get; }

    /// <summary>The fake engine behind the daemon.</summary>
    public FakeContainerRuntime Runtime { get; }

    /// <summary>An <see cref="HttpClient"/> bound to the daemon's socket.</summary>
    public HttpClient Client { get; }

    /// <summary>The socket the daemon listens on.</summary>
    public string SocketPath => Options.SocketPath;

    /// <summary>Starts a daemon on a fresh socket and waits until it answers <c>/_ping</c>.</summary>
    public static async Task<DaemonTestHost> StartAsync(Action<CiderOptions>? configure = null)
    {
        var id = Guid.NewGuid().ToString("n")[..10];
        var dataDir = Path.Combine(Path.GetTempPath(), "ad-daemon-tests", id);
        var options = new CiderOptions
        {
            DataDir = dataDir,
            // sockaddr_un.sun_path is 104 bytes on macOS: keep the path short.
            SocketPath = $"/tmp/cider-test-{id}.sock",
            LogLevel = Environment.GetEnvironmentVariable("CIDER_TEST_LOGLEVEL") ?? "Warning",
            DnsEnabled = false,
            PollIntervalSeconds = 1,
        };

        configure?.Invoke(options);
        options.EnsureDirectories();

        var runtime = new FakeContainerRuntime();
        var app = DaemonHost.Create(options, new DaemonHostSettings
        {
            DnsEnabled = false,
            ConfigureServices = services =>
            {
                services.AddSingleton<IContainerRuntime>(runtime);
                services.AddSingleton<IRecordStore<ContainerRecord>>(new InMemoryRecordStore<ContainerRecord>());
                services.AddSingleton<IRecordStore<NetworkRecord>>(new InMemoryRecordStore<NetworkRecord>());
                services.AddSingleton<IRecordStore<VolumeRecord>>(new InMemoryRecordStore<VolumeRecord>());
                services.AddSingleton<IDnsForwarderService>(NullDnsForwarderService.Instance);
            },
        });

        await app.StartAsync();

        var host = new DaemonTestHost(app, options, runtime);
        await host.WaitForPingAsync();
        return host;
    }

    /// <summary>A GET whose response body is read as a string.</summary>
    public async Task<(int Status, string Body)> GetAsync(string path)
    {
        using var response = await Client.GetAsync(new Uri(path, UriKind.Relative));
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A POST with an optional JSON body whose response body is read as a string.</summary>
    public async Task<(int Status, string Body)> PostJsonAsync(string path, string? json = null)
    {
        using var content = new StringContent(json ?? "{}");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await Client.PostAsync(new Uri(path, UriKind.Relative), content);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A DELETE whose response status is returned.</summary>
    public async Task<int> DeleteAsync(string path)
    {
        using var response = await Client.DeleteAsync(new Uri(path, UriKind.Relative));
        return (int)response.StatusCode;
    }

    private async Task WaitForPingAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await Client.GetAsync(new Uri("/_ping", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"the test daemon never answered on {SocketPath}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            await _app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        await _app.DisposeAsync();

        try
        {
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }

            if (Directory.Exists(Options.DataDir))
            {
                Directory.Delete(Options.DataDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
