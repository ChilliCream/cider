using System.Text.Json;
using Cider.Core.Configuration;
using Cider.Daemon.Dns;
using Cider.Daemon.Hosting;
using Cider.E2E.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Cider.E2E.Tests;

/// <summary>
/// E2E — cider-0o3: a DNS forwarder whose owning daemon is gone (no live data dir on disk hashes to
/// it any more) is reaped by the next daemon that starts, while a still-live second daemon's own
/// forwarder is never touched. This drives genuinely throwaway daemons directly (never through the
/// shared <see cref="DaemonFixture"/> the rest of the suite uses, and never touching its collection),
/// so a hard kill can be simulated without disturbing any other test.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DnsForwarderReapingTests
{
    [E2EFact]
    public async Task Orphaned_forwarder_is_reaped_by_the_next_daemon_start_a_live_seconds_is_left_alone()
    {
        if (!DaemonFixture.Enabled)
        {
            return;
        }

        var optionsA = BuildOptions("0o3rpa");
        var optionsB = BuildOptions("0o3rpb");
        var optionsC = BuildOptions("0o3rpc");

        var forwarderA = DnsForwarderService.ForwarderName("bridge", DnsForwarderService.DataDirHash(optionsA.DataDir));
        var forwarderB = DnsForwarderService.ForwarderName("bridge", DnsForwarderService.DataDirHash(optionsB.DataDir));

        WebApplication? appB = null;
        WebApplication? appC = null;

        try
        {
            // ---- A: comes up (creating its own "bridge" forwarder, per DaemonLifecycle's warm-up),
            // then is torn down the way a hard kill leaves things: the forwarder VM keeps running
            // (StopAsync releases no forwarders -- see DnsForwarderService.StopAsync), and its data
            // dir goes away entirely, exactly like a throwaway E2E/compat run's directory eventually
            // does once nothing references it any more.
            var appA = await StartAsync(optionsA);
            await StopAsync(appA);
            Directory.Delete(optionsA.DataDir, recursive: true);

            Assert.True(
                await ForwarderExistsAsync(forwarderA),
                $"setup failed: {forwarderA} was never created");

            // ---- B: a second, still-live daemon. Its own forwarder must survive every reap below.
            appB = await StartAsync(optionsB);
            Assert.True(
                await ForwarderExistsAsync(forwarderB),
                $"setup failed: {forwarderB} was never created");

            // ---- C: a third daemon starting now must reap A's orphan (its data dir is gone) and
            // leave B's alone (optionsB.DataDir still exists on disk, so B reads as live).
            appC = await StartAsync(optionsC);

            var reaped = await DaemonFixture.EventuallyAsync(
                async () => !await ForwarderExistsAsync(forwarderA),
                TimeSpan.FromSeconds(30));
            Assert.True(reaped, $"orphaned forwarder {forwarderA} was not reaped by the next daemon's startup");

            Assert.True(
                await ForwarderExistsAsync(forwarderB),
                "a live second daemon's forwarder must never be reaped by another daemon's startup");
        }
        finally
        {
            if (appC is not null)
            {
                await StopAsync(appC);
            }

            if (appB is not null)
            {
                await StopAsync(appB);
            }

            await RemoveForwarderAsync(forwarderA);
            await RemoveForwarderAsync(forwarderB);
            await RemoveForwarderAsync(DnsForwarderService.ForwarderName("bridge", DnsForwarderService.DataDirHash(optionsC.DataDir)));

            TryRemoveDirectory(optionsA.DataDir);
            TryRemoveDirectory(optionsB.DataDir);
            TryRemoveDirectory(optionsC.DataDir);
            TryRemoveFile(optionsA.SocketPath);
            TryRemoveFile(optionsB.SocketPath);
            TryRemoveFile(optionsC.SocketPath);
        }
    }

    /// <summary>Builds throwaway options on their own <c>/tmp/cider-&lt;id&gt;-&lt;n&gt;</c> data dir/socket.</summary>
    private static CiderOptions BuildOptions(string id)
    {
        var unique = $"{id}-{Guid.NewGuid():n}"[..16];
        return new CiderOptions
        {
            DataDir = $"/tmp/cider-{unique}",
            SocketPath = $"/tmp/cider-{unique}.sock",
            LogLevel = "Information",
            DnsEnabled = true,
        };
    }

    private static async Task<WebApplication> StartAsync(CiderOptions options)
    {
        options.EnsureDirectories();
        var app = DaemonHost.Create(options, new DaemonHostSettings { DnsEnabled = true });
        await app.StartAsync();

        using var client = DaemonClient.Create(options.SocketPath, TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/_ping", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return app;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"the throwaway daemon never answered on {options.SocketPath}");
    }

    private static async Task StopAsync(WebApplication app)
    {
        try
        {
            await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        await app.DisposeAsync();
    }

    /// <summary>Straight through the Apple CLI, exactly like <c>DaemonFixture.CleanupForwarderAsync</c>.</summary>
    private static async Task<bool> ForwarderExistsAsync(string name)
    {
        var list = await Cmd.RunAsync("container", ["ls", "-a", "--format", "json"], timeout: TimeSpan.FromSeconds(60));
        if (!list.Ok || string.IsNullOrWhiteSpace(list.Stdout))
        {
            return false;
        }

        using var document = JsonDocument.Parse(list.Stdout);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("configuration", out var configuration)
                && configuration.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task RemoveForwarderAsync(string name)
    {
        await Cmd.RunAsync("container", ["stop", name], timeout: TimeSpan.FromSeconds(60));
        await Cmd.RunAsync("container", ["delete", "-f", name], timeout: TimeSpan.FromSeconds(60));
    }

    private static void TryRemoveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryRemoveFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
