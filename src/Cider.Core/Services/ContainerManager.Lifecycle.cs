using System.Globalization;
using System.Net;
using Cider.Core.DockerApi;
using Cider.Core.DockerApi.Models;
using Cider.Core.DockerApi.Streams;
using Cider.Core.Ids;
using Cider.Core.Runtime;
using Cider.Core.State;
using Microsoft.Extensions.Logging;

namespace Cider.Core.Services;

public sealed partial class ContainerManager
{
    private const int PumpBufferSize = 32 * 1024;

    /// <summary>How long the exit handler waits for the stdio pumps to drain before giving up.</summary>
    public TimeSpan PumpDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often <see cref="AwaitStartupAndRegisterNetworkNamesAsync"/> re-inspects after start.</summary>
    public TimeSpan NetworkPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Hard cap on how long the post-start network poll keeps retrying at all (including the
    /// part that runs after <see cref="StartAsync"/> has already returned to its caller).</summary>
    public TimeSpan NetworkPollBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="StartAsync"/> itself will wait for an address once the runtime
    /// confirms the container is running, before returning and leaving the rest to <see cref="StatePoller"/>.</summary>
    public TimeSpan StartReturnBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary><c>POST /containers/{id}/start</c>; 304 when the container already runs.</summary>
    public async Task StartAsync(string idOrName, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (string.Equals(record.State.Status, "running", StringComparison.Ordinal) && handle.Process is not null)
            {
                throw DockerErrors.NotModified();
            }

            // Files `docker cp`'d in before the first start are mounted into the container here, so
            // they are in place the moment its entrypoint runs — Apple `container cp` cannot write
            // into a container that is not running, and copying them in afterwards is too late for
            // an image that reads them at once (see TryMountStagedArchivesAsync).
            var mountedBatches = await TryMountStagedArchivesAsync(record, ct);

            IContainerProcess process;
            try
            {
                process = await _runtime.StartContainerAsync(
                    record.RuntimeId,
                    new StartOptions { AttachStdin = record.Request.OpenStdin },
                    ct);
            }
            catch (RuntimeException ex)
            {
                record.State.Error = ex.Message;
                Persist(record);
                throw Translate(ex);
            }

            // Only now that the start has succeeded are the mounted batches marked: the marker turns
            // off the copy fallback for them for good, and a failed start would have left them
            // mounted on an engine container that the next start re-creates from the record alone.
            await MarkStagedBatchesMounted(mountedBatches, record.RuntimeId);

            handle.Process = process;
            handle.Tty = process.HasTty;
            handle.LogWriter = _logs.OpenWriter(record.Id);
            handle.Pumps.Clear();
            handle.Pumps.Add(PumpAsync(handle, process.Stdout, StdStream.Stdout));
            if (process.Stderr is { } stderr)
            {
                handle.Pumps.Add(PumpAsync(handle, stderr, StdStream.Stderr));
            }

            record.State.Status = "running";
            record.State.StartedAt = DateTimeOffset.UtcNow;
            record.State.FinishedAt = null;
            record.State.ExitCode = 0;
            record.State.Error = null;
            record.State.Pid = process.Pid ?? 0;
            record.UserStopped = false;
            if (record.Healthcheck is { Test.Count: > 0 } && !IsHealthcheckDisabled(record.Healthcheck))
            {
                record.State.Health = new HealthState { Status = "starting" };
            }

            Persist(record);

            await BindAttachmentsAsync(handle);
            handle.ExitHandling = Task.Run(() => HandleExitAsync(record.Id, handle, process), CancellationToken.None);

            await AwaitStartupAndRegisterNetworkNamesAsync(record, process, ct);

            // Everything `docker cp`'d into the container while it was not running goes in here,
            // with the gate still held and before this call returns, so the client that started it
            // cannot observe it running without the files it handed over before the start.
            // Aspire/DCP injects its development certificates exactly that way, and Apple
            // `container cp` refuses a container that is not running.
            await FlushStagedArchivesAsync(record, ct);

            await EnsurePublishedPortsAsync(record, ct);

            Publish(record, "start");
            RaiseStateChanged(record, "start");
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    /// <summary><c>POST /containers/{id}/stop</c>; 304 when the container is not running.</summary>
    public async Task StopAsync(string idOrName, int? timeoutSeconds, string? signal, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (!record.State.Running)
            {
                throw DockerErrors.NotModified();
            }

            record.UserStopped = true;
            Persist(record);

            try
            {
                await _runtime.StopContainerAsync(record.RuntimeId, timeoutSeconds ?? record.StopTimeout, signal ?? record.StopSignal, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }
        }
        finally
        {
            handle.Gate.Release();
        }

        await WaitForExitHandlingAsync(handle, timeoutSeconds);
        MarkStoppedWithoutHandle(handle, record);
        UnpublishPorts(record.Id);
        Publish(record, "stop");
    }

    /// <summary>
    /// Marks a container the daemon does not hold the stdio of (reconciled after a daemon restart,
    /// or created outside the daemon) as exited. Without this the record stays <c>running</c> until
    /// the state poller catches up seconds later, so <c>docker stop</c> would return while
    /// <c>docker inspect</c> still says the container is running.
    /// </summary>
    private void MarkStoppedWithoutHandle(ContainerHandle handle, ContainerRecord record)
    {
        if (handle.ExitHandling is not null || !record.State.Running)
        {
            return;
        }

        record.State.Status = "exited";
        record.State.FinishedAt ??= DateTimeOffset.UtcNow;
        record.State.ExitCode = 0;
        record.State.Error = "exit code unknown (daemon restarted)";
        Persist(record);
    }

    /// <summary><c>POST /containers/{id}/kill</c>; 409 when the container is not running.</summary>
    public async Task KillAsync(string idOrName, string? signal, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);
        var effective = string.IsNullOrEmpty(signal) ? "SIGKILL" : NormalizeSignal(signal);

        await handle.Gate.WaitAsync(ct);
        try
        {
            if (!record.State.Running)
            {
                throw DockerErrors.Conflict($"Cannot kill container: {idOrName}: Container {record.Id} is not running");
            }

            if (IsTerminating(effective))
            {
                record.UserStopped = true;
                Persist(record);
            }

            try
            {
                await _runtime.KillContainerAsync(record.RuntimeId, effective, ct);
            }
            catch (RuntimeException ex)
            {
                throw Translate(ex);
            }
        }
        finally
        {
            handle.Gate.Release();
        }

        Publish(record, "kill", new Dictionary<string, string>(StringComparer.Ordinal) { ["signal"] = effective });
    }

    /// <summary><c>POST /containers/{id}/restart</c>.</summary>
    public async Task RestartAsync(string idOrName, int? timeoutSeconds, CancellationToken ct)
    {
        var record = Resolve(idOrName);

        try
        {
            await StopAsync(record.Id, timeoutSeconds, signal: null, ct);
        }
        catch (DockerApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotModified)
        {
            // Already stopped: a restart just starts it.
        }

        await StartAsync(record.Id, ct);
        Publish(record, "restart");
    }

    /// <summary><c>DELETE /containers/{id}</c>; 409 for a running container without <c>force</c>.</summary>
    public async Task RemoveAsync(string idOrName, bool force, bool removeVolumes, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        var wasRunning = record.State.Running;
        if (wasRunning && !force)
        {
            throw DockerErrors.ContainerRunning(record.Name);
        }

        if (wasRunning)
        {
            record.UserStopped = true;
            Persist(record);
            try
            {
                await _runtime.KillContainerAsync(record.RuntimeId, "SIGKILL", ct);
            }
            catch (RuntimeException ex)
            {
                _logger.LogDebug(ex, "killing container {Container} before removal failed", record.Id);
            }

            await WaitForExitHandlingAsync(handle, timeoutSeconds: 10);
        }

        await handle.Gate.WaitAsync(ct);
        try
        {
            record.State.Status = "removing";
            Persist(record);

            try
            {
                await _runtime.RemoveContainerAsync(record.RuntimeId, force, ct);
            }
            catch (RuntimeException ex) when (ex.Kind == RuntimeErrorKind.NotFound)
            {
                // Already gone on the engine side; the record still has to go.
            }
            catch (RuntimeException ex)
            {
                record.State.Status = wasRunning ? "running" : "exited";
                Persist(record);
                throw Translate(ex);
            }

            if (handle.Process is { } process)
            {
                await process.DisposeAsync();
                handle.Process = null;
            }

            _names.Unregister(record.Id);
            UnpublishPorts(record.Id);
            ReleasePorts(record);
            _logs.Delete(record.Id);
            DropStagedArchives(record.Id);
            _store.Delete(record.Id);
            _handles.TryRemove(record.Id, out _);
            handle.Removed.TrySetResult(record.State.ExitCode);
            CompleteAttachments(handle);
        }
        finally
        {
            handle.Gate.Release();
        }

        if (removeVolumes)
        {
            await RemoveAnonymousVolumesAsync(record, ct);
        }

        Publish(record, "destroy");
        RaiseStateChanged(record, "destroy");
    }

    /// <summary><c>POST /containers/{id}/wait?condition=</c>.</summary>
    public async Task<ContainerWaitResponse> WaitAsync(string idOrName, string condition, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);
        var wanted = string.IsNullOrEmpty(condition) ? "not-running" : condition;

        switch (wanted)
        {
            case "removed":
            {
                var code = await handle.Removed.Task.WaitAsync(ct);
                return new ContainerWaitResponse { StatusCode = code };
            }

            case "next-exit":
            {
                // Capture the pending exit before awaiting: `docker run` waits before it starts.
                var pending = handle.NextExit.Task;
                var code = await pending.WaitAsync(ct);
                return Response(code);
            }

            default:
            {
                if (!record.State.Running)
                {
                    return Response(record.State.ExitCode);
                }

                var pending = handle.NextExit.Task;
                var code = await pending.WaitAsync(ct);
                return Response(code);
            }
        }

        ContainerWaitResponse Response(int code)
        {
            var error = string.IsNullOrEmpty(record.State.Error)
                ? null
                : new ContainerWaitExitError { Message = record.State.Error };
            return new ContainerWaitResponse { StatusCode = code, Error = error };
        }
    }

