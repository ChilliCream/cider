using Cider.AppleContainer;
using Cider.Core.Configuration;
using Cider.Core.DockerApi.Json;
using Cider.Core.Events;
using Cider.Core.Health;
using Cider.Core.Logs;
using Cider.Core.Net;
using Cider.Core.Restart;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Daemon.BuildKit;
using Cider.Daemon.Dns;
using Cider.Daemon.Routes;
using Cider.Daemon.Tunnel;
using Cider.Dns;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;

namespace Cider.Daemon.Hosting;

/// <summary>Knobs the <c>serve</c> verb and the integration tests share when building the daemon.</summary>
public sealed class DaemonHostSettings
{
    /// <summary>Whether the DNS server and the per-network forwarders run.</summary>
    public bool DnsEnabled { get; set; } = true;

    /// <summary>Applied after the default registrations, so tests can replace any of them.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>
/// Builds the daemon's <see cref="WebApplication"/>: Kestrel on the unix socket with the hijack
/// interceptor, the middleware chain (errors → version prefix → routing → endpoints) and every
/// manager in DI. The <c>serve</c> verb and the integration tests build the exact same host.
/// </summary>
public static class DaemonHost
{
    /// <summary>Creates the configured, not yet started, daemon application.</summary>
    public static WebApplication Create(CiderOptions options, DaemonHostSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        settings ??= new DaemonHostSettings();

        var services = new ServiceProviderHolder();
        var builder = WebApplication.CreateSlimBuilder();

        ConfigureLogging(builder, options);
        ConfigureKestrel(builder, options, services);
        ConfigureServices(builder.Services, options, settings);

        var app = builder.Build();
        services.Instance = app.Services;

        app.UseMiddleware<ErrorMiddleware>();
        app.UseMiddleware<VersionPrefixMiddleware>();
        app.UseRouting();

        app.MapSystemRoutes();
        app.MapCiderRoutes();
        app.MapContainerRoutes();
        app.MapExecRoutes();
        app.MapImageRoutes();
        app.MapBuildRoutes();
        app.MapNetworkRoutes();
        app.MapVolumeRoutes();
        app.MapStubRoutes();

        return app;
    }

