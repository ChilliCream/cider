using System.Reflection;
using System.Text.Json;
using Cider.AppleContainer;
using Cider.Core.Configuration;
using Cider.Daemon.Hosting;
using Cider.Daemon.Install;
using Cider.Daemon.Routes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cider.Daemon;

/// <summary>The <c>cider</c> executable: <c>serve</c> (default), <c>install</c>, <c>uninstall</c>, <c>status</c>, <c>sync</c>, <c>version</c>.</summary>
public static class Program
{
    /// <summary>Entry point; returns the process exit code.</summary>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        try
        {
            return verb switch
            {
                "serve" => await ServeAsync(rest),
                "install" => await InstallAsync(rest),
                "uninstall" => await UninstallAsync(rest),
                "status" => await StatusAsync(rest),
                "sync" => await SyncAsync(rest),
                "version" => await VersionAsync(rest),
                "help" or "--help" or "-h" => Help(0),
                _ => Unknown(verb),
            };
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine($"cider: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cider: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--socket", "--data-dir", "--log-level"], ["--no-dns"]);
        var options = CiderOptions.Load(parsed.Value("--data-dir"), parsed.Value("--socket"), parsed.Value("--log-level"));
        WarnAboutLegacyDataDir(options);

        var app = DaemonHost.Create(options, new DaemonHostSettings
        {
            DnsEnabled = !parsed.Flag("--no-dns"),
        });

        await app.RunAsync();
        return 0;
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--socket", "--data-dir", "--log-level"], ["--system-socket", "--force-system-socket", "--no-context"]);
        var options = CiderOptions.Load(parsed.Value("--data-dir"), parsed.Value("--socket"), parsed.Value("--log-level"));

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine the path of the running executable");

        var installOptions = new InstallOptions(
            executable,
            options.SocketPath,
            options.DataDir,
            options.LogLevel,
            CreateDockerContext: !parsed.Flag("--no-context"),
            SystemSocketSymlink: parsed.Flag("--system-socket") || parsed.Flag("--force-system-socket"),
            SystemSocketForce: parsed.Flag("--force-system-socket"));

        var result = await LaunchdInstaller.InstallAsync(installOptions, Console.Out, CancellationToken.None);
        Console.WriteLine(result.Message);

        if (result.Success)
        {
            Console.WriteLine();
            Console.WriteLine("Point Docker tooling at cider with either of:");
            Console.WriteLine("  docker context use cider");
            Console.WriteLine($"  export DOCKER_HOST=unix://{options.SocketPath}");
            if (!installOptions.SystemSocketSymlink)
            {
                Console.WriteLine();
                Console.WriteLine(SystemSocketLink.Instructions(options.SocketPath));
            }
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> UninstallAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--label", "--data-dir"], []);
        var label = parsed.Value("--label") ?? "com.chillicream.cider.daemon";
        // The data dir holds system-socket.backup.json, i.e. what /var/run/docker.sock pointed at
        // before `install --system-socket` repointed it.
        var options = CiderOptions.Load(parsed.Value("--data-dir"), null, null);
        var result = await LaunchdInstaller.UninstallAsync(label, Console.Out, CancellationToken.None, options.DataDir);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--socket", "--data-dir"], []);
        var options = CiderOptions.Load(parsed.Value("--data-dir"), parsed.Value("--socket"), null);

        WarnAboutLegacyDataDir(options);
        Console.WriteLine($"socket:    {options.SocketPath}");
        Console.WriteLine($"data dir:  {options.DataDir}");

        var responding = await PingAsync(options.SocketPath);
        Console.WriteLine($"daemon:    {(responding ? "responding" : "not responding")}");

        var service = await LaunchdInstaller.StatusAsync("com.chillicream.cider.daemon", CancellationToken.None);
        Console.WriteLine($"launchd:   {(service.Installed ? "installed" : "not installed")}" +
                          (service.Running ? $", running (pid {service.Pid})" : ", not running") +
                          (service.LastExitStatus is { Length: > 0 } exit ? $", last exit {exit}" : ""));

        var runtime = new AppleContainerRuntime(
            new AppleContainerOptions { CliPath = options.ContainerCliPath, TmpDir = options.TmpDir },
            NullLogger<AppleContainerRuntime>.Instance);

        try
        {
            var info = await runtime.GetInfoAsync(CancellationToken.None);
            Console.WriteLine($"apple:     {info.Name} {info.Version}, {(info.Ready ? "running" : "stopped")}" +
                              (info.KernelVersion is { Length: > 0 } kernel ? $", kernel {kernel}" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"apple:     unavailable ({ex.Message})");
        }

        return responding ? 0 : 1;
    }

    /// <summary>
    /// Resynchronises the running daemon's persisted state against Apple <c>container</c> on demand
    /// (a container/network/volume deleted with the Apple CLI, Apple services restarted, a
    /// hard-killed daemon). Never touches <c>&lt;data-dir&gt;/state</c> directly — it only ever POSTs
    /// to the running daemon, which owns the resync itself (<c>StateSynchronizer</c>).
    /// </summary>
    private static async Task<int> SyncAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--socket", "--data-dir"], ["--json"]);
        var options = CiderOptions.Load(parsed.Value("--data-dir"), parsed.Value("--socket"), null);

        if (!File.Exists(options.SocketPath))
        {
            return DaemonNotResponding(options.SocketPath);
        }

        using var client = DaemonClient.Create(options.SocketPath, TimeSpan.FromMinutes(2));

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(new Uri("/_cider/sync", UriKind.Relative), content: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            return DaemonNotResponding(options.SocketPath);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"cider: sync failed: {body}");
                return 1;
            }

            var report = JsonSerializer.Deserialize(body, CiderJsonContext.Default.SyncReportDto)
                ?? throw new InvalidOperationException("cider: the daemon returned an empty sync report");

