using System.Collections.Concurrent;
using System.Text;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.Events;
using Cider.Core.Services;
using Cider.Core.State;
using Cider.Core.Time;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Health;

/// <summary>
/// Runs the <c>HEALTHCHECK</c> of every running container as an exec probe and keeps
/// <c>State.Health</c> plus the <c>health_status: …</c> events in sync with the result.
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable
{
    /// <summary>Docker's defaults for a healthcheck that leaves them at zero.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private const int MaxLogEntries = 5;
    private const int MaxOutputBytes = 4096;

    private readonly ContainerManager _containers;
    private readonly ExecManager _execs;
    private readonly EventBus _events;
    private readonly IRecordStore<ContainerRecord> _store;
    private readonly ILogger<HealthMonitor> _logger;
    private readonly ConcurrentDictionary<string, ProbeState> _probes = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Creates the monitor.</summary>
    public HealthMonitor(
        ContainerManager containers,
        ExecManager execs,
        EventBus events,
        IRecordStore<ContainerRecord> store,
        ILogger<HealthMonitor> logger)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _execs = execs ?? throw new ArgumentNullException(nameof(execs));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>How often the monitor looks for probes that are due.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Starts the background loop.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the background loop and waits for it.</summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _loop = null;
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Runs one due-probe pass; exposed so tests do not have to wait for a tick.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var record in _store.GetAll())
        {
            if (!string.Equals(record.State.Status, "running", StringComparison.Ordinal))
            {
                _probes.TryRemove(record.Id, out _);
                continue;
            }

            var healthcheck = record.Healthcheck;
            if (healthcheck is null || healthcheck.Test.Count == 0 ||
                string.Equals(healthcheck.Test[0], "NONE", StringComparison.Ordinal))
            {
                continue;
            }

            var probe = _probes.GetOrAdd(record.Id, _ => new ProbeState(now));
            if (probe.InFlight || now < probe.NextRun)
            {
                continue;
            }

            probe.InFlight = true;
            probe.NextRun = now + IntervalOf(healthcheck, probe, now);

            await RunProbeAsync(record, healthcheck, probe, ct);
        }
    }

    private static TimeSpan IntervalOf(HealthConfig healthcheck, ProbeState probe, DateTimeOffset now)
    {
        var startPeriod = FromNanos(healthcheck.StartPeriod);
        if (startPeriod > TimeSpan.Zero && now < probe.StartedAt + startPeriod && healthcheck.StartInterval > 0)
        {
            return FromNanos(healthcheck.StartInterval);
        }

        return healthcheck.Interval > 0 ? FromNanos(healthcheck.Interval) : DefaultInterval;
    }

    private static TimeSpan FromNanos(long nanos) => TimeSpan.FromTicks(Math.Max(nanos, 0) / 100L);

    private async Task RunProbeAsync(ContainerRecord record, HealthConfig healthcheck, ProbeState probe, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var argv = BuildProbeCommand(healthcheck, record);
        var exitCode = -1;
        var output = "";

        try
        {
            var timeout = healthcheck.Timeout > 0 ? FromNanos(healthcheck.Timeout) : TimeSpan.FromSeconds(30);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(timeout);

            var created = await _execs.CreateAsync(record.Id, new ExecCreateRequest
            {
                Cmd = argv,
                AttachStdout = true,
                AttachStderr = true,
            }, probeCts.Token);

            await using var session = await _execs.StartAsync(created.Id, tty: false, consoleSize: null, probeCts.Token);

            var buffer = new StringBuilder();
            await foreach (var chunk in session.Output.ReadAllAsync(probeCts.Token))
            {
                if (buffer.Length < MaxOutputBytes)
                {
                    buffer.Append(Encoding.UTF8.GetString(chunk.Data.Span));
                }
            }

            exitCode = await session.Exited.WaitAsync(probeCts.Token);
            output = buffer.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            output = "Health check exceeded timeout";
        }
        catch (OperationCanceledException)
        {
            probe.InFlight = false;
            return;
        }
        catch (DockerApiException ex)
        {
            output = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "health probe of container {Container} failed", record.Id);
            output = ex.Message;
        }
        finally
        {
            probe.InFlight = false;
        }

        Apply(record, healthcheck, probe, start, exitCode, output);
    }

    private void Apply(
        ContainerRecord record,
        HealthConfig healthcheck,
        ProbeState probe,
        DateTimeOffset start,
        int exitCode,
        string output)
    {
        var health = record.State.Health ??= new HealthState { Status = "starting" };
        var previous = health.Status;

        health.Log.Add(new HealthcheckResult
        {
            Start = DockerTime.Format(start),
            End = DockerTime.Format(DateTimeOffset.UtcNow),
            ExitCode = exitCode,
            Output = output,
        });

        while (health.Log.Count > MaxLogEntries)
        {
            health.Log.RemoveAt(0);
        }

        if (exitCode == 0)
        {
            health.FailingStreak = 0;
            health.Status = "healthy";
        }
        else
        {
            var startPeriod = FromNanos(healthcheck.StartPeriod);
            var inStartPeriod = startPeriod > TimeSpan.Zero && DateTimeOffset.UtcNow < probe.StartedAt + startPeriod;
            if (!inStartPeriod)
            {
                health.FailingStreak++;
                var retries = healthcheck.Retries > 0 ? healthcheck.Retries : 3;
                if (health.FailingStreak >= retries)
                {
                    health.Status = "unhealthy";
                }
            }
        }

        _containers.PersistExternal(record);

        if (!string.Equals(previous, health.Status, StringComparison.Ordinal) &&
            health.Status is "healthy" or "unhealthy")
        {
            _events.Publish(DockerEvents.Container($"health_status: {health.Status}", record));
            _containers.RaiseStateChangedExternal(record, $"health_status: {health.Status}");
        }
    }

    private static List<string> BuildProbeCommand(HealthConfig healthcheck, ContainerRecord record)
    {
        var test = healthcheck.Test;
        var kind = test[0];

        if (string.Equals(kind, "CMD", StringComparison.Ordinal))
        {
            return [.. test.Skip(1)];
        }

        if (string.Equals(kind, "CMD-SHELL", StringComparison.Ordinal))
        {
            var shell = record.Request.Shell is { Count: > 0 } configured ? configured : ["/bin/sh", "-c"];
            var argv = new List<string>(shell);
            argv.Add(string.Join(' ', test.Skip(1)));
            return argv;
        }

        // A bare list (no CMD/CMD-SHELL prefix) is Docker's legacy shell form.
        return ["/bin/sh", "-c", string.Join(' ', test)];
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, ct);
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "health monitor tick failed");
            }
        }
    }

    private sealed class ProbeState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset NextRun { get; set; } = startedAt;

        public bool InFlight { get; set; }
    }
}