    private static void ConfigureLogging(WebApplicationBuilder builder, CiderOptions options)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
            console.UseUtcTimestamp = false;
        });
        builder.Logging.Services.Configure<ConsoleLoggerOptions>(console =>
            console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(ParseLevel(options.LogLevel));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, CiderOptions options, ServiceProviderHolder holder)
    {
        DeleteStaleSocket(options.SocketPath);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.AllowSynchronousIO = false;

            // Build contexts and image loads are arbitrarily large.
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.MinResponseDataRate = null;

            // BuildKit's FileSend chunks reach 3 MiB and LLB definitions can exceed the HTTP/2
            // defaults; both windows are raised together so a large message from either side can
            // fully occupy one flow-control window without stalling on ACKs.
            kestrel.Limits.Http2.InitialStreamWindowSize = 1024 * 1024;
            kestrel.Limits.Http2.InitialConnectionWindowSize = 2 * 1024 * 1024;

            kestrel.ListenUnixSocket(options.SocketPath, listen =>
                listen.Use(next => context => HijackInterceptor.HandleAsync(context, next, holder.Require())));

            // The in-process tunnel (see Cider.Daemon.Tunnel.TunnelTransport): BuildKit's hijacked
            // /grpc and /session connections, and buildctl dial-stdio, are handed to Kestrel's
            // HTTP/2 engine here — never over a socket, so h2c prior-knowledge (which Kestrel only
            // ever speaks on a Http2-only endpoint; see cider-ger.5) is exactly what this needs.
            kestrel.Listen(new TunnelEndPoint(), listen => listen.Protocols = HttpProtocols.Http2);
        });
    }

    private static void ConfigureServices(IServiceCollection services, CiderOptions options, DaemonHostSettings settings)
    {
        services.AddSingleton(options);
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = null;
            json.SerializerOptions.DictionaryKeyPolicy = null;
            json.SerializerOptions.PropertyNameCaseInsensitive = true;
            json.SerializerOptions.Encoder = DockerJson.Options.Encoder;
        });

        services.AddSingleton<IContainerRuntime>(sp => new AppleContainerRuntime(
            new AppleContainerOptions
            {
                CliPath = options.ContainerCliPath,
                TmpDir = options.TmpDir,
            },
            sp.GetRequiredService<ILogger<AppleContainerRuntime>>()));

        services.AddSingleton(_ => new EngineId(options.DataDir));

        services.AddSingleton<IRecordStore<ContainerRecord>>(_ =>
            new JsonFileStore<ContainerRecord>(Path.Combine(options.StateDir, "containers")));
        services.AddSingleton<IRecordStore<NetworkRecord>>(_ =>
            new JsonFileStore<NetworkRecord>(Path.Combine(options.StateDir, "networks")));
        services.AddSingleton<IRecordStore<VolumeRecord>>(_ =>
            new JsonFileStore<VolumeRecord>(Path.Combine(options.StateDir, "volumes")));

        services.AddSingleton<EventBus>();
        services.AddSingleton(_ => new LogStore(options.LogsDir, options.LogMaxBytes));
        services.AddSingleton<PortAllocator>();

        // `proxy` (the default): the daemon binds the host ports and forwards into the container
        // itself. `apple`: the ports go to the engine as `-p` and nothing is published here.
        if (options.UseProxyPortPublishing)
        {
            services.AddSingleton<PortProxyManager>();
            services.AddSingleton<IPortPublisher>(sp => sp.GetRequiredService<PortProxyManager>());
        }
        else
        {
            services.AddSingleton<IPortPublisher>(NullPortPublisher.Instance);
        }

        services.AddSingleton<NameRegistry>();

        if (settings.DnsEnabled && options.DnsEnabled)
        {
            services.AddSingleton<IDnsResolver, DaemonDnsResolver>();
            services.AddSingleton<DnsForwarderService>();
            services.AddSingleton<IDnsForwarderService>(sp => sp.GetRequiredService<DnsForwarderService>());
        }
        else
        {
            services.AddSingleton<IDnsForwarderService>(NullDnsForwarderService.Instance);
        }

        services.AddSingleton<ImageManager>();
        services.AddSingleton<NetworkManager>();
        services.AddSingleton<VolumeManager>();
        services.AddSingleton<ContainerManager>();
        services.AddSingleton<IContainerCounts>(sp => sp.GetRequiredService<ContainerManager>());
        services.AddSingleton<ExecManager>();
        services.AddSingleton<SystemManager>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<RestartSupervisor>();
        services.AddSingleton<StatePoller>();
        services.AddSingleton<StateSynchronizer>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DaemonLifecycle>());

        // The in-process tunnel transport (see Cider.Daemon.Tunnel.TunnelTransport) and the gRPC
        // server plumbing every mapped BuildKit service needs. IgnoreUnknownServices is required,
        // not cosmetic: without it grpc-dotnet maps a catch-all unimplemented-service endpoint at
        // routing Order 0 that beats any MapFallback (Order int.MaxValue), so every request to a
        // service we have not mapped yet would get grpc-status 12 from the wrong place. Message
        // size limits are unbounded for the same reason DaemonHost lifts MaxRequestBodySize above:
        // FileSend chunks and LLB definitions routinely exceed grpc-dotnet's 4 MB default.
        services.AddSingleton<TunnelTransport>();
        services.AddSingleton<IConnectionListenerFactory>(sp => sp.GetRequiredService<TunnelTransport>());

        // Every CLI session dialed through the hijacked POST /session (see HijackInterceptor); a
        // build's Control/Solve can name a session id before its connection has upgraded, hence
        // CliSessionRegistry.WaitAsync rather than only a synchronous lookup.
        services.AddSingleton<CliSessionRegistry>();

        services.AddGrpc(grpc =>
        {
            grpc.IgnoreUnknownServices = true;
            grpc.MaxReceiveMessageSize = null;
            grpc.MaxSendMessageSize = null;
        });

        settings.ConfigureServices?.Invoke(services);
    }

    private static void DeleteStaleSocket(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"cider: cannot remove the stale socket {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Maps a configured log level name onto <see cref="LogLevel"/>, defaulting to Information.</summary>
    public static LogLevel ParseLevel(string? level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;

    private sealed class ServiceProviderHolder
    {
        public IServiceProvider? Instance { get; set; }

        public IServiceProvider Require() =>
            Instance ?? throw new InvalidOperationException("the service provider is not available yet");
    }
}
