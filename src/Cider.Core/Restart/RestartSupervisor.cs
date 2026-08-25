using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.Events;
using Cider.Core.Runtime;
using Cider.Core.Services;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Restart;

/// <summary>
/// Docker's restart policies (<c>always</c>, <c>unless-stopped</c>, <c>on-failure[:max]</c>).
/// It listens to <see cref="ContainerManager.StateChanged"/> and restarts containers with the
/// exponential backoff Docker uses (100 ms doubling, capped at a minute).
/// </summary>
public sealed class RestartSupervisor : IAsyncDisposable
{
    private readonly ContainerManager _containers;
    private readonly EventBus _events;
    private readonly ILogger<RestartSupervisor> _logger;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _pending = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>
    /// The error text <see cref="MarkVanished"/> puts on <c>record.State.Error</c>, and the marker
    /// <see cref="ContainerManager.HandleExitAsync"/> (ContainerManager.Lifecycle.cs) also stamps
    /// when the started process itself reports the container gone rather than the start call
    /// throwing (a warm tty cache can let <c>container start -a</c> spawn against a runtime id
    /// Apple has already dropped — cider-msj). <see cref="OnStateChanged"/> treats either source of
    /// this marker as terminal.
    /// </summary>
    public const string VanishedError = "container no longer exists in Apple container (removed outside cider)";

    /// <summary>Creates the supervisor.</summary>
    public RestartSupervisor(ContainerManager containers, EventBus events, ILogger<RestartSupervisor> logger)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Delay before the first restart attempt; doubles with every consecutive failure.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Upper bound for the backoff delay.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a container has to stay up before the next exit is treated as a fresh failure
    /// streak instead of a continuation of the current one (Docker resets after 10 s).
    /// </summary>
    public TimeSpan StableRunThreshold { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Subscribes to container state changes.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_running)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _containers.StateChanged += OnStateChanged;
        _running = true;
        return Task.CompletedTask;
    }

    /// <summary>Unsubscribes and waits for in-flight restarts.</summary>
    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _containers.StateChanged -= OnStateChanged;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        var pending = _pending.Values.ToArray();
        _pending.Clear();
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch (Exception ex) when (ex is OperationCanceledException or DockerApiException or RuntimeException)
            {
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Whether the policy would restart this container after the exit it just had.</summary>
    public static bool ShouldRestart(ContainerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.UserStopped || record.AutoRemove)
        {
            return false;
        }

        var policy = record.RestartPolicy;
        return policy.Name switch
        {
            "always" => true,
            "unless-stopped" => true,
            "on-failure" => record.State.ExitCode != 0 &&
                            (policy.MaximumRetryCount <= 0 || record.RestartCount < policy.MaximumRetryCount),
            _ => false,
        };
    }

    private void OnStateChanged(ContainerRecord record, string action)
    {
        if (!string.Equals(action, "die", StringComparison.Ordinal) || !_running)
        {
            return;
        }

        // ContainerManager.HandleExitAsync already recognized (from the started process's own
        // stderr) that the container itself is gone, even though the start call never threw —
        // the same "give up for good" situation the NotFound catch in RestartAsync handles below,
        // just discovered on the other side of a successful start. Status/Error/the "die" event
        // are already set by the time this runs; only the scheduling decision is ours to make.
        if (string.Equals(record.State.Error, VanishedError, StringComparison.Ordinal))
        {
            _attempts.TryRemove(record.Id, out _);
            LogGivingUp(record);
            return;
        }

        if (!ShouldRestart(record))
        {
            _attempts.TryRemove(record.Id, out _);
            return;
        }

        // Docker only resets the backoff once the container has stayed up for at least 10 s
        // (moby's restartmanager); anything shorter keeps doubling so a container whose start
        // "succeeds" but whose process exits at once does not spin at the 100 ms floor forever.
        if (record.State.StartedAt is { } startedAt && record.State.FinishedAt is { } finishedAt &&
            finishedAt - startedAt >= StableRunThreshold)
        {
            _attempts.TryRemove(record.Id, out _);
        }

        var attempt = _attempts.AddOrUpdate(record.Id, 1, (_, current) => current + 1);
        var delay = BackoffFor(attempt);
        var token = _cts?.Token ?? CancellationToken.None;

        if (attempt == 5)
        {
            _logger.LogWarning(
                "container {Container} keeps exiting; restart attempt {Attempt}, next in {Delay}",
                record.Name, attempt, delay);
        }
        else
        {
            _logger.LogDebug(
                "container {Container} exited; restart attempt {Attempt}, next in {Delay}",
                record.Name, attempt, delay);
        }

        var task = Task.Run(() => RestartAsync(record, delay, token), CancellationToken.None);
        _pending[record.Id] = task;
    }

    private TimeSpan BackoffFor(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 20));
        var delay = InitialBackoff * multiplier;
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private async Task RestartAsync(ContainerRecord record, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            _containers.MarkRestarting(record);
            _events.Publish(DockerEvents.Container("restart", record));
            await Task.Delay(delay, ct);

            // Deliberately not clearing _attempts[record.Id] here: StartAsync returning just means
            // the child process was spawned, not that it stayed up. Docker only resets the backoff
            // once the container has actually run for a while (see OnStateChanged) — resetting on
            // every successful spawn is the bug this supervisor exists to fix (a container whose
            // start "succeeds" but whose process exits at once would otherwise restart at the 100 ms
            // floor forever instead of backing off).
            await _containers.StartAsync(record.Id, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // The runtime container is gone for good (removed outside cider, or Apple's services
            // restarted and lost it) — StartAsync will 404 on every future attempt too, so retrying
            // is exactly the tight loop this supervisor exists to prevent. Stop for good instead.
            MarkVanished(record);
        }
        catch (Exception ex) when (ex is DockerApiException or RuntimeException)
        {
            _logger.LogWarning(ex, "restart policy could not restart container {Container}", record.Id);
            MarkFailed(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "restart policy could not restart container {Container}", record.Id);
            MarkFailed(record);
        }
        finally
        {
            _pending.TryRemove(record.Id, out _);
        }
    }

    private void MarkFailed(ContainerRecord record)
    {
        if (string.Equals(record.State.Status, "restarting", StringComparison.Ordinal))
        {
            record.State.Status = "exited";
            _containers.PersistExternal(record);
        }
    }

    /// <summary>
    /// Gives up on a container the runtime no longer knows about: stops supervising it, marks it
    /// exited with an explanatory error, and publishes one <c>die</c> so listeners see it settle
    /// (not <see cref="ContainerManager.RaiseStateChangedExternal"/> — that would hand this same
    /// exit straight back to <see cref="OnStateChanged"/> and reschedule the very retry this is
    /// meant to stop). The state poller (see cider-4y2) then drops the record.
    /// </summary>
    private void MarkVanished(ContainerRecord record)
    {
        _attempts.TryRemove(record.Id, out _);

        record.State.Status = "exited";
        record.State.Error = VanishedError;
        record.State.FinishedAt ??= DateTimeOffset.UtcNow;
        _containers.PersistExternal(record);
        _events.Publish(DockerEvents.Container("die", record, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exitCode"] = record.State.ExitCode.ToString(CultureInfo.InvariantCulture),
        }));

        LogGivingUp(record);
    }

    private void LogGivingUp(ContainerRecord record) =>
        _logger.LogWarning(
            "container {Container} no longer exists in Apple container (removed outside cider); giving up restarting it",
            record.Name);
}