            if (parsed.Flag("--json"))
            {
                Console.WriteLine(body);
            }
            else
            {
                PrintSyncSummary(report);
            }

            return report.Warnings.Count == 0 ? 0 : 1;
        }
    }

    private static int DaemonNotResponding(string socketPath)
    {
        Console.Error.WriteLine(
            $"cider: daemon not responding on {socketPath} — start it "
            + "(launchctl kickstart -k gui/$UID/com.chillicream.cider.daemon) and retry");
        return 1;
    }

    private static void PrintSyncSummary(SyncReportDto report)
    {
        Console.WriteLine($"containers: {DescribeResource(report.Containers, includeUpdated: true)}");
        Console.WriteLine($"networks:   {DescribeResource(report.Networks, includeUpdated: false)}");
        Console.WriteLine($"volumes:    {DescribeResource(report.Volumes, includeUpdated: false)}");

        if (report.Warnings.Count > 0)
        {
            Console.WriteLine($"warnings:   {report.Warnings.Count}");
            foreach (var warning in report.Warnings)
            {
                Console.WriteLine($"  - {warning}");
            }
        }

        if (report.IsEmpty)
        {
            Console.WriteLine("nothing to do — cider's state already matches Apple container.");
        }
    }

    private static string DescribeResource(SyncResourceReportDto resource, bool includeUpdated)
    {
        var parts = new List<string>
        {
            $"{resource.Removed.Count} removed{NameList(resource.Removed)}",
            $"{resource.Adopted.Count} adopted{NameList(resource.Adopted)}",
        };

        if (includeUpdated)
        {
            parts.Add($"{resource.Updated.Count} updated{NameList(resource.Updated)}");
        }

        return string.Join(", ", parts);
    }

    private static string NameList(IReadOnlyCollection<string> names) =>
        names.Count > 0 ? $" ({string.Join(", ", names)})" : "";

    private static async Task<int> VersionAsync(string[] args)
    {
        var parsed = CommandLine.Parse(args, ["--data-dir"], []);
        var options = CiderOptions.Load(parsed.Value("--data-dir"), null, null);

        Console.WriteLine($"cider {InformationalVersion()}");
        Console.WriteLine($"Docker API {options.ApiVersion} (min {options.MinApiVersion}), reported engine {options.EngineVersion}");

        var runtime = new AppleContainerRuntime(
            new AppleContainerOptions { CliPath = options.ContainerCliPath, TmpDir = options.TmpDir },
            NullLogger<AppleContainerRuntime>.Instance);

        try
        {
            var info = await runtime.GetInfoAsync(CancellationToken.None);
            Console.WriteLine($"Apple container {info.Version}" + (info.Ready ? " (running)" : " (not running)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Apple container unavailable: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Transitional (rename to Cider): prints the one <c>mv</c> command that moves a pre-rename
    /// <c>~/.apple-demon</c> onto the new default data directory. The daemon never moves it itself.
    /// </summary>
    private static void WarnAboutLegacyDataDir(CiderOptions options)
    {
        if (options.MigrationHint is { Length: > 0 } hint)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(hint);
            Console.Error.WriteLine();
        }
    }

    private static async Task<bool> PingAsync(string socketPath)
    {
        if (!File.Exists(socketPath))
        {
            return false;
        }

        try
        {
            using var client = DaemonClient.Create(socketPath, TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(new Uri("/_ping", UriKind.Relative));
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static string InformationalVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"cider: unknown command '{verb}'");
        return Help(2);
    }

    private static int Help(int exitCode)
    {
        var writer = exitCode == 0 ? Console.Out : Console.Error;
        writer.WriteLine("""
            cider — a Docker Engine API daemon backed by Apple container

            Usage:
              cider serve [--socket PATH] [--data-dir DIR] [--log-level LEVEL] [--no-dns]
              cider install [--system-socket] [--force-system-socket] [--no-context] [--socket PATH] [--data-dir DIR]
              cider uninstall [--label LABEL] [--data-dir DIR]
              cider status [--socket PATH]
              cider sync [--socket PATH] [--data-dir DIR] [--json]
              cider version
              cider help

            --system-socket saves whatever /var/run/docker.sock pointed at into
            <data-dir>/system-socket.backup.json and `uninstall` puts that target back — pass the same
            --data-dir you installed with, or uninstall cannot find that record. It refuses to
            replace a real socket file (which could not be restored) unless --force-system-socket.

            Environment:
              CIDER_SOCKET, CIDER_DATA_DIR, CIDER_LOG_LEVEL, CIDER_CONTAINER_CLI
            """);
        return exitCode;
    }
}

/// <summary>A bad command line.</summary>
public sealed class OptionException(string message) : Exception(message);

/// <summary>A hand-rolled parser for the daemon's handful of <c>--option value</c> / <c>--flag</c> arguments.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    /// <summary>Parses <paramref name="args"/>, accepting only the named options and flags.</summary>
    public static CommandLine Parse(string[] args, IReadOnlyList<string> options, IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flags);

        var parsed = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? arg : arg[..separator];
            var inline = separator < 0 ? null : arg[(separator + 1)..];

            if (flags.Contains(name))
            {
                parsed._flags.Add(name);
                continue;
            }

            if (!options.Contains(name))
            {
                throw new OptionException($"unknown option '{name}'");
            }

            if (inline is not null)
            {
                parsed._values[name] = inline;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new OptionException($"option '{name}' needs a value");
            }

            parsed._values[name] = args[++i];
        }

        return parsed;
    }

    /// <summary>The value of an option, or <c>null</c> when it was not given.</summary>
    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>Whether a flag was given.</summary>
    public bool Flag(string name) => _flags.Contains(name);
}
