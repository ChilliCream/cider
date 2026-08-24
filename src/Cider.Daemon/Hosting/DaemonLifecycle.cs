using Cider.Core.Configuration;
using Cider.Core.Health;
using Cider.Core.Net;
using Cider.Core.Restart;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Daemon.Dns;

namespace Cider.Daemon.Hosting;

/// <summary>
/// The daemon's startup and shutdown sequence: bring the engine up, reconcile the persisted state,
/// start DNS and the background supervisors — and on the way out stop them and unlink the socket.
/// A failing engine is only a warning: the daemon keeps answering <c>/_ping</c> and <c>/version</c>.
/// </summary>
public sealed class DaemonLifecycle(
    IContainerRuntime runtime,
    ContainerManager containers,
    NetworkManager networks,
    StatePoller poller,
    HealthMonitor health,
    RestartSupervisor restarts,
    IPortPublisher ports,
    CiderOptions options,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DaemonLifecycle> logger) : IHostedService
{
    private static readonly TimeSpan EngineStartTimeout = TimeSpan.FromMinutes(3);

    private DnsForwarderService? _dns;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Kestrel binds the socket in its own hosted service, which the generic host may start
        // after this one, so the mode is fixed up once everything is up.
        lifetime.ApplicationStarted.Register(SetSocketPermissions);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(EngineStartTimeout);
            await runtime.EnsureReadyAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is RuntimeException or OperationCanceledException or IOException)
        {
            logger.LogWarning(ex, "the Apple container runtime is not ready; serving anyway");
        }

        await SafeAsync("ensure the default network", () => networks.EnsureDefaultAsync(cancellationToken));
        await SafeAsync("reconcile container state", () => containers.ReconcileAsync(cancellationToken));

        _dns = services.GetService<DnsForwarderService>();
        if (_dns is not null)
        {
            // Removing a network has to take its forwarder container down first, or Apple refuses
            // the delete; the manager cannot take the service as a dependency (the service depends
            // on the manager), so it is handed over here.
            networks.SetDnsForwarders(_dns);
            await SafeAsync("start the DNS server", () => _dns.StartAsync(cancellationToken));
        }

        await SafeAsync("start the state poller", () => poller.StartAsync(cancellationToken));
        await SafeAsync("start the health monitor", () => health.StartAsync(cancellationToken));
        await SafeAsync("start the restart supervisor", () => restarts.StartAsync(cancellationToken));

        logger.LogInformation("cider is listening on {Socket}", options.SocketPath);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await SafeAsync("stop the state poller", poller.StopAsync);
        await SafeAsync("stop the health monitor", health.StopAsync);
        await SafeAsync("stop the restart supervisor", restarts.StopAsync);
        await SafeAsync("close the published ports", () =>
        {
            ports.Dispose();
            return Task.CompletedTask;
        });

        if (_dns is not null)
        {
            await SafeAsync("stop the DNS server", _dns.StopAsync);
        }

        try
        {
            if (File.Exists(options.SocketPath))
            {
                File.Delete(options.SocketPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "could not unlink the socket {Socket}", options.SocketPath);
        }
    }

    private void SetSocketPermissions()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(options.SocketPath))
            {
                File.SetUnixFileMode(
                    options.SocketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogDebug(ex, "could not chmod the socket {Socket}", options.SocketPath);
        }
    }

    private async Task SafeAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "could not {What}", what);
        }
    }
}
