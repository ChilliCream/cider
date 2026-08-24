using System.Collections.Concurrent;
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

        if (!ShouldRestart(record))
        {
            _attempts.TryRemove(record.Id, out _);
            return;
        }

        var attempt = _attempts.AddOrUpdate(record.Id, 1, (_, current) => current + 1);
        var delay = BackoffFor(attempt);
        var token = _cts?.Token ?? CancellationToken.None;

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
            await _containers.StartAsync(record.Id, ct);
            _attempts.TryRemove(record.Id, out _);
        }
        catch (OperationCanceledException)
        {
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
}
