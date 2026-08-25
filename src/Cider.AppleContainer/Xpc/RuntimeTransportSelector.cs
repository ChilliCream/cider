using System.Runtime.CompilerServices;
using Cider.Core.Configuration;
using Cider.Core.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cider.AppleContainer.Xpc;

/// <summary>The runtime plus what it can do, as decided by <see cref="RuntimeTransportSelector.SelectAsync"/>.</summary>
public sealed record RuntimeSelection(IContainerRuntime Runtime, RuntimeCapabilities Capabilities);

/// <summary>
/// Decides XPC vs. the CLI at startup and gates the choice on the apiserver's reported version
/// (task cider-ede.4's fix direction). Everything above this point in the file scope only ever asks
/// for the runtime and its capabilities through here — no other type constructs the CLI fallback
/// runtime for daemon use.
///
/// Until cider-ede.5 lands <c>XpcContainerRuntime</c>, <see cref="RuntimeSelection.Runtime"/> is
/// always the CLI-backed <see cref="AppleContainerRuntime"/>, even when
/// <see cref="RuntimeSelection.Capabilities"/> reports <see cref="RuntimeTransportKind.Xpc"/>: the
/// version-gate decision and its logging already run for real (task item 4 — "the selector returns
/// the CLI runtime for now but already performs the ping/version logic and logs it, so this task is
/// testable alone"), but nothing yet exists to make an XPC decision behave differently for a caller
/// of <see cref="RuntimeSelection.Runtime"/>. cider-ede.5 is expected to swap that runtime construction
/// for a real <c>XpcContainerRuntime</c> when the decision is <see cref="RuntimeTransportKind.Xpc"/>.
/// </summary>
public static class RuntimeTransportSelector
{
    /// <summary>docs/spikes/xpc/02-apiserver-xpc-protocol.md §1.1.</summary>
    internal const string ApiServerService = "com.apple.container.apiserver";

    /// <summary><c>ping</c>'s own timeout (task fix direction §2; also
    /// docs/spikes/xpc/02-apiserver-xpc-protocol.md line 86, "ping from system status").</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long <c>auto</c> waits for <c>container system start</c> before giving up and
    /// falling back to the CLI runtime — the same budget <c>DaemonLifecycle.EnsureReadyAsync</c>
    /// already gives the engine at startup.</summary>
    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromMinutes(3);

    private static readonly Version MaskedPathsMinimum = new(1, 2, 0);

    /// <summary>Requested transport, parsed from <see cref="CiderOptions.RuntimeTransport"/>.</summary>
    private enum RequestedTransport
    {
        Auto,
        Xpc,
        Cli,
    }

    private static readonly ConditionalWeakTable<CiderOptions, Lazy<Task<RuntimeSelection>>> Cache = new();

    /// <summary>
    /// Runs <see cref="SelectAsync"/> at most once per <paramref name="options"/> instance — callers
    /// that each resolve their own DI singleton (<c>IContainerRuntime</c>, <c>RuntimeCapabilities</c>)
    /// share one ping, one possible <c>container system start</c>, and one startup log line instead
    /// of each redoing all three.
    /// </summary>
    public static Task<RuntimeSelection> SelectOnceAsync(
        CiderOptions options, ILoggerFactory loggerFactory, CancellationToken ct = default, bool autoStartServices = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lazy = Cache.GetValue(
            options,
            _ => new Lazy<Task<RuntimeSelection>>(() => SelectAsync(options, loggerFactory, ct, autoStartServices)));
        return lazy.Value;
    }

    /// <summary>
    /// <c>cli</c> → the CLI runtime, no ping. <c>xpc</c>/<c>auto</c> → pings the apiserver (10 s
    /// budget); on failure, <c>auto</c> tries <c>container system start</c> via the CLI runtime's own
    /// <see cref="IContainerRuntime.EnsureReadyAsync"/> (when <paramref name="autoStartServices"/>,
    /// skipped for read-only callers such as <c>cider status</c>) and pings again. A version below
    /// <see cref="ApiServerVersion.Minimum"/> warns and falls back in <c>auto</c>, or fails fast
    /// (<see cref="RuntimeException"/>, <see cref="RuntimeErrorKind.Unavailable"/>) in <c>xpc</c>. A
    /// version above <see cref="ApiServerVersion.Tested"/> only logs and proceeds.
    /// </summary>
    public static async Task<RuntimeSelection> SelectAsync(
        CiderOptions options, ILoggerFactory loggerFactory, CancellationToken ct, bool autoStartServices = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("Cider.AppleContainer.Xpc.RuntimeTransportSelector");
        var cli = new AppleContainerRuntime(
            new AppleContainerOptions { CliPath = options.ContainerCliPath, TmpDir = options.TmpDir },
            loggerFactory.CreateLogger<AppleContainerRuntime>());

        var requested = ParseRequestedTransport(options.RuntimeTransport);
        if (requested == RequestedTransport.Cli)
        {
            logger.LogInformation("runtime transport: cli (configured)");
            return new RuntimeSelection(cli, new RuntimeCapabilities
            {
                Transport = RuntimeTransportKind.Cli,
                NetworkCreate = NetworkCreateSupported(),
            });
        }

        var (version, pingError) = await TryPingAsync(ct).ConfigureAwait(false);

        if (version is null && requested == RequestedTransport.Auto && autoStartServices)
        {
            logger.LogInformation(
                "apiserver did not respond to ping ({Reason}); starting Apple container services", pingError);
            try
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startCts.CancelAfter(ServiceStartTimeout);
                await cli.EnsureReadyAsync(startCts.Token).ConfigureAwait(false);
                (version, pingError) = await TryPingAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RuntimeException or OperationCanceledException or IOException)
            {
                pingError = $"could not start Apple container services: {ex.Message}";
            }
        }

