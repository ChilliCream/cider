using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

// A minimal .NET Aspire AppHost pointed at cider through DOCKER_HOST.
//
// Two container resources (redis + postgres) cover pull, create, start, published ports and
// readiness/health gating; one project resource ("op3app") runs on the host and actually connects
// to both over those published ports, so a green run means real host -> container traffic, not just
// "DCP said Running". The consumer reports through a file the AppHost names, because DCP owns the
// child process's console.
//
// The AppHost exits by itself once the consumer reaches a terminal state, so the E2E test can shell
// out to `dotnet run` and simply wait for the process.

var sentinel = Environment.GetEnvironmentVariable("ASPIRE_E2E_SENTINEL")
    ?? Path.Combine(Path.GetTempPath(), "cider-aspire-e2e.sentinel");

if (File.Exists(sentinel))
{
    File.Delete(sentinel);
}

Console.WriteLine("APPHOST_DOCKER_HOST=" + (Environment.GetEnvironmentVariable("DOCKER_HOST") ?? "(unset)"));
Console.WriteLine("APPHOST_SENTINEL=" + sentinel);

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DisableDashboard = true,
    AllowUnsecuredTransport = true,
    EnableResourceLogging = true,
});

var cache = builder.AddRedis("op3cache");
var postgres = builder.AddPostgres("op3pg");

builder.AddProject<Projects.E2EAspireConsumer>("op3app")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithEnvironment("ASPIRE_E2E_SENTINEL", sentinel);

var app = builder.Build();

// DCP retries a failing resource forever, so the whole run needs a wall clock. The E2E test shortens
// it (ASPIRE_E2E_TIMEOUT_SECONDS) while Aspire is still blocked on a daemon gap, so a run that can
// only fail fails in minutes instead of a quarter of an hour.
var budget = TimeSpan.FromSeconds(
    int.TryParse(
        Environment.GetEnvironmentVariable("ASPIRE_E2E_TIMEOUT_SECONDS"),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var seconds) && seconds > 0
        ? seconds
        : 720);
Console.WriteLine("APPHOST_TIMEOUT_SECONDS=" + budget.TotalSeconds.ToString(CultureInfo.InvariantCulture));

using var cancellation = new CancellationTokenSource(budget);
var exitCode = 1;

try
{
    await app.StartAsync(cancellation.Token);
    Console.WriteLine("APPHOST_STARTED");

    var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
    var terminalState = await notifications.WaitForResourceAsync("op3app", KnownResourceStates.TerminalStates, cancellation.Token);
    Console.WriteLine("APPHOST_CONSUMER_STATE=" + terminalState);

    foreach (var line in ReadSentinel(sentinel))
    {
        Console.WriteLine("CONSUMER_SENTINEL: " + line);
    }

    if (ReadSentinel(sentinel).Contains("ASPIRE_OK", StringComparer.Ordinal))
    {
        Console.WriteLine("ASPIRE_RUN_OK");
        exitCode = 0;
    }
    else
    {
        Console.WriteLine("ASPIRE_RUN_FAILED");
    }
}
catch (Exception ex)
{
    Console.WriteLine("ASPIRE_RUN_FAILED");
    Console.WriteLine(ex.ToString());
}
finally
{
    try
    {
        await app.StopAsync(new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine("APPHOST_STOP_FAILED: " + ex);
    }

    await app.DisposeAsync();
    Console.WriteLine("APPHOST_DISPOSED: reaped " + ReapOrphanedOrchestrators() + " orphaned dcp process(es)");
}

return exitCode;

// DCP's api server is started detached (`--detach`) and outlives the AppHost on purpose: it watches
// the pid it was given with `--monitor`, and only once that process is gone does it delete the
// containers, networks and volumes of the session and exit. Killing it here — which an earlier
// version of this fixture did — is what left those objects behind, so this only reaps *orphans*:
// dcp processes whose monitored process no longer exists, i.e. leftovers of a run that was killed
// before its own api server could finish. A live Aspire app elsewhere on this machine is never
// touched, because the process it monitors is still there.
static int ReapOrphanedOrchestrators()
{
    var reaped = 0;
    foreach (var (pid, monitored) in Orchestrators())
    {
        if (monitored is { } watched && !IsAlive(watched))
        {
            reaped += Kill(pid);
        }
    }

    return reaped;
}

static bool IsAlive(int pid)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return false;
    }
}

// Every `aspire.hosting.orchestration` process on this machine with the pid it monitors.
static List<(int Pid, int? Monitored)> Orchestrators()
{
    var found = new List<(int, int?)>();
    try
    {
        using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/ps")
        {
            ArgumentList = { "-ax", "-o", "pid=,command=" },
            RedirectStandardOutput = true,
        });
        if (ps is null)
        {
            return found;
        }

        var listing = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(10_000);
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0
                || !int.TryParse(trimmed[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                || !trimmed.Contains("aspire.hosting.orchestration", StringComparison.Ordinal))
            {
                continue;
            }

            found.Add((pid, MonitoredPid(trimmed)));
        }
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
    {
    }

    return found;
}

// The argument after `--monitor` has to be read as a whole: a substring test on "--monitor 1234"
// also matches "--monitor 12345", i.e. an unrelated Aspire app on this machine.
static int? MonitoredPid(string command)
{
    var arguments = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    for (var i = 0; i < arguments.Length; i++)
    {
        var value = string.Equals(arguments[i], "--monitor", StringComparison.Ordinal) && i + 1 < arguments.Length
            ? arguments[i + 1]
            : arguments[i].StartsWith("--monitor=", StringComparison.Ordinal)
                ? arguments[i]["--monitor=".Length..]
                : null;

        if (value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return pid;
        }
    }

    return null;
}

static int Kill(int pid)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        return 1;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or SystemException)
    {
        return 0;
    }
}

static string[] ReadSentinel(string path)
{
    try
    {
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }
    catch (IOException)
    {
        return [];
    }
}