    /// <summary><c>POST /containers/{id}/resize</c>; succeeds even when nothing is running yet.</summary>
    public async Task ResizeAsync(string idOrName, int cols, int rows, CancellationToken ct)
    {
        var record = Resolve(idOrName);
        var handle = GetHandle(record.Id);

        if (handle.Process is { } process)
        {
            try
            {
                await process.ResizeAsync(cols, rows, ct);
            }
            catch (Exception ex) when (ex is RuntimeException or IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "resize of container {Container} failed", record.Id);
            }
        }

        Publish(record, "resize", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["height"] = rows.ToString(CultureInfo.InvariantCulture),
            ["width"] = cols.ToString(CultureInfo.InvariantCulture),
        });
    }

    /// <summary><c>POST /containers/{id}/rename</c>.</summary>
    public Task RenameAsync(string idOrName, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var record = Resolve(idOrName);
        var trimmed = (newName ?? "").TrimStart('/');
        if (!Names.IsValidDockerName(trimmed))
        {
            throw DockerErrors.BadParameter(
                $"Invalid container name ({trimmed}), only [a-zA-Z0-9][a-zA-Z0-9_.-] are allowed");
        }

        lock (_nameGate)
        {
            var existing = FindByName(trimmed);
            if (existing is not null && !string.Equals(existing.Id, record.Id, StringComparison.Ordinal))
            {
                throw DockerErrors.ContainerNameConflict(trimmed, existing.Id);
            }

            var oldName = record.Name;
            record.Name = trimmed;
            Persist(record);
            Publish(record, "rename", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["oldName"] = oldName,
            });
        }

        return Task.CompletedTask;
    }

    /// <summary><c>POST /containers/{id}/update</c>: only the restart policy can be changed here.</summary>
    public Task UpdateAsync(string idOrName, ContainerUpdateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var record = Resolve(idOrName);

        if (request.Memory != 0 || request.NanoCpus != 0 || request.CpuShares != 0 ||
            request.CpuQuota != 0 || request.CpuPeriod != 0 || request.PidsLimit is > 0)
        {
            throw DockerErrors.NotImplemented("cider: updating container resources is not supported by Apple container");
        }

        if (request.RestartPolicy is { } policy)
        {
            record.RestartPolicy = policy;
            (record.Request.HostConfig ??= new HostConfig()).RestartPolicy = policy;
            Persist(record);
            Publish(record, "update");
        }

        return Task.CompletedTask;
    }

    /// <summary><c>POST /containers/prune</c>: removes stopped containers.</summary>

    private async Task PumpAsync(ContainerHandle handle, Stream source, StdStream stream)
    {
        var buffer = new byte[PumpBufferSize];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0)
                {
                    return;
                }

                var chunk = buffer.AsMemory(0, read).ToArray();

                var writer = handle.LogWriter;
                if (writer is not null)
                {
                    await writer.WriteAsync(stream, chunk, CancellationToken.None);
                }

                Broadcast(handle, stream, chunk);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The process went away underneath us; the exit handler takes it from here.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "log pump for container {Container} failed", handle.Id);
        }
    }

    private async Task HandleExitAsync(string id, ContainerHandle handle, IContainerProcess process)
    {
        var exitCode = -1;
        try
        {
            exitCode = await process.Exited;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "waiting for container {Container} to exit failed", id);
        }

        try
        {
            if (handle.Pumps.Count > 0)
            {
                await Task.WhenAll(handle.Pumps).WaitAsync(PumpDrainTimeout);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug("stdio pumps of container {Container} did not drain in time", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "stdio pumps of container {Container} failed", id);
        }

        if (handle.LogWriter is { } writer)
        {
            handle.LogWriter = null;
            try
            {
                await writer.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "closing the log writer of container {Container} failed", id);
            }
        }

        var record = _store.Get(id);
        if (record is not null)
        {
            record.State.Status = "exited";
            record.State.ExitCode = exitCode;
            record.State.FinishedAt = DateTimeOffset.UtcNow;
            record.State.Pid = 0;
            if (record.State.Health is { } health)
            {
                health.Status = "unhealthy";
            }

            Persist(record);
        }

        handle.Process = null;
        CompleteAttachments(handle);

        var pending = handle.NextExit;
        handle.NextExit = ContainerHandle.NewExit();
        pending.TrySetResult(exitCode);

        if (record is null)
        {
            return;
        }

        _names.Unregister(record.Id);
        UnpublishPorts(record.Id);
        Publish(record, "die", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
        });
        RaiseStateChanged(record, "die");

        if (record.AutoRemove)
        {
            // Detached on purpose: RemoveAsync takes the same gate a stop may still be holding.
            _ = Task.Run(async () =>
            {
                try
                {
                    await RemoveAsync(record.Id, force: true, removeVolumes: true, CancellationToken.None);
                }
                catch (Exception ex) when (ex is DockerApiException or RuntimeException)
                {
                    _logger.LogDebug(ex, "auto-removing container {Container} failed", record.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "auto-removing container {Container} failed", record.Id);
                }
            });
        }
    }

    private async Task WaitForExitHandlingAsync(ContainerHandle handle, int? timeoutSeconds)
    {
        var exitHandling = handle.ExitHandling;
        if (exitHandling is null)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds ?? 10, 1)) + TimeSpan.FromSeconds(10);
        try
        {
            await exitHandling.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("container {Container} did not finish exiting within {Timeout}", handle.Id, timeout);
        }
    }

    private static bool IsTerminating(string signal) =>
        signal is "SIGKILL" or "SIGTERM" or "SIGINT" or "SIGQUIT" or "SIGSTOP" or "KILL" or "TERM" or "INT" or "QUIT";

    private static string NormalizeSignal(string signal)
    {
        var trimmed = signal.Trim();
        if (trimmed.Length == 0)
        {
            return "SIGKILL";
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return trimmed;
        }

        return trimmed.StartsWith("SIG", StringComparison.OrdinalIgnoreCase)
            ? trimmed.ToUpperInvariant()
            : "SIG" + trimmed.ToUpperInvariant();
    }

    private static bool IsHealthcheckDisabled(HealthConfig config) =>
        config.Test.Count > 0 && string.Equals(config.Test[0], "NONE", StringComparison.Ordinal);
}