        if (version is null)
        {
            var reason = $"apiserver unreachable: {pingError}";
            if (requested == RequestedTransport.Xpc)
            {
                throw RuntimeException.Unavailable($"runtime transport 'xpc' requested but {reason}");
            }

            logger.LogWarning("runtime transport: cli ({Reason})", reason);
            return new RuntimeSelection(cli, new RuntimeCapabilities
            {
                Transport = RuntimeTransportKind.Cli,
                FallbackReason = reason,
                NetworkCreate = NetworkCreateSupported(),
            });
        }

        if (version.IsBelowMinimum)
        {
            var reason = $"apiserver {version.Semver} is older than the minimum supported {ApiServerVersion.Minimum} ({version.RawBanner})";
            if (requested == RequestedTransport.Xpc)
            {
                throw RuntimeException.Unavailable($"runtime transport 'xpc' requested but {reason}");
            }

            logger.LogWarning("runtime transport: cli ({Reason})", reason);
            return new RuntimeSelection(cli, new RuntimeCapabilities
            {
                Transport = RuntimeTransportKind.Cli,
                ApiServerVersion = version,
                FallbackReason = reason,
                NetworkCreate = NetworkCreateSupported(),
            });
        }

        if (version.IsNewerThanTested)
        {
            logger.LogInformation(
                "apiserver {Version} is newer than the last version cider was tested against ({Tested}); proceeding untested",
                version.Semver, ApiServerVersion.Tested);
        }

        logger.LogInformation(
            "runtime transport: xpc, apiserver {Version} ({Build} {Commit}), min {Minimum}, tested {Tested}",
            version.Semver, version.Build, version.Commit, ApiServerVersion.Minimum, ApiServerVersion.Tested);

        return new RuntimeSelection(cli, new RuntimeCapabilities
        {
            Transport = RuntimeTransportKind.Xpc,
            ApiServerVersion = version,
            NetworkCreate = NetworkCreateSupported(),
            MaskedPaths = version.Semver >= MaskedPathsMinimum,
        });
    }

    /// <summary>Sends one <c>ping</c> on its own short-lived client. Returns <c>(null, reason)</c> on
    /// any transport failure or an unparseable <c>apiServerVersion</c> banner, never throws for
    /// those — a real <paramref name="ct"/> cancellation still propagates.</summary>
    private static async Task<(ApiServerVersion? Version, string? Error)> TryPingAsync(CancellationToken ct)
    {
        using var client = new XpcClient(ApiServerService, NullLogger.Instance);
        try
        {
            using var reply = await client
                .SendAsync(new XpcMessage("ping"), new XpcCallOptions { Timeout = PingTimeout }, ct)
                .ConfigureAwait(false);
            var banner = reply.GetString("apiServerVersion");
            return ApiServerVersion.TryParse(banner, out var version)
                ? (version, null)
                : (null, $"ping succeeded but its apiServerVersion banner did not parse: '{banner}'");
        }
        catch (XpcException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary><c>networkCreate</c> exists only on macOS 26+ (task description).</summary>
    private static bool NetworkCreateSupported() => Environment.OSVersion.Version.Major >= 26;

    private static RequestedTransport ParseRequestedTransport(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        CiderOptions.XpcRuntimeTransport => RequestedTransport.Xpc,
        CiderOptions.CliRuntimeTransport => RequestedTransport.Cli,
        // "auto", empty, or anything unrecognised — the same forgiving style as
        // CiderOptions.UseProxyPortPublishing ("anything other than apple means proxy").
        _ => RequestedTransport.Auto,
    };
}
